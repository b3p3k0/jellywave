using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card R2a: the station answer cache. Keyed by station key (id and origin) and item; bounded by entries in all, entries
/// per station and total bytes; concurrent misses share one fetch, admitted once; a refusal goes to its caller only.
/// </summary>
public class StationCacheTests
{
    private static readonly StationKey One = StationKey.Of("one", new Uri("http://one.example/"));
    private static readonly StationKey OneMoved = StationKey.Of("one", new Uri("http://moved.example/"));
    private static readonly StationKey Two = StationKey.Of("two", new Uri("http://two.example/"));
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(1);

    private sealed class Probe
    {
        public int Admitted;

        public int Fetched;

        public CancellationToken LastToken;
    }

    private static StationCache<string> Cache(UserBudgetsTests.ManualClock clock, int maxEntries = 100, int maxPerStation = 100, long maxBytes = 1_000_000)
        => new(clock, maxEntries, maxPerStation, maxBytes, call => call.Value?.Length ?? 0);

    private static Task<StationCall<string>> Get(StationCache<string> cache, StationKey station, string item, Probe probe, Func<CancellationToken, Task<string>>? fetch = null, TimeSpan? life = null, CancellationToken cancellationToken = default)
        => cache.GetAsync(
            new StationCacheKey(station, item),
            () =>
            {
                Interlocked.Increment(ref probe.Admitted);
                Func<CancellationToken, Task<StationCall<string>>> run = async ct =>
                {
                    Interlocked.Increment(ref probe.Fetched);
                    probe.LastToken = ct;
                    return StationCall<string>.Of(fetch is null ? item + "@" + station.Origin : await fetch(ct));
                };
                return (null, run);
            },
            _ => life ?? Life,
            cancellationToken);

    [Fact]
    public async Task A_kept_answer_is_served_until_its_lifetime_ends()
    {
        var clock = new UserBudgetsTests.ManualClock();
        var cache = Cache(clock);
        var probe = new Probe();

        Assert.Equal("a@http://one.example/", (await Get(cache, One, "a", probe)).Value);
        clock.Now += Life - TimeSpan.FromSeconds(1);
        Assert.Equal("a@http://one.example/", (await Get(cache, One, "a", probe)).Value);
        Assert.Equal(1, probe.Fetched);

        clock.Now += TimeSpan.FromSeconds(1);
        await Get(cache, One, "a", probe);
        Assert.Equal(2, probe.Fetched);
        Assert.Equal(2, probe.Admitted);
    }

    [Fact]
    public async Task The_key_holds_the_station_s_origin_so_a_moved_station_is_asked_afresh()
    {
        var cache = Cache(new UserBudgetsTests.ManualClock());
        var probe = new Probe();

        await Get(cache, One, "a", probe);
        Assert.Equal("a@http://moved.example/", (await Get(cache, OneMoved, "a", probe)).Value);
        Assert.Equal("a@http://two.example/", (await Get(cache, Two, "a", probe)).Value);
        Assert.Equal(3, probe.Fetched);
    }

    [Fact]
    public async Task Concurrent_misses_share_one_admitted_fetch()
    {
        var cache = Cache(new UserBudgetsTests.ManualClock());
        var probe = new Probe();
        var release = new TaskCompletionSource<string>();

        var waiters = Enumerable.Range(0, 20).Select(_ => Get(cache, One, "a", probe, _ => release.Task)).ToList();
        release.SetResult("shared");
        var answers = await Task.WhenAll(waiters);

        Assert.All(answers, a => Assert.Equal("shared", a.Value));
        Assert.Equal(1, probe.Admitted);
        Assert.Equal(1, probe.Fetched);
    }

    [Fact]
    public async Task A_refusal_goes_to_its_caller_only_and_is_never_kept()
    {
        var cache = Cache(new UserBudgetsTests.ManualClock());
        var refusals = 0;
        var refused = await cache.GetAsync(
            new StationCacheKey(One, "a"),
            () =>
            {
                refusals++;
                return (StationCall<string>.Refuse(429, TimeSpan.FromSeconds(9)), null);
            },
            _ => Life,
            CancellationToken.None);

        Assert.Equal(429, refused.Refusal);
        Assert.Equal(0, cache.Count);
        var probe = new Probe();
        Assert.Equal(0, (await Get(cache, One, "a", probe)).Refusal);
        Assert.Equal(1, probe.Admitted);
    }

    [Fact]
    public async Task A_waiter_that_gives_up_leaves_the_fetch_to_the_others_and_the_last_one_cancels_it()
    {
        var cache = Cache(new UserBudgetsTests.ManualClock());
        var probe = new Probe();
        var release = new TaskCompletionSource<string>();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        Func<CancellationToken, Task<string>> fetch = ct => release.Task.WaitAsync(ct);

        var a = Get(cache, One, "a", probe, fetch, cancellationToken: first.Token);
        var b = Get(cache, One, "a", probe, fetch, cancellationToken: second.Token);
        await first.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        Assert.False(probe.LastToken.IsCancellationRequested);

        await second.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
        Assert.True(probe.LastToken.IsCancellationRequested);   // nobody waits: the station call is cancelled
        Assert.Equal(0, cache.Count);

        // The next caller starts afresh.
        Assert.Equal("a@http://one.example/", (await Get(cache, One, "a", probe)).Value);
        Assert.Equal(2, probe.Fetched);
    }

