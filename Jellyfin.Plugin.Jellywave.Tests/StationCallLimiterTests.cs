using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card R2a: the shared limiter for user-driven station calls. At most four in flight per station, sixteen in all and four
/// portraits (fix round); a call over any is refused at once, never queued; a permit is given back once, whatever ended
/// the call.
/// </summary>
public class StationCallLimiterTests
{
    private const string One = "http://one.example/";
    private const string Two = "http://two.example/";

    private const int PerStation = StationCallLimiter.MaxPerStation;

    [Fact]
    public void Four_per_station_then_refused_at_once_while_another_station_still_gets_its_own()
    {
        var limiter = new StationCallLimiter();
        var held = Enumerable.Range(0, PerStation).Select(_ => limiter.TryEnter(One)).ToList();

        Assert.Equal(4, PerStation);
        Assert.All(held, Assert.NotNull);
        Assert.Null(limiter.TryEnter(One));
        Assert.NotNull(limiter.TryEnter(Two));
        Assert.Equal(PerStation + 1, limiter.InUse);

        held[0]!.Dispose();
        Assert.NotNull(limiter.TryEnter(One));
    }

    [Fact]
    public void Sixteen_in_all_then_refused_even_for_a_station_with_room()
    {
        var limiter = new StationCallLimiter();
        var held = Enumerable.Range(0, StationCallLimiter.MaxTotal / PerStation)
            .SelectMany(i => Enumerable.Range(0, PerStation).Select(_ => limiter.TryEnter("http://s" + i + ".example/"))).ToList();

        Assert.Equal(16, StationCallLimiter.MaxTotal);
        Assert.All(held, Assert.NotNull);
        Assert.Equal(StationCallLimiter.MaxTotal, limiter.InUse);
        Assert.Null(limiter.TryEnter(One));

        held[0]!.Dispose();
        Assert.NotNull(limiter.TryEnter(One));
    }

    [Fact]
    public void At_most_four_portraits_in_all_within_the_total()
    {
        var limiter = new StationCallLimiter();
        var portraits = Enumerable.Range(0, StationCallLimiter.MaxPortraits).Select(i => limiter.TryEnter("http://p" + i + ".example/", portrait: true)).ToList();

        Assert.Equal(4, StationCallLimiter.MaxPortraits);
        Assert.All(portraits, Assert.NotNull);
        Assert.Null(limiter.TryEnter(One, portrait: true));
        Assert.NotNull(limiter.TryEnter(One));   // other calls still go
        Assert.Equal(StationCallLimiter.MaxPortraits, limiter.PortraitsInUse);

        portraits[0]!.Dispose();
        portraits[0]!.Dispose();
        Assert.Equal(StationCallLimiter.MaxPortraits - 1, limiter.PortraitsInUse);
        Assert.NotNull(limiter.TryEnter(One, portrait: true));
    }

    [Fact]
    public void Callers_stuck_on_one_station_hold_at_most_its_four_slots_of_the_total()
    {
        // A slow station: every caller of it after the fourth is refused without taking a slot of the total, so the
        // other stations keep twelve.
        var limiter = new StationCallLimiter();
        for (var i = 0; i < PerStation; i++)
        {
            Assert.NotNull(limiter.TryEnter(One));
        }

        for (var i = 0; i < 50; i++)
        {
            Assert.Null(limiter.TryEnter(One));
        }

        Assert.Equal(PerStation, limiter.InUse);
        Assert.Equal(StationCallLimiter.MaxTotal - PerStation, Enumerable.Range(0, 40).Count(i => limiter.TryEnter("http://other" + (i % 5) + ".example/") is not null));
    }

    [Fact]
    public void A_permit_is_given_back_once_and_the_map_empties()
    {
        var limiter = new StationCallLimiter();
        var permit = limiter.TryEnter(One)!;
        var other = limiter.TryEnter(One)!;

        permit.Dispose();
        permit.Dispose();

        Assert.Equal(1, limiter.InUse);
        other.Dispose();
        Assert.Equal(0, limiter.InUse);
        Assert.Equal(0, limiter.StationsInUse);
    }

    [Fact]
    public void Concurrent_callers_never_exceed_the_limits()
    {
        var limiter = new StationCallLimiter();
        var peak = 0;
        var gate = new object();
        Parallel.For(0, 4000, i =>
        {
            using var permit = limiter.TryEnter(i % 2 == 0 ? One : "http://s" + (i % 7) + ".example/", portrait: i % 3 == 0);
            lock (gate)
            {
                peak = System.Math.Max(peak, limiter.InUse);
            }
        });

        Assert.InRange(peak, 1, StationCallLimiter.MaxTotal);
        Assert.Equal(0, limiter.InUse);
        Assert.Equal(0, limiter.PortraitsInUse);
        Assert.Equal(0, limiter.StationsInUse);
    }
}
