using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L14: a SUB/WAVE station is asked only while someone needs it (a session plays its item, paused included, or a
/// signed-in user had its station page open in the last minute), with a short grace after the last listener. An idle
/// station is not asked at all, its item shows the station's own look, and that is not a failure. A station that becomes
/// needed wakes the poller, and the woken poll asks only it. "Reload status" asks every station once, on purpose.
/// </summary>
public class ListenerDemandTests
{
    private const string Tile = "Live Radio";

    /// <summary>The loop tests' longest wait for the regular polls (real time; a passing run takes about 2 s).</summary>
    private static readonly TimeSpan LoopDeadline = TimeSpan.FromSeconds(30);

    /// <summary>The wake test's longest wait: under the 30 real seconds of its poll interval on the fast clock.</summary>
    private static readonly TimeSpan WakeDeadline = TimeSpan.FromSeconds(20);

    /// <summary>Answers per host and counts every request. Thread-safe: the poller fetches stations at once.</summary>
    private sealed class Stations : HttpMessageHandler
    {
        private readonly object _gate = new();

        public Dictionary<string, string> NowPlaying { get; } = new();

        public HashSet<string> Down { get; } = new();

        public List<string> Seen { get; } = new();

        /// <summary>Gets or sets what every now-playing request awaits first (to hold a request in flight).</summary>
        public Func<Task>? BeforeNowPlaying { get; set; }

        /// <summary>One station's now-playing requests, read under the lock (the poll loop runs on another thread).</summary>
        public int NowPlayingTo(string id)
        {
            lock (_gate)
            {
                return Seen.Count(s => s == id + ".example/api/now-playing");
            }
        }

        /// <summary>Every request to one station's host.</summary>
        public int To(string id)
        {
            lock (_gate)
            {
                return Seen.Count(s => s.StartsWith(id + ".example/", StringComparison.Ordinal));
            }
        }

        public void Forget()
        {
            lock (_gate)
            {
                Seen.Clear();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            var path = request.RequestUri.AbsolutePath;
            lock (_gate)
            {
                Seen.Add(host + path);
            }

            if (path == "/api/now-playing" && BeforeNowPlaying is { } hook)
            {
                await hook();
            }

            if (Down.Contains(host))
            {
                throw new HttpRequestException("connection refused");
            }

            var body = path == "/api/now-playing" ? NowPlaying.GetValueOrDefault(host) : null;
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class RecordingLogger : ILogger<NowPlayingService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }

        public List<string> At(LogLevel level)
        {
            lock (Entries)
            {
                return Entries.Where(e => e.Level == level).Select(e => e.Message).ToList();
            }
        }
    }

    /// <summary>A clock whose timestamps move only when a test moves them.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    }

    /// <summary>
    /// The real clock, ten times as fast, for the poll loop's tests: the poll interval runs on this clock, the loop's
    /// shortest wait between polls (<c>WakeGap</c>, 1 s) on the real one, so a 5 s interval is up long before the gap ends.
    /// </summary>
    private sealed class FastClock : TimeProvider
    {
        private const int Speed = 10;
        private readonly long _start = Stopwatch.GetTimestamp();

        public override long TimestampFrequency => Stopwatch.Frequency;

        public override long GetTimestamp() => (Stopwatch.GetTimestamp() - _start) * Speed;
    }

    /// <summary>A Jellyfin host whose startup has completed, so the poll loop starts at once; anything else throws.</summary>
    public class StartedHost : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_" + nameof(IServerApplicationHost.CoreStartupHasCompleted) ? true : throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class Server
    {
        public Server(params string[] ids)
            : this(null, ids)
        {
        }

        /// <summary>A server whose poller runs on <paramref name="loopClock"/> and can start its loop (null: the manual clock, no loop).</summary>
        public Server(TimeProvider? loopClock, params string[] ids)
        {
            var channel = new Channel { Id = Library.IdFor("Channel " + Tile, typeof(Channel)), Name = Tile };
            Library.Items[channel.Id] = channel;
            Client = new StationClient(new StubFactory(Http));
            Artwork = new LiveItemArtwork(Client, SaveImageAsync, NullLogger<LiveItemArtwork>.Instance);
            var clock = loopClock ?? Clock;
            Demand = new StationDemand(Playing, clock, () => TileGate());
            Poller = new NowPlayingService(
                Client,
                State,
                Artwork,
                new StationLogos(Client, SaveImageAsync, NullLogger<StationLogos>.Instance, TimeProvider.System),
                new LiveItemFinder(Library.Manager, () => ChannelLookupFails ? throw new InvalidOperationException("database is locked") : OtherChannels, NullLogger<LiveItemFinder>.Instance),
                Health,
                new ScheduleCache(Client),
                Library.Manager,
                loopClock is null ? null! : DispatchProxy.Create<IServerApplicationHost, StartedHost>(),
                Log,
                () => Config,
                clock,
                Demand);
            foreach (var id in ids.Length == 0 ? new[] { "a", "b" } : ids)
            {
                Add(id);
            }
        }

        public FakeLibrary Library { get; } = FakeLibrary.Create();

        public Stations Http { get; } = new();

        public StationClient Client { get; }

        public FakePlayingItems Playing { get; } = new();

        /// <summary>Gets or sets the tile gate for the sessions' users (default: everyone sees the tile).</summary>
        public Func<Func<Guid, bool>> TileGate { get; set; } = () => _ => true;

        /// <summary>Gets the other plugins' channels (one named like the tile is a name collision).</summary>
        public List<IChannel> OtherChannels { get; } = new();

        /// <summary>Gets or sets a value indicating whether resolving the tile's channel throws (a library lock).</summary>
        public bool ChannelLookupFails { get; set; }

        public ManualClock Clock { get; } = new();

        public StationDemand Demand { get; }

        public NowPlayingState State { get; } = new();

        public PollerHealth Health { get; } = new();

        public RecordingLogger Log { get; } = new();

        public LiveItemArtwork Artwork { get; }

        public NowPlayingService Poller { get; }

        public PluginConfiguration Config { get; } = new() { LibraryName = Tile };

        public Dictionary<string, Audio> Items { get; } = new();

        public void Add(string id)
        {
            Config.Stations.Add(new StationDefinition { Id = id, Name = "Radio " + id.ToUpperInvariant(), BaseUrl = "http://" + id + ".example", SortOrder = Config.Stations.Count });
            Http.NowPlaying[id + ".example"] = "{\"nowPlaying\":{\"artist\":\"Artist " + id + "\",\"title\":\"Song " + id + "\"},\"streamOnline\":true}";
            var item = new Audio { Id = Guid.NewGuid(), ExternalId = id };
            Items[id] = item;
            Library.ListedItems.Add(item);
        }

        public StationKey Key(string id) => StationKey.Of(id, new Uri("http://" + id + ".example/"));

        public string? Overview(string id) => Items[id].Overview;

        /// <summary>The station's now-playing requests (the feed the poller asks every interval).</summary>
        public int Asked(string id) => Http.NowPlayingTo(id);

        public void Play(string id) => Playing.Ids.Add(Items[id].Id);

        public void Stop(string id) => Playing.Ids.Remove(Items[id].Id);

        public Task PollAsync() => Poller.PollOnceAsync(CancellationToken.None);

        public Task WokenPollAsync() => Poller.PollOnceAsync(true, CancellationToken.None);

        public bool Woken() => Demand.WaitForWakeAsync(TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult();

        private Task SaveImageAsync(BaseItem item, Stream stream, string mime, CancellationToken cancellationToken)
        {
            item.SetImagePath(ImageType.Primary, "http://saved.example/" + Guid.NewGuid().ToString("N"));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void The_windows_are_the_card_s()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), StationDemand.PageWindow);
        Assert.Equal(TimeSpan.FromSeconds(30), StationDemand.ListenerGrace);
    }

    [Fact]
    public async Task With_nobody_listening_the_first_poll_after_startup_asks_no_station()
    {
        var server = new Server();

        await server.PollAsync();
        await server.PollAsync();

        Assert.Empty(server.Http.Seen);
        Assert.All(new[] { "a", "b" }, id =>
        {
            var health = server.Health.Snapshot(server.Key(id));
            Assert.NotNull(health.IdleSinceUtc);
            Assert.Null(health.Reachable);   // not a failure: never asked
            Assert.Null(health.LastError);
            Assert.Equal(JellywaveChannel.DefaultOverview, server.Overview(id));
            Assert.Equal(LiveItemArtwork.LogoKey, server.Artwork.Applied(id));
        });
        Assert.Empty(server.Log.At(LogLevel.Warning));
        Assert.Empty(server.Log.At(LogLevel.Error));
        Assert.DoesNotContain(server.Log.At(LogLevel.Information), l => l.Contains("idle", StringComparison.Ordinal));   // idle from the start: no line
    }

    [Fact]
    public async Task A_played_station_is_asked_and_the_one_nobody_plays_is_not()
    {
        var server = new Server("a", "b", "c");
        server.Play("a");

        await server.PollAsync();
        await server.PollAsync();

        Assert.Equal(2, server.Asked("a"));
        Assert.Equal(0, server.Http.To("b"));
        Assert.Equal(0, server.Http.To("c"));
        Assert.StartsWith("Now playing: Artist a – Song a", server.Overview("a"), StringComparison.Ordinal);
        Assert.Equal(JellywaveChannel.DefaultOverview, server.Overview("b"));
        Assert.Equal(true, server.Health.Snapshot(server.Key("a")).Reachable);
        Assert.Null(server.Health.Snapshot(server.Key("a")).IdleSinceUtc);
        Assert.NotNull(server.Health.Snapshot(server.Key("b")).IdleSinceUtc);
        Assert.Contains(server.Log.At(LogLevel.Information), l => l == "Station Radio A is in use (someone plays it or has its page open); asking it for now playing");
    }

    [Fact]
    public async Task An_open_station_page_keeps_the_station_asked_for_60_seconds_then_it_goes_idle()
    {
        var server = new Server();
        server.Demand.PageOpened("b");

        await server.PollAsync();
        Assert.Equal(1, server.Asked("b"));
        Assert.Equal(0, server.Http.To("a"));

        server.Clock.Advance(TimeSpan.FromSeconds(60));
        await server.PollAsync();
        Assert.Equal(2, server.Asked("b"));   // exactly 60 s: still open

        server.Clock.Advance(TimeSpan.FromSeconds(1));
        server.Http.Forget();
        await server.PollAsync();
        await server.PollAsync();

        Assert.Empty(server.Http.Seen);
        Assert.Equal(JellywaveChannel.DefaultOverview, server.Overview("b"));
        Assert.Equal(LiveItemArtwork.LogoKey, server.Artwork.Applied("b"));
        Assert.Null(server.State.Snapshot(server.Key("b")).Feed);
        var health = server.Health.Snapshot(server.Key("b"));
        Assert.Equal((true, (string?)null, (DateTime?)null), (health.Reachable, health.LastError, health.LastErrorUtc));   // idle is no failure
        Assert.NotNull(health.IdleSinceUtc);
        Assert.Empty(server.Log.At(LogLevel.Warning));
        Assert.Contains(server.Log.At(LogLevel.Information), l => l == "Station Radio B is idle (nobody plays it or has its page open); not asked until someone does");
    }

    [Fact]
    public async Task A_listener_who_stops_keeps_the_station_asked_for_the_grace_only()
    {
        var server = new Server();
        server.Play("a");
        await server.PollAsync();
        server.Stop("a");

        server.Clock.Advance(TimeSpan.FromSeconds(20));
        await server.PollAsync();
        Assert.Equal(2, server.Asked("a"));   // 20 s after the last listener: within the grace

        server.Clock.Advance(TimeSpan.FromSeconds(11));
        await server.PollAsync();
        Assert.Equal(2, server.Asked("a"));   // 31 s: idle
        Assert.Equal(JellywaveChannel.DefaultOverview, server.Overview("a"));
    }

    [Fact]
    public async Task Paused_counts_as_playing_for_as_long_as_the_session_keeps_the_item()
    {
        // SessionPlayingItems lists a paused session's item (L6); here the item simply stays in the list.
        var server = new Server();
        server.Play("a");
        for (var i = 0; i < 5; i++)
        {
            await server.PollAsync();
            server.Clock.Advance(TimeSpan.FromMinutes(10));
        }

        Assert.Equal(5, server.Asked("a"));
    }

    [Fact]
    public async Task A_failing_item_lookup_does_not_make_a_played_station_idle()
    {
        var server = new Server();
        server.Play("a");
        await server.PollAsync();
        server.Library.OnQuery = _ => throw new InvalidOperationException("database is locked");

        server.Clock.Advance(TimeSpan.FromSeconds(45));   // past the grace: only the last lookup that worked keeps it
        await server.PollAsync();   // the write phase's lookups fail too; that is logged per item

        Assert.Equal(2, server.Asked("a"));
        Assert.Equal(0, server.Http.To("b"));
    }

    [Fact]
    public async Task An_idle_station_s_page_or_playback_start_wakes_the_poller_and_a_needed_one_s_does_not()
    {
        var server = new Server("a", "b", "c");
        server.Play("c");
        await server.PollAsync();   // a and b idle, c needed
        Assert.False(server.Woken());

        server.Demand.PageOpened("c");
        server.Playing.Start(server.Items["c"].Id, "c");
        server.Playing.Start(Guid.NewGuid(), "some-other-item");
        server.Playing.Start(Guid.NewGuid(), null);
        Assert.False(server.Woken());

        server.Demand.PageOpened("a");
        Assert.True(server.Woken());
        server.Demand.PageOpened("a");   // already woken for: one wake per idle spell
        Assert.False(server.Woken());

        server.Playing.Start(server.Items["b"].Id, "b");
        Assert.True(server.Woken());
    }

    [Fact]
    public async Task Wakes_that_come_while_nobody_waits_are_kept_as_one()
    {
        var server = new Server();
        await server.PollAsync();

        server.Demand.PageOpened("a");
        server.Demand.PageOpened("b");

        Assert.True(server.Woken());
        Assert.False(server.Woken());
    }

    [Fact]
    public async Task The_woken_poll_asks_only_the_station_that_just_became_needed()
    {
        var server = new Server("a", "b", "c");
        server.Play("a");
        await server.PollAsync();
        server.Http.Forget();

        server.Clock.Advance(TimeSpan.FromSeconds(5));
        server.Playing.Start(server.Items["b"].Id, "b");
        Assert.True(server.Woken());
        await server.WokenPollAsync();

        Assert.Equal(1, server.Asked("b"));
        Assert.Equal(0, server.Http.To("a"));   // asked 5 s ago; keeps its place in the interval
        Assert.Equal(0, server.Http.To("c"));
        Assert.StartsWith("Now playing: Artist b – Song b", server.Overview("b"), StringComparison.Ordinal);
        Assert.StartsWith("Now playing: Artist a – Song a", server.Overview("a"), StringComparison.Ordinal);

        server.Clock.Advance(TimeSpan.FromSeconds(15));
        await server.PollAsync();   // the interval's poll asks every needed station
        Assert.Equal((1, 2), (server.Asked("a"), server.Asked("b")));
    }

    [Fact]
    public async Task A_station_that_went_idle_is_asked_at_once_when_it_is_needed_again()
    {
        var server = new Server();
        server.Play("a");
        await server.PollAsync();
        server.Stop("a");
        server.Clock.Advance(TimeSpan.FromSeconds(31));
        await server.PollAsync();
        Assert.Equal(JellywaveChannel.DefaultOverview, server.Overview("a"));

        server.Playing.Start(server.Items["a"].Id, "a");
        Assert.True(server.Woken());
        await server.WokenPollAsync();

        Assert.Equal(2, server.Asked("a"));
        Assert.StartsWith("Now playing: Artist a – Song a", server.Overview("a"), StringComparison.Ordinal);
        Assert.Null(server.Health.Snapshot(server.Key("a")).IdleSinceUtc);
    }

    [Fact]
    public async Task A_failing_needed_station_still_fails_while_an_idle_one_never_does()
    {
        var server = new Server();
        server.Play("a");
        server.Http.Down.Add("a.example");
        server.Http.Down.Add("b.example");

        await server.PollAsync();

        Assert.Equal((false, "could not connect"), (server.Health.Snapshot(server.Key("a")).Reachable, server.Health.Snapshot(server.Key("a")).LastError));
        Assert.Null(server.Health.Snapshot(server.Key("b")).Reachable);
        Assert.Single(server.Log.At(LogLevel.Warning));   // a's streak only
    }

    [Fact]
    public async Task With_now_playing_off_no_station_is_asked_even_while_played()
    {
        var server = new Server();
        server.Config.NowPlayingEnabled = false;
        server.Play("a");
        server.Demand.PageOpened("b");

        await server.PollAsync();

        Assert.Empty(server.Http.Seen);
    }

    [Fact]
    public async Task The_demand_forgets_stations_that_are_no_longer_listed()
    {
        var clock = new ManualClock();
        var demand = new StationDemand(new FakePlayingItems(), clock);
        demand.PageOpened("gone");
        demand.Decide(["gone", "kept"], new HashSet<string>());

        demand.Decide(["kept"], new HashSet<string>());
        var again = demand.Decide(["gone", "kept"], new HashSet<string>());

        Assert.Empty(again);   // "gone" came back without its page
        demand.PageOpened("gone");
        Assert.True(await demand.WaitForWakeAsync(TimeSpan.Zero, CancellationToken.None));
    }

    // ---- Reload status: every station asked once, on purpose ----

    [Fact]
    public async Task Reload_status_asks_every_listed_station_once_and_leaves_an_idle_station_idle()
    {
        var server = new Server("a", "b", "c");
        server.Http.Down.Add("c.example");
        await server.PollAsync();   // all idle
        var check = new StationCheck(server.Client, server.Health, NullLogger<StationCheck>.Instance);

        var asked = await check.CheckAsync(server.Config, CancellationToken.None);

        Assert.Equal(3, asked);
        Assert.All(new[] { "a", "b", "c" }, id => Assert.Equal(1, server.Http.To(id)));
        var a = server.Health.Snapshot(server.Key("a"));
        Assert.Equal(true, a.Reachable);
        Assert.NotNull(a.LastSuccessUtc);
        Assert.NotNull(a.IdleSinceUtc);   // still idle: the page shows the check beside it
        var c = server.Health.Snapshot(server.Key("c"));
        Assert.Equal((false, "could not connect"), (c.Reachable, c.LastError));
        Assert.NotNull(c.IdleSinceUtc);
        Assert.Null(server.State.Snapshot(server.Key("a")).Feed);   // no state, no item change
        Assert.Equal(JellywaveChannel.DefaultOverview, server.Overview("a"));

        server.Http.Forget();
        await server.PollAsync();
        Assert.Empty(server.Http.Seen);   // the poller still leaves them alone
        Assert.Equal(true, server.Health.Snapshot(server.Key("a")).Reachable);
    }

    [Fact]
    public async Task Reload_status_asks_nothing_with_now_playing_off_or_while_a_check_runs()
    {
        var server = new Server();
        var check = new StationCheck(server.Client, server.Health, NullLogger<StationCheck>.Instance, server.Clock);
        server.Config.NowPlayingEnabled = false;
        Assert.Equal(0, await check.CheckAsync(server.Config, CancellationToken.None));
        Assert.Equal(0, await check.CheckAsync(null, CancellationToken.None));
        Assert.Empty(server.Http.Seen);

        server.Config.NowPlayingEnabled = true;
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        server.Http.BeforeNowPlaying = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var first = check.CheckAsync(server.Config, CancellationToken.None);
        await entered.Task;

        // Bounded, so a check that waited for the first one fails here instead of hanging the run.
        Assert.Equal(0, await check.CheckAsync(server.Config, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        release.SetResult();
        Assert.Equal(2, await first);
        Assert.Equal(2, server.Http.Seen.Count);
        server.Clock.Advance(StationCheck.Cooldown);
        Assert.Equal(2, await check.CheckAsync(server.Config, CancellationToken.None));   // free again
    }

    [Fact]
    public async Task Reload_status_asks_nothing_within_10_seconds_of_the_last_check_and_answers_with_its_results()
    {
        var server = new Server();
        await server.PollAsync();
        var check = new StationCheck(server.Client, server.Health, NullLogger<StationCheck>.Instance, server.Clock);
        var controller = new JellywaveController(server.Client, null!, server.State, server.Health, server.Artwork, null!, new LiveItemFinder(server.Library.Manager, () => [], NullLogger<LiveItemFinder>.Instance), NullLogger<JellywaveController>.Instance, () => server.Config, new StationTitles(), check);
        Assert.Equal(TimeSpan.FromSeconds(10), StationCheck.Cooldown);

        var first = (await controller.CheckHealth(CancellationToken.None)).Value!.Stations.Single(s => s.Id == "a");
        server.Clock.Advance(TimeSpan.FromSeconds(9.9));
        var second = (await controller.CheckHealth(CancellationToken.None)).Value!.Stations.Single(s => s.Id == "a");

        Assert.Equal(2, server.Http.Seen.Count);   // the first press only: one feed per station
        Assert.Equal(true, second.Reachable);
        Assert.Equal(first.LastSuccessUtc, second.LastSuccessUtc);   // the last check's results

        server.Clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(2, await check.CheckAsync(server.Config, CancellationToken.None));
        Assert.Equal(4, server.Http.Seen.Count);

        // The 10 s count from the end of a check, not its start: a slow check (8 s) still leaves the full spacing.
        server.Clock.Advance(StationCheck.Cooldown);
        server.Http.BeforeNowPlaying = () =>
        {
            server.Clock.Advance(TimeSpan.FromSeconds(4));
            return Task.CompletedTask;
        };
        Assert.Equal(2, await check.CheckAsync(server.Config, CancellationToken.None));
        server.Http.BeforeNowPlaying = null;
        server.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await check.CheckAsync(server.Config, CancellationToken.None));
        Assert.Equal(6, server.Http.Seen.Count);
    }

    [Fact]
    public async Task The_health_route_after_reload_status_shows_idle_and_the_check()
    {
        var server = new Server();
        await server.PollAsync();
        var check = new StationCheck(server.Client, server.Health, NullLogger<StationCheck>.Instance);
        var controller = new JellywaveController(server.Client, null!, server.State, server.Health, server.Artwork, null!, new LiveItemFinder(server.Library.Manager, () => [], NullLogger<LiveItemFinder>.Instance), NullLogger<JellywaveController>.Instance, () => server.Config, new StationTitles(), check);

        var before = controller.Health().Value!.Stations.Single(s => s.Id == "a");
        var after = (await controller.CheckHealth(CancellationToken.None)).Value!.Stations.Single(s => s.Id == "a");

        Assert.NotNull(before.IdleSinceUtc);
        Assert.Null(before.Reachable);
        Assert.Equal(before.IdleSinceUtc, after.IdleSinceUtc);
        Assert.Equal(true, after.Reachable);
        Assert.True(after.LastSuccessUtc >= after.IdleSinceUtc);
        Assert.Equal(1, server.Asked("a"));
    }

    // ---- The open page: the station route filter ----

    [Theory]
    [InlineData(nameof(JellywaveWebController.Feed))]
    [InlineData(nameof(JellywaveWebController.Schedule))]
    [InlineData(nameof(JellywaveWebController.LikeStatus))]
    [InlineData(nameof(JellywaveWebController.Lyrics))]
    public async Task A_station_route_call_marks_that_station_s_page_open_and_no_other(string action)
    {
        var h = new MultiStationHarness();

        await h.RunAsync(action, "two");

        Assert.Equal(new[] { "two" }, h.Demand.Decide(["live", "two"], new HashSet<string>()).ToArray());
    }

    [Fact]
    public async Task An_unlisted_id_marks_nothing_and_a_route_without_an_id_marks_the_primary()
    {
        var h = new MultiStationHarness();

        await h.RunAsync(nameof(JellywaveWebController.Feed), "nope");
        Assert.Empty(h.Demand.Decide(["live", "two"], new HashSet<string>()));

        await h.RunAsync(nameof(JellywaveWebController.Feed), null);
        Assert.Equal(new[] { "live" }, h.Demand.Decide(["live", "two"], new HashSet<string>()).ToArray());
    }

    [Fact]
    public async Task A_request_status_call_marks_the_receipt_s_station()
    {
        var h = new MultiStationHarness();
        var token = h.Receipts.Record(WebRoute.Listener, MultiStationHarness.KeyOf("two", MultiStationHarness.HostTwo), "req-x")!;

        await WebRoute.RunAsync(h.Controller(), nameof(JellywaveWebController.RequestStatus), ("requestId", token));

        Assert.Equal(new[] { "two" }, h.Demand.Decide(["live", "two"], new HashSet<string>()).ToArray());
    }

    // ---- The session view passes Jellyfin's playback start on ----

    [Fact]
    public void The_session_view_passes_a_playback_start_on_with_the_item_s_external_id_until_disposed()
    {
        var proxy = DispatchProxy.Create<ISessionManager, TitleSamplerTests.FakeSessions>();
        var sessions = (TitleSamplerTests.FakeSessions)(object)proxy;
        var view = new SessionPlayingItems(proxy);
        var seen = new List<string?>();
        view.PlaybackStarted += (_, e) => seen.Add(e.ExternalId);

        var handler = Assert.Single(sessions.StartHandlers);
        handler(proxy, new PlaybackProgressEventArgs { Item = new Audio { ExternalId = "a" } });
        handler(proxy, new PlaybackProgressEventArgs());
        view.Dispose();

        Assert.Equal(new[] { "a", null }, seen);
        Assert.Empty(sessions.StartHandlers);
    }

    [Fact]
    public void The_session_view_passes_the_session_s_users_on_and_filters_the_playing_items_by_user()
    {
        var proxy = DispatchProxy.Create<ISessionManager, TitleSamplerTests.FakeSessions>();
        var sessions = (TitleSamplerTests.FakeSessions)(object)proxy;
        var view = new SessionPlayingItems(proxy);
        var seen = new List<IReadOnlyList<Guid>>();
        view.PlaybackStarted += (_, e) => seen.Add(e.UserIds);
        var owner = Guid.NewGuid();
        var added = Guid.NewGuid();
        var reported = new Jellyfin.Database.Implementations.Entities.User("bob", "Default", "Default");
        var session = new SessionInfo(proxy, NullLogger.Instance) { UserId = owner, AdditionalUsers = [new MediaBrowser.Model.Session.SessionUserInfo { UserId = added }] };

        var handler = Assert.Single(sessions.StartHandlers);
        handler(proxy, new PlaybackProgressEventArgs { Item = new Audio { ExternalId = "a" }, Users = [reported], Session = session });
        handler(proxy, new PlaybackProgressEventArgs { Item = new Audio { ExternalId = "a" } });

        Assert.Equal(new[] { reported.Id, owner, added }, seen[0]);
        Assert.Empty(seen[1]);   // no user: no listener (StationDemand then wakes nothing)

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        sessions.List.Add(new SessionInfo(proxy, NullLogger.Instance) { UserId = owner, NowPlayingItem = new MediaBrowser.Model.Dto.BaseItemDto { Id = a } });
        sessions.List.Add(new SessionInfo(proxy, NullLogger.Instance) { UserId = Guid.NewGuid(), NowPlayingItem = new MediaBrowser.Model.Dto.BaseItemDto { Id = b } });
        sessions.List.Add(new SessionInfo(proxy, NullLogger.Instance) { UserId = Guid.NewGuid(), AdditionalUsers = [new MediaBrowser.Model.Session.SessionUserInfo { UserId = added }], NowPlayingItem = new MediaBrowser.Model.Dto.BaseItemDto { Id = c } });
        sessions.List.Add(new SessionInfo(proxy, NullLogger.Instance) { NowPlayingItem = new MediaBrowser.Model.Dto.BaseItemDto { Id = b } });   // no user

        Assert.Equal(new[] { a, c }.Order(), view.PlayingItemIds(user => user == owner || user == added).Order());
        Assert.Equal(new[] { a, b, c }.Order(), view.PlayingItemIds().Order());   // every session (the title sampler asks this first: anything playing?)
        view.Dispose();
    }

    // ---- Fix round S2: only a user who can see the tile keeps a station asked ----

    [Fact]
    public async Task A_user_who_cannot_see_the_tile_neither_wakes_a_station_nor_keeps_it_asked()
    {
        var blocked = Guid.NewGuid();
        var server = new Server();
        server.TileGate = () => user => user != blocked;
        await server.PollAsync();

        server.Playing.Start(server.Items["a"].Id, "a", blocked);
        Assert.False(server.Woken());
        await server.PollAsync();
        Assert.Equal(0, server.Asked("a"));

        server.Playing.Start(server.Items["b"].Id, "b");   // the default listener sees the tile
        Assert.True(server.Woken());
        await server.WokenPollAsync();
        Assert.Equal((0, 1), (server.Asked("a"), server.Asked("b")));
    }

    [Fact]
    public async Task A_tile_gate_that_fails_wakes_nothing_and_keeps_the_stations_played_at_the_last_lookup()
    {
        var server = new Server();
        server.Play("a");
        await server.PollAsync();
        server.TileGate = () => throw new InvalidOperationException("database is locked");

        server.Playing.Start(server.Items["b"].Id, "b");   // Jellyfin's event: nothing escapes
        Assert.False(server.Woken());
        server.Clock.Advance(TimeSpan.FromSeconds(45));
        await server.PollAsync();

        Assert.Equal((2, 0), (server.Asked("a"), server.Asked("b")));
    }

    // ---- Fix round nit: the last played stations are kept for a failing lookup only, and not for good ----

    [Fact]
    public async Task A_name_collision_drops_the_last_played_stations_at_once()
    {
        var server = new Server();
        server.Play("a");
        await server.PollAsync();

        server.OtherChannels.Add(OtherPluginChannel.Named(Tile));
        server.Clock.Advance(TimeSpan.FromSeconds(31));
        await server.PollAsync();

        Assert.Equal(1, server.Asked("a"));   // no channel of ours: none of our items can play
    }

    [Fact]
    public async Task A_failing_channel_lookup_keeps_the_last_played_stations_while_something_plays_and_none_once_nothing_does()
    {
        // Fix round 2, F6: the poll passes the channel it resolved to the tile gate, so a failing channel lookup no longer
        // reaches the gate; it holds the last played stations while any session plays, and idles them once none does.
        var server = new Server();
        server.Play("a");
        await server.PollAsync();
        server.ChannelLookupFails = true;

        // The write phase resolves the channel too and fails the poll after the fetches (the loop logs it and goes on).
        server.Clock.Advance(TimeSpan.FromSeconds(45));
        await Assert.ThrowsAsync<InvalidOperationException>(server.PollAsync);
        Assert.Equal(2, server.Asked("a"));

        server.Stop("a");
        server.Clock.Advance(TimeSpan.FromSeconds(31));
        await Assert.ThrowsAsync<InvalidOperationException>(server.PollAsync);
        Assert.Equal(2, server.Asked("a"));
    }

    [Fact]
    public async Task A_lookup_that_keeps_failing_keeps_the_last_played_stations_for_two_minutes_only()
    {
        var server = new Server();
        server.Play("a");
        await server.PollAsync();
        server.Library.OnQuery = _ => throw new InvalidOperationException("database is locked");

        server.Clock.Advance(TimeSpan.FromSeconds(120));
        await server.PollAsync();
        Assert.Equal(2, server.Asked("a"));   // two minutes after the last lookup that worked: still kept

        server.Clock.Advance(TimeSpan.FromSeconds(31));   // past the hold, and past the grace after the last poll that kept it
        await server.PollAsync();
        Assert.Equal(2, server.Asked("a"));
    }

    // ---- Fix round R2: an idle station shows its own text ----

    [Fact]
    public async Task An_idle_station_shows_its_tagline_else_its_notes_else_the_default_text_and_the_logo()
    {
        var server = new Server("a", "b", "c");
        server.Config.Stations[0].Tagline = "  Freeform <b>radio</b>\nfrom the lake  ";
        server.Config.Stations[0].Notes = "Notes of a";
        server.Config.Stations[1].Notes = "Run by <i>volunteers</i>";

        await server.PollAsync();

        Assert.Equal("Freeform bradio/b from the lake", server.Overview("a"));
        Assert.Equal("Run by ivolunteers/i", server.Overview("b"));
        Assert.Equal(JellywaveChannel.DefaultOverview, server.Overview("c"));
        Assert.All(new[] { "a", "b", "c" }, id => Assert.Equal(LiveItemArtwork.LogoKey, server.Artwork.Applied(id)));
        Assert.Empty(server.Http.Seen);
    }

    [Fact]
    public async Task A_station_that_goes_idle_trades_its_track_for_its_own_text_and_back()
    {
        var server = new Server();
        server.Config.Stations[0].Tagline = "The lake's own radio";
        server.Play("a");
        await server.PollAsync();
        Assert.StartsWith("Now playing: Artist a – Song a", server.Overview("a"), StringComparison.Ordinal);

        server.Stop("a");
        server.Clock.Advance(TimeSpan.FromSeconds(31));
        await server.PollAsync();
        Assert.Equal("The lake's own radio", server.Overview("a"));

        server.Playing.Start(server.Items["a"].Id, "a");
        await server.WokenPollAsync();
        Assert.StartsWith("Now playing: Artist a – Song a", server.Overview("a"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_needed_station_shows_the_default_text_not_its_tagline()
    {
        var server = new Server();
        server.Config.Stations[0].Tagline = "The lake's own radio";
        server.Play("a");
        server.Http.Down.Add("a.example");

        await server.PollAsync();

        Assert.Equal(JellywaveChannel.DefaultOverview, server.Overview("a"));
    }

    [Fact]
    public void The_idle_overview_is_cut_like_the_notes()
    {
        var station = new StationDefinition { Tagline = new string('x', 400) };

        var overview = JellywaveChannel.IdleOverview(station);

        Assert.Equal(StationDefinition.MaxNotesLength, overview.Length);
        Assert.EndsWith("…", overview, StringComparison.Ordinal);
        Assert.Equal(JellywaveChannel.DefaultOverview, JellywaveChannel.IdleOverview(new StationDefinition { Tagline = " <> ", Notes = "\n" }));
    }

    // ---- Fix round nits: the fetch time, the woken poll, the primary's idle mark ----

    [Fact]
    public async Task A_regular_poll_that_asks_nothing_clears_the_fetch_time_and_a_woken_poll_records_none()
    {
        var server = new Server();
        server.Play("a");
        await server.PollAsync();
        Assert.NotNull(server.Health.Snapshot(null).LastFetchSeconds);

        server.Stop("a");
        server.Clock.Advance(TimeSpan.FromSeconds(31));
        await server.PollAsync();
        Assert.Null(server.Health.Snapshot(null).LastFetchSeconds);

        server.Playing.Start(server.Items["b"].Id, "b");
        await server.WokenPollAsync();
        Assert.Equal(1, server.Asked("b"));
        Assert.Null(server.Health.Snapshot(null).LastFetchSeconds);   // one station's fetch is not the list's
    }

    [Fact]
    public async Task A_woken_poll_with_nobody_new_to_ask_writes_nothing()
    {
        var server = new Server();
        server.Play("a");
        await server.PollAsync();
        var updates = server.Library.Updated.Count;
        var queries = server.Library.Queries.Count;

        server.Playing.StartedAndStopped("b");
        Assert.True(server.Woken());
        await server.WokenPollAsync();

        Assert.Equal(1, server.Asked("a"));
        Assert.Equal(0, server.Asked("b"));
        Assert.Equal(updates, server.Library.Updated.Count);
        Assert.Equal(queries + 2, server.Library.Queries.Count);   // the played-item lookups only, no write phase
    }

    [Fact]
    public async Task A_woken_poll_that_sees_a_station_go_idle_writes_its_idle_look_and_asks_it_at_once_when_played_again()
    {
        // Fix round 2, F1: a's listener stops and the grace ends between two regular polls; a wake for b (a start and a
        // stop) then runs the poll that sees a go idle. It asks nobody, but it must still mark a idle, or a stays "asked"
        // and its next playback waits for the regular poll instead of waking one.
        var server = new Server();
        server.Play("a");
        await server.PollAsync();
        server.Stop("a");
        server.Clock.Advance(TimeSpan.FromSeconds(31));

        server.Playing.StartedAndStopped("b");
        Assert.True(server.Woken());
        await server.WokenPollAsync();
        Assert.Equal(1, server.Asked("a"));
        Assert.Equal(JellywaveChannel.DefaultOverview, server.Overview("a"));
        Assert.NotNull(server.Health.Snapshot(server.Key("a")).IdleSinceUtc);

        server.Playing.Start(server.Items["a"].Id, "a");
        Assert.True(server.Woken());
        await server.WokenPollAsync();
        Assert.Equal(2, server.Asked("a"));
        Assert.StartsWith("Now playing: Artist a – Song a", server.Overview("a"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_health_route_s_top_level_carries_the_primary_station_s_idle_mark()
    {
        var server = new Server();
        await server.PollAsync();
        var controller = new JellywaveController(server.Client, null!, server.State, server.Health, server.Artwork, null!, new LiveItemFinder(server.Library.Manager, () => [], NullLogger<LiveItemFinder>.Instance), NullLogger<JellywaveController>.Instance, () => server.Config, new StationTitles(), new StationCheck(server.Client, server.Health, NullLogger<StationCheck>.Instance));

        var health = controller.Health().Value!;

        Assert.NotNull(health.IdleSinceUtc);
        Assert.Equal(health.Stations.Single(s => s.Id == "a").IdleSinceUtc, health.IdleSinceUtc);
    }

    // ---- Fix round R1: the poll loop itself ----

    [Fact]
    public async Task A_stream_of_playback_starts_of_an_idle_station_never_stops_the_regular_poll()
    {
        // A signed-in user reports a start and a stop of station b's item over and over, so b wakes the loop again and again
        // and is never seen playing. Station a, which someone plays, must still be asked every interval (5 s on the fast
        // clock, so the loop's 1 s gap sets the pace): before the fix it was asked once, then never again. Three asks take
        // about 2 s; the 30 s deadline is for a starved thread pool under a parallel test run (fix round 2: 10 s flaked once).
        var server = new Server(new FastClock(), "a", "b");
        server.Config.NowPlayingPollSeconds = PluginConfiguration.MinPollSeconds;
        server.Play("a");
        using var stop = new CancellationTokenSource();
        var reports = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                server.Playing.StartedAndStopped("b");
                await Task.Delay(20);
            }
        });

        var elapsed = Stopwatch.StartNew();
        await server.Poller.StartAsync(CancellationToken.None);
        while (server.Asked("a") < 3 && elapsed.Elapsed < LoopDeadline)
        {
            await Task.Delay(50);
        }

        await stop.CancelAsync();
        await reports;
        await server.Poller.StopAsync(CancellationToken.None);
        server.Poller.Dispose();

        Assert.True(server.Asked("a") >= 3, "a asked " + server.Asked("a") + " times in " + elapsed.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
        Assert.Equal(0, server.Asked("b"));
    }

    [Fact]
    public async Task The_loop_asks_a_station_that_wakes_it_within_seconds_without_asking_the_others_again()
    {
        // A long interval (300 s, 30 s on the fast clock): only the wake can bring b's ask inside this test. Its deadline,
        // 20 s, stays under that interval (a second regular poll would ask both) and leaves room for a starved thread pool.
        var server = new Server(new FastClock(), "a", "b");
        server.Config.NowPlayingPollSeconds = PluginConfiguration.MaxPollSeconds;
        server.Play("a");

        var elapsed = Stopwatch.StartNew();
        await server.Poller.StartAsync(CancellationToken.None);
        while (server.Asked("a") < 1 && elapsed.Elapsed < WakeDeadline)
        {
            await Task.Delay(20);
        }

        server.Playing.Start(server.Items["b"].Id, "b");
        while (server.Asked("b") < 1 && elapsed.Elapsed < WakeDeadline)
        {
            await Task.Delay(20);
        }

        await server.Poller.StopAsync(CancellationToken.None);
        server.Poller.Dispose();

        Assert.True((server.Asked("a"), server.Asked("b")) == (1, 1), "a and b asked " + (server.Asked("a"), server.Asked("b")) + " in " + elapsed.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
    }
}