    [Fact]
    public async Task A_failing_fetch_fails_every_waiter_and_keeps_nothing()
    {
        var cache = Cache(new UserBudgetsTests.ManualClock());
        var probe = new Probe();
        var release = new TaskCompletionSource<string>();
        var waiters = Enumerable.Range(0, 3).Select(_ => Get(cache, One, "a", probe, _ => release.Task)).ToList();
        release.SetException(new InvalidOperationException("boom"));

        foreach (var waiter in waiters)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => waiter);
        }

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Bounded_by_entries_in_all_and_per_station_oldest_first()
    {
        var cache = Cache(new UserBudgetsTests.ManualClock(), maxEntries: 3, maxPerStation: 2);
        var probe = new Probe();
        await Get(cache, One, "a", probe);
        await Get(cache, One, "b", probe);
        await Get(cache, One, "c", probe);   // a goes: two per station
        Assert.Equal(2, cache.Count);
        await Get(cache, Two, "x", probe);
        await Get(cache, Two, "y", probe);   // b goes: three in all
        Assert.Equal(3, cache.Count);

        var before = probe.Fetched;
        await Get(cache, One, "c", probe);
        await Get(cache, Two, "x", probe);
        await Get(cache, Two, "y", probe);
        Assert.Equal(before, probe.Fetched);
        await Get(cache, One, "a", probe);
        await Get(cache, One, "b", probe);
        Assert.Equal(before + 2, probe.Fetched);
    }

    [Fact]
    public async Task Bounded_by_total_bytes_and_an_answer_over_the_budget_is_not_kept()
    {
        var cache = Cache(new UserBudgetsTests.ManualClock(), maxBytes: 100);
        var probe = new Probe();
        await Get(cache, One, "a", probe, _ => Task.FromResult(new string('a', 40)));
        await Get(cache, One, "b", probe, _ => Task.FromResult(new string('b', 40)));
        await Get(cache, Two, "c", probe, _ => Task.FromResult(new string('c', 40)));   // a goes

        Assert.Equal(2, cache.Count);
        Assert.Equal(80, cache.Bytes);

        await Get(cache, Two, "big", probe, _ => Task.FromResult(new string('z', 101)));
        Assert.Equal(2, cache.Count);
        Assert.Equal(80, cache.Bytes);
    }

    [Fact]
    public async Task Short_lived_entries_go_before_long_lived_ones()
    {
        // Fix round: error entries (15 s) are evicted before kept lines (10 min), so made-up ids cannot push lines out.
        var cache = Cache(new UserBudgetsTests.ManualClock(), maxPerStation: 2);
        var probe = new Probe();
        await Get(cache, One, "lines", probe, life: TimeSpan.FromMinutes(10));
        for (var i = 0; i < 10; i++)
        {
            await Get(cache, One, "error" + i, probe, life: TimeSpan.FromSeconds(15));
        }

        var before = probe.Fetched;
        await Get(cache, One, "lines", probe);
        Assert.Equal(before, probe.Fetched);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task A_zero_lifetime_keeps_nothing()
    {
        var cache = Cache(new UserBudgetsTests.ManualClock());
        var probe = new Probe();
        await Get(cache, One, "a", probe, life: TimeSpan.Zero);
        await Get(cache, One, "a", probe, life: TimeSpan.Zero);
        Assert.Equal(2, probe.Fetched);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Invalidate_drops_the_answer_and_a_fetch_already_in_flight_does_not_put_it_back()
    {
        var cache = Cache(new UserBudgetsTests.ManualClock());
        var probe = new Probe();
        await Get(cache, One, "a", probe);
        cache.Invalidate(new StationCacheKey(One, "a"));
        Assert.Equal(0, cache.Count);

        var release = new TaskCompletionSource<string>();
        var inFlight = Get(cache, One, "a", probe, _ => release.Task);
        cache.Invalidate(new StationCacheKey(One, "a"));
        release.SetResult("before the like");
        Assert.Equal("before the like", (await inFlight).Value);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Drop_others_keeps_only_the_named_item_of_that_station()
    {
        var cache = Cache(new UserBudgetsTests.ManualClock());
        var probe = new Probe();
        await Get(cache, One, "old", probe);
        await Get(cache, One, "new", probe);
        await Get(cache, Two, "old", probe);

        cache.DropOthers(new StationCacheKey(One, "new"));

        Assert.Equal(2, cache.Count);
        var before = probe.Fetched;
        await Get(cache, One, "new", probe);
        await Get(cache, Two, "old", probe);
        Assert.Equal(before, probe.Fetched);
    }
}
