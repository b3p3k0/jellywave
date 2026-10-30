using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L4 through the real poll: every listed SUB/WAVE station is polled and keeps its own state, health, cover and
/// overview, keyed by (station id, origin). One station's failure, empty feed, timeout, URL change or removal touches
/// that station only, and the fetch phase runs at most <see cref="NowPlayingService.MaxConcurrentStations"/> at once.
/// </summary>
public class PerStationPollTests
{
    private const string Tile = "Live Radio";

    /// <summary>Answers per host; a host can fail with any exception. Thread-safe: the poller fetches stations at once.</summary>
    private sealed class Stations : HttpMessageHandler
    {
        private readonly object _gate = new();

        public Dictionary<string, string> NowPlaying { get; } = new();

        public Dictionary<string, string> State { get; } = new();

        public Dictionary<string, Func<Exception>> Failing { get; } = new();

        public HashSet<string> Art { get; } = new();

        public List<string> Seen { get; } = new();

        /// <summary>Gets or sets what every now-playing request awaits first (to hold requests in flight).</summary>
        public Func<CancellationToken, Task>? BeforeNowPlaying { get; set; }

        public int Count(string request)
        {
            lock (_gate)
            {
                return Seen.Count(s => s == request);
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
                await hook(cancellationToken);
            }

            if (Failing.TryGetValue(host, out var fail))
            {
                throw fail();
            }

            if (Art.Contains(host + path))
            {
                var image = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageFixtures.For("image/jpeg")) };
                image.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                return image;
            }

            var body = path switch
            {
                "/api/now-playing" => NowPlaying.GetValueOrDefault(host),
                "/api/state" => State.GetValueOrDefault(host),
                "/api/schedule" => """{"timezone":"UTC","shows":[],"schedule":{}}""",
                _ => null   // covers: the station answers 502 when it has no art
            };
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

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class Server
    {
        public Server(params string[] ids)
            : this(TimeProvider.System, ids)
        {
        }

        public Server(TimeProvider clock, params string[] ids)
        {
            var channel = new Channel { Id = Library.IdFor("Channel " + Tile, typeof(Channel)), Name = Tile };
            Library.Items[channel.Id] = channel;
            var client = new StationClient(new StubFactory(Http));
            Artwork = new LiveItemArtwork(client, SaveImageAsync, NullLogger<LiveItemArtwork>.Instance);
            Schedule = new ScheduleCache(client);
            Poller = new NowPlayingService(
                client,
                State,
                Artwork,
                new StationLogos(client, SaveImageAsync, NullLogger<StationLogos>.Instance, TimeProvider.System),
                new LiveItemFinder(Library.Manager, () => new List<IChannel>(), NullLogger<LiveItemFinder>.Instance),
                Health,
                Schedule,
                Library.Manager,
                null!,
                Log,
                () => Config,
                clock);
            foreach (var id in ids.Length == 0 ? new[] { "a", "b", "c" } : ids)
            {
                Add(id);
            }
        }

        public FakeLibrary Library { get; } = FakeLibrary.Create();

        public Stations Http { get; } = new();

        public NowPlayingState State { get; } = new();

        public PollerHealth Health { get; } = new();

        public RecordingLogger Log { get; } = new();

        public LiveItemArtwork Artwork { get; }

        public ScheduleCache Schedule { get; }

        public NowPlayingService Poller { get; }

        public PluginConfiguration Config { get; } = new() { LibraryName = Tile };

        public Dictionary<string, Audio> Items { get; } = new();

        public List<(string? Item, string Mime)> Saved { get; } = new();

        /// <summary>Gets or sets what every image save calls first.</summary>
        public Action<BaseItem>? OnSave { get; set; }

        /// <summary>Adds SUB/WAVE station <paramref name="id"/> ("Radio <id>") at host <c>&lt;id&gt;.example</c>, with its item.</summary>
        public void Add(string id)
        {
            Config.Stations.Add(new StationDefinition { Id = id, Name = "Radio " + id.ToUpperInvariant(), BaseUrl = "http://" + id + ".example", SortOrder = Config.Stations.Count });
            Http.NowPlaying[id + ".example"] = Feed("Artist " + id.ToUpperInvariant(), "Song " + id.ToUpperInvariant(), "cov1");
            var item = new Audio { Id = Guid.NewGuid(), ExternalId = id };
            Items[id] = item;
            Library.ListedItems.Add(item);
        }

        public StationDefinition Station(string id) => Config.Stations.Single(s => s.Id == id);

        public StationKey Key(string id) => StationKey.Of(id, new Uri(Station(id).BaseUrl!.TrimEnd('/') + "/"));

        public string Overview(string id) => Items[id].Overview!;

        public List<string> SavedFor(string id) => Saved.Where(s => s.Item == id).Select(s => s.Mime).ToList();

        public Task PollAsync() => Poller.PollOnceAsync(CancellationToken.None);

        public static string Feed(string artist, string title, string coverId)
            => "{\"nowPlaying\":{\"artist\":\"" + artist + "\",\"title\":\"" + title + "\",\"subsonic_id\":\"" + coverId + "\"},\"streamOnline\":true}";

        private Task SaveImageAsync(BaseItem item, Stream stream, string mime, CancellationToken cancellationToken)
        {
            OnSave?.Invoke(item);
            Saved.Add((item.ExternalId, mime));
            item.SetImagePath(ImageType.Primary, "http://saved.example/" + Guid.NewGuid().ToString("N"));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Three_stations_each_get_their_own_overview_cover_state_and_health()
    {
        var server = new Server();
        server.Http.Art.Add("a.example/api/cover/cov1");
        server.Http.Art.Add("b.example/api/cover/cov1");   // c has no art for its track

        await server.PollAsync();

        foreach (var id in new[] { "a", "b", "c" })
        {
            var name = id.ToUpperInvariant();
            Assert.StartsWith("Now playing: Artist " + name + " – Song " + name, server.Overview(id), StringComparison.Ordinal);
            Assert.Equal("Song " + name, server.State.Snapshot(server.Key(id)).Feed?.NowPlaying?.Title);
            Assert.Equal(true, server.Health.Snapshot(server.Key(id)).Reachable);
        }

        Assert.Equal(new[] { "image/jpeg" }, server.SavedFor("a"));
        Assert.Equal(new[] { "image/jpeg" }, server.SavedFor("b"));
        Assert.Equal(new[] { "image/png" }, server.SavedFor("c"));
        Assert.Equal(("cov1", "cov1", LiveItemArtwork.LogoKey), (server.Artwork.Applied("a"), server.Artwork.Applied("b"), server.Artwork.Applied("c")));
    }

    [Fact]
    public async Task One_failing_station_leaves_the_others_items_and_state_untouched()
    {
        var server = new Server();
        server.Http.Art.Add("a.example/api/cover/cov1");
        server.Http.Art.Add("b.example/api/cover/cov1");
        server.Http.Art.Add("c.example/api/cover/cov1");
        server.Http.State["a.example"] = """{"history":[{"artist":"Old","title":"From A"}]}""";
        await server.PollAsync();
        var overviewA = server.Overview("a");
        var overviewC = server.Overview("c");

        server.Http.Failing["b.example"] = () => new HttpRequestException("Connection refused (b.example:7700) secret-detail");
        await server.PollAsync();

        // b: its own state, health and item only.
        Assert.Equal(JellywaveChannel.DefaultOverview, server.Overview("b"));
        Assert.Equal(new[] { "image/jpeg", "image/png" }, server.SavedFor("b"));
        Assert.Same(NowPlayingSnapshot.Nothing, server.State.Snapshot(server.Key("b")));
        var b = server.Health.Snapshot(server.Key("b"));
        Assert.Equal((false, StationFailures.CouldNotConnect), (b.Reachable, b.LastError));

        // a and c: untouched.
        Assert.Equal(overviewA, server.Overview("a"));
        Assert.Equal(overviewC, server.Overview("c"));
        Assert.Equal(new[] { "image/jpeg" }, server.SavedFor("a"));
        Assert.Equal(new[] { "image/jpeg" }, server.SavedFor("c"));
        Assert.Equal("From A", server.State.Snapshot(server.Key("a")).StationState?.History?[0].Title);
        Assert.All(new[] { "a", "c" }, id => Assert.Equal((true, (DateTime?)null), (server.Health.Snapshot(server.Key(id)).Reachable, server.Health.Snapshot(server.Key(id)).LastErrorUtc)));
    }

    [Fact]
    public async Task A_failure_streak_warns_once_naming_the_station_and_recovery_logs_one_line()
    {
        var server = new Server();
        server.Http.Failing["b.example"] = () => new HttpRequestException("Connection refused (b.example:7700) secret-detail");

        await server.PollAsync();
        await server.PollAsync();
        var warning = Assert.Single(server.Log.At(LogLevel.Warning));
        Assert.Contains("Radio B", warning, StringComparison.Ordinal);
        Assert.Contains(StationFailures.CouldNotConnect, warning, StringComparison.Ordinal);

        server.Http.Failing.Clear();
        await server.PollAsync();
        await server.PollAsync();

        Assert.Single(server.Log.At(LogLevel.Warning));
        Assert.Single(server.Log.At(LogLevel.Information), m => m == "Station Radio B is answering again");
        Assert.DoesNotContain(server.Log.Entries, e => e.Message.Contains("b.example", StringComparison.Ordinal) || e.Message.Contains("secret-detail", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_timeout_at_one_station_is_that_station_s_failure()
    {
        var server = new Server();
        server.Http.Failing["a.example"] = () => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 8 seconds elapsing.");

        await server.PollAsync();

        Assert.Equal(StationFailures.TimedOut, server.Health.Snapshot(server.Key("a")).LastError);
        Assert.Equal(JellywaveChannel.DefaultOverview, server.Overview("a"));
        Assert.StartsWith("Now playing: Artist B", server.Overview("b"), StringComparison.Ordinal);
        Assert.StartsWith("Now playing: Artist C", server.Overview("c"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_null_feed_at_one_station_fails_that_station_only()
    {
        var server = new Server();
        await server.PollAsync();

        server.Http.NowPlaying["b.example"] = "null";
        await server.PollAsync();

        var b = server.Health.Snapshot(server.Key("b"));
        Assert.Equal((false, StationFailures.EmptyFeed), (b.Reachable, b.LastError));
        Assert.Same(NowPlayingSnapshot.Nothing, server.State.Snapshot(server.Key("b")));
        Assert.Equal(JellywaveChannel.DefaultOverview, server.Overview("b"));
        Assert.Equal(true, server.Health.Snapshot(server.Key("a")).Reachable);
        Assert.StartsWith("Now playing: Artist A", server.Overview("a"), StringComparison.Ordinal);
        Assert.StartsWith("Now playing: Artist C", server.Overview("c"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_base_url_under_the_same_id_resets_only_that_station()
    {
        var server = new Server();
        server.Http.Art.Add("a.example/api/cover/cov1");
        server.Http.Art.Add("b.example/api/cover/cov1");
        server.Http.Art.Add("d.example/api/cover/cov1");
        server.Http.State["a.example"] = """{"history":[{"artist":"Old","title":"From A"}]}""";
        server.Http.State["b.example"] = """{"history":[{"artist":"Old","title":"From B"}]}""";
        server.Http.NowPlaying["d.example"] = Server.Feed("Artist D", "Song D", "cov1");   // same cover id, no /api/state
        await server.PollAsync();
        var healthA = server.Health.Snapshot(server.Key("a"));

        server.Station("b").BaseUrl = "http://d.example";

        // Before the next poll a reader with the new key gets nothing of the old origin.
        Assert.Same(NowPlayingSnapshot.OtherStation, server.State.Snapshot(server.Key("b")));
        Assert.Null(server.Health.Snapshot(server.Key("b")).Reachable);

        await server.PollAsync();

        var b = server.State.Snapshot(server.Key("b"));
        Assert.Equal("Song D", b.Feed?.NowPlaying?.Title);
        Assert.Null(b.StationState);   // not station B's history
        Assert.StartsWith("Now playing: Artist D", server.Overview("b"), StringComparison.Ordinal);
        Assert.Equal(1, server.Http.Count("d.example/api/cover/cov1"));
        Assert.Equal("http://d.example/", server.Artwork.AppliedOrigin("b"));
        Assert.Equal(new[] { "image/jpeg", "image/jpeg" }, server.SavedFor("b"));

        // a: same state, history and health; its cover was not asked for again.
        Assert.Equal("From A", server.State.Snapshot(server.Key("a")).StationState?.History?[0].Title);
        Assert.Equal(1, server.Http.Count("a.example/api/cover/cov1"));
        Assert.Equal(new[] { "image/jpeg" }, server.SavedFor("a"));
        Assert.Equal(true, server.Health.Snapshot(server.Key("a")).Reachable);
        Assert.True(server.Health.Snapshot(server.Key("a")).LastSuccessUtc >= healthA.LastSuccessUtc);
    }

    [Fact]
    public async Task A_new_failing_base_url_starts_a_new_warning_streak()
    {
        var server = new Server();
        server.Http.Failing["b.example"] = () => new HttpRequestException("refused");
        server.Http.Failing["d.example"] = () => new HttpRequestException("refused");
        await server.PollAsync();
        await server.PollAsync();
        Assert.Single(server.Log.At(LogLevel.Warning));

        server.Station("b").BaseUrl = "http://d.example";
        await server.PollAsync();

        Assert.Equal(2, server.Log.At(LogLevel.Warning).Count);
        Assert.Equal(StationFailures.CouldNotConnect, server.Health.Snapshot(server.Key("b")).LastError);
    }

    [Fact]
    public async Task Removing_a_station_drops_its_state_and_adding_it_back_starts_afresh()
    {
        var server = new Server();
        server.Http.Art.Add("c.example/api/cover/cov1");
        await server.PollAsync();
        var c = server.Station("c");
        var key = server.Key("c");

        server.Config.Stations.Remove(c);
        await server.PollAsync();

        Assert.Same(NowPlayingSnapshot.Nothing, server.State.Snapshot(key));
        Assert.Null(server.Health.Snapshot(key).Reachable);
        Assert.Null(server.Artwork.Applied("c"));
        Assert.StartsWith("Now playing: Artist A", server.Overview("a"), StringComparison.Ordinal);

        server.Config.Stations.Add(c);
        await server.PollAsync();

        Assert.Equal(2, server.Http.Count("c.example/api/cover/cov1"));
        Assert.Equal(new[] { "image/jpeg", "image/jpeg" }, server.SavedFor("c"));
        Assert.Equal(true, server.Health.Snapshot(key).Reachable);
    }

    [Fact]
    public async Task A_disabled_station_is_no_longer_polled_and_its_state_is_dropped()
    {
        var server = new Server();
        await server.PollAsync();

        server.Station("a").Enabled = false;
        await server.PollAsync();

        Assert.Equal(1, server.Http.Count("a.example/api/now-playing"));
        Assert.Same(NowPlayingSnapshot.Nothing, server.State.Snapshot(server.Key("a")));
        Assert.Null(server.Health.Snapshot(server.Key("a")).Reachable);
        Assert.Equal(2, server.Http.Count("b.example/api/now-playing"));
    }

    [Fact]
    public async Task At_most_the_cap_of_stations_is_fetched_at_once_and_every_station_is_polled()
    {
        // Each now-playing request waits until all six have arrived or half a second has passed: with the cap, four are
        // in flight and the other two follow; without it six would be, and polled one by one only one.
        var server = new Server("s0", "s1", "s2", "s3", "s4", "s5");
        var gate = new object();
        int inFlight = 0, peak = 0, arrived = 0;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Http.BeforeNowPlaying = async _ =>
        {
            lock (gate)
            {
                inFlight++;
                peak = Math.Max(peak, inFlight);
                if (++arrived == 6)
                {
                    all.TrySetResult();
                }
            }

            await Task.WhenAny(all.Task, Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None));
            lock (gate)
            {
                inFlight--;
            }
        };

        await server.PollAsync();

        // The documented bound (DESIGN "Now playing"), not the constant: a changed cap must fail here.
        Assert.Equal(4, peak);
        Assert.Equal(4, NowPlayingService.MaxConcurrentStations);
        Assert.All(new[] { "s0", "s1", "s2", "s3", "s4", "s5" }, id => Assert.StartsWith("Now playing:", server.Overview(id), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Polling_off_falls_every_sub_wave_item_back_and_clears_every_state()
    {
        var server = new Server();
        server.Http.Art.Add("a.example/api/cover/cov1");
        await server.PollAsync();

        server.Config.NowPlayingEnabled = false;
        await server.PollAsync();

        Assert.All(new[] { "a", "b", "c" }, id =>
        {
            Assert.Equal(JellywaveChannel.DefaultOverview, server.Overview(id));
            Assert.Same(NowPlayingSnapshot.Nothing, server.State.Snapshot(server.Key(id)));
            Assert.Equal("image/png", server.SavedFor(id).Last());
        });
    }

    [Fact]
    public async Task An_unexpected_error_at_one_station_is_that_station_s_failure_only()
    {
        var server = new Server();
        server.Http.Failing["b.example"] = () => new InvalidOperationException("a bug at b.example");

        await server.PollAsync();

        var b = server.Health.Snapshot(server.Key("b"));
        Assert.Equal((false, StationFailures.Unexpected), (b.Reachable, b.LastError));
        Assert.Equal(JellywaveChannel.DefaultOverview, server.Overview("b"));
        Assert.StartsWith("Now playing: Artist A", server.Overview("a"), StringComparison.Ordinal);
        Assert.StartsWith("Now playing: Artist C", server.Overview("c"), StringComparison.Ordinal);
        Assert.Contains(server.Log.At(LogLevel.Error), m => m.Contains("Radio B", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failing_item_write_for_one_station_does_not_stop_the_items_after_it()
    {
        var server = new Server();
        server.Library.FailUpdates.Add("b");

        await server.PollAsync();

        Assert.StartsWith("Now playing: Artist C", server.Overview("c"), StringComparison.Ordinal);
        Assert.Contains(server.Library.Updated, i => i.ExternalId == "c");
        Assert.Contains(server.Log.At(LogLevel.Error), m => m.Contains("Radio B", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_station_that_changes_kind_and_back_gets_the_right_image_each_time()
    {
        // The cover keeper remembers SUB/WAVE stations only and the logo keeper streams only, so neither keeps a stale
        // "already applied" for an id that the other one painted meanwhile.
        var server = new Server();
        server.Http.Art.Add("a.example/api/cover/cov1");
        server.Http.Art.Add("cdn.example/a.jpg");
        var a = server.Station("a");

        await server.PollAsync();   // SUB/WAVE: cover
        a.Kind = StationKind.Stream;
        a.StreamUrl = "https://cdn.example/a.mp3";
        a.LogoUrl = "https://cdn.example/a.jpg";
        await server.PollAsync();   // stream: its logo
        Assert.Equal(JellywaveChannel.DefaultOverview, server.Overview("a"));
        a.Kind = StationKind.SubWave;
        await server.PollAsync();   // SUB/WAVE again: the cover again
        a.Kind = StationKind.Stream;
        await server.PollAsync();   // stream again: the logo again

        Assert.Equal(4, server.SavedFor("a").Count);
        Assert.Equal(2, server.Http.Count("a.example/api/cover/cov1"));
        Assert.Equal(2, server.Http.Count("cdn.example/a.jpg"));
    }

    [Fact]
    public async Task Removing_a_station_drops_its_schedule_copy()
    {
        var server = new Server();
        var baseC = new Uri("http://c.example/");
        await server.Schedule.GetAsync("c", baseC, CancellationToken.None);
        await server.Schedule.GetAsync("c", baseC, CancellationToken.None);
        Assert.Equal(1, server.Http.Count("c.example/api/schedule"));

        server.Config.Stations.Remove(server.Station("c"));
        await server.PollAsync();
        await server.Schedule.GetAsync("c", baseC, CancellationToken.None);

        Assert.Equal(2, server.Http.Count("c.example/api/schedule"));
    }

    private static async Task<Exception?> StopDuringFetchAsync(Server server)
    {
        // The third station to arrive cancels the poll while all three are in flight.
        using var stopping = new CancellationTokenSource();
        var arrived = 0;
        server.Http.BeforeNowPlaying = async token =>
        {
            if (Interlocked.Increment(ref arrived) == 3)
            {
                _ = Task.Run(() => stopping.Cancel(), CancellationToken.None);
            }

            await Task.Delay(Timeout.Infinite, token);
        };
        return await Record.ExceptionAsync(() => server.Poller.PollOnceAsync(stopping.Token));
    }

    [Fact]
    public async Task Stopping_during_the_fetch_logs_nothing_records_nothing_and_writes_nothing()
    {
        // RA R1 (R01, R02, R04): shutdown is not a station failure.
        var server = new Server();
        await server.PollAsync();
        var updated = server.Library.Updated.Count;
        var logged = server.Log.Entries.Count(e => e.Level >= LogLevel.Warning);
        var errorsBefore = new[] { "a", "b", "c" }.Select(id => server.Health.Snapshot(server.Key(id)).LastErrorUtc).ToList();
        foreach (var id in new[] { "a", "b", "c" })
        {
            server.Http.NowPlaying[id + ".example"] = Server.Feed("New " + id, "New song", "cov2");
        }

        var ex = await StopDuringFetchAsync(server);

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.Equal(updated, server.Library.Updated.Count);
        Assert.Equal(logged, server.Log.Entries.Count(e => e.Level >= LogLevel.Warning));
        Assert.Equal(errorsBefore, new[] { "a", "b", "c" }.Select(id => server.Health.Snapshot(server.Key(id)).LastErrorUtc).ToList());
        Assert.All(new[] { "a", "b", "c" }, id => Assert.Equal(true, server.Health.Snapshot(server.Key(id)).Reachable));
    }

    [Fact]
    public async Task Stopping_during_the_write_phase_rethrows_logs_nothing_and_writes_no_further_item()
    {
        // RA R1 (R03): the first item's save cancels the poll; its database write then answers with a cancelled task.
        var server = new Server();
        server.Library.HonourCancellation = true;
        using var stopping = new CancellationTokenSource();
        server.OnSave = _ => stopping.Cancel();

        var ex = await Record.ExceptionAsync(() => server.Poller.PollOnceAsync(stopping.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.Empty(server.Library.Updated);
        Assert.Equal(new[] { "a" }, server.Saved.Select(s => s.Item));
        Assert.DoesNotContain(server.Log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_plain_operation_canceled_at_one_station_stays_that_station_s_failure()
    {
        // RA R05: only the poll's own token means shutdown.
        var server = new Server();
        server.Http.Failing["b.example"] = () => new OperationCanceledException("linked timeout");

        await server.PollAsync();

        Assert.Equal(false, server.Health.Snapshot(server.Key("b")).Reachable);
        Assert.StartsWith("Now playing: Artist A", server.Overview("a"), StringComparison.Ordinal);
        Assert.StartsWith("Now playing: Artist C", server.Overview("c"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unexpected_error_after_a_good_poll_clears_the_line_and_shows_the_logo()
    {
        // RA R06, R07.
        var server = new Server();
        server.Http.Art.Add("b.example/api/cover/cov1");
        await server.PollAsync();
        Assert.StartsWith("Now playing: Artist B", server.Overview("b"), StringComparison.Ordinal);

        server.Http.Failing["b.example"] = () => new InvalidOperationException("a bug");
        await server.PollAsync();

        Assert.Equal(JellywaveChannel.DefaultOverview, server.Overview("b"));
        Assert.Equal(new[] { "image/jpeg", "image/png" }, server.SavedFor("b"));
    }

    [Fact]
    public async Task Cover_art_off_gives_every_sub_wave_item_the_logo_without_asking()
    {
        // RA R17.
        var server = new Server();
        server.Http.Art.Add("a.example/api/cover/cov1");
        server.Config.CoverArtEnabled = false;

        await server.PollAsync();

        Assert.Equal(new[] { "image/png" }, server.SavedFor("a"));
        Assert.Equal(0, server.Http.Count("a.example/api/cover/cov1"));
    }

    [Fact]
    public async Task With_no_sub_wave_station_listed_the_poller_waits_the_idle_delay()
    {
        // RA R21.
        var server = new Server("a");
        var a = server.Station("a");
        a.Kind = StationKind.Stream;
        a.StreamUrl = "https://cdn.example/a.mp3";
        server.Config.NowPlayingPollSeconds = 10;

        var delay = await server.Poller.PollOnceAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    [Fact]
    public async Task A_removed_failing_station_warns_again_when_it_comes_back_failing()
    {
        // RA R14: the warning streak goes with the station.
        var server = new Server();
        server.Http.Failing["b.example"] = () => new HttpRequestException("refused");
        await server.PollAsync();
        var b = server.Station("b");
        server.Config.Stations.Remove(b);
        await server.PollAsync();
        server.Config.Stations.Add(b);
        await server.PollAsync();

        Assert.Equal(2, server.Log.At(LogLevel.Warning).Count);
    }

    [Fact]
    public async Task Health_top_level_description_and_cover_are_the_primary_station_s_own()
    {
        // RA R26, R28: the description from the primary's key, the cover from what its item shows (no art here, so the
        // logo, although the feed names cover cov1).
        var server = new Server("a", "b");
        await server.PollAsync();
        var station = new StationClient(null!);
        var controller = new Api.JellywaveController(
            station,
            null!,
            server.State,
            server.Health,
            server.Artwork,
            new StationLogos(station, (_, _, _, _) => Task.CompletedTask, NullLogger<StationLogos>.Instance, TimeProvider.System),
            new LiveItemFinder(server.Library.Manager, () => [], NullLogger<LiveItemFinder>.Instance),
            NullLogger<Api.JellywaveController>.Instance,
            () => server.Config);

        var health = controller.Health().Value!;

        Assert.StartsWith("Now playing: Artist A", health.Description, StringComparison.Ordinal);
        Assert.Equal(LiveItemArtwork.LogoKey, health.Cover);
    }

    [Fact]
    public async Task A_fetch_phase_longer_than_the_interval_is_logged_once_per_streak_and_shown_in_health()
    {
        // RA S3: no deadline, but the admin is told when the list makes polls slower than set.
        var clock = new ManualClock();
        var server = new Server(clock);
        server.Config.NowPlayingPollSeconds = 20;
        var took = TimeSpan.FromSeconds(7);
        server.Http.BeforeNowPlaying = _ =>
        {
            clock.Advance(took);
            return Task.CompletedTask;
        };

        int Lines() => server.Log.At(LogLevel.Information).Count(m => m.Contains("fetch phase alone took", StringComparison.Ordinal));

        await server.PollAsync();   // 3 × 7 s = 21 s: slow
        await server.PollAsync();   // still slow: same streak
        Assert.Equal(1, Lines());
        Assert.Equal(21.0, server.Health.Snapshot(null).LastFetchSeconds);

        // Jitter around the interval: 18 s and 16.5 s are under it but not under 80 % (16 s), so the streak goes on.
        took = TimeSpan.FromSeconds(6);
        await server.PollAsync();
        took = TimeSpan.FromSeconds(7);
        await server.PollAsync();
        took = TimeSpan.FromSeconds(5.5);
        await server.PollAsync();
        took = TimeSpan.FromSeconds(7);
        await server.PollAsync();
        Assert.Equal(1, Lines());

        took = TimeSpan.FromSeconds(5.3);
        await server.PollAsync();   // 15.9 s: under 80 %, the streak ends
        Assert.Equal(15.9, server.Health.Snapshot(null).LastFetchSeconds);
        took = TimeSpan.FromSeconds(7);
        await server.PollAsync();   // slow again: a new streak

        Assert.Equal(2, Lines());
    }

    [Fact]
    public async Task A_fetch_phase_within_the_interval_logs_nothing_about_its_length()
    {
        var clock = new ManualClock();
        var server = new Server(clock);
        server.Config.NowPlayingPollSeconds = 21;
        server.Http.BeforeNowPlaying = _ =>
        {
            clock.Advance(TimeSpan.FromSeconds(7));
            return Task.CompletedTask;
        };

        await server.PollAsync();   // exactly the interval

        Assert.DoesNotContain(server.Log.At(LogLevel.Information), m => m.Contains("fetch phase alone took", StringComparison.Ordinal));
        Assert.Equal(21.0, server.Health.Snapshot(null).LastFetchSeconds);
    }

    [Fact]
    public async Task No_cover_is_downloaded_for_a_station_whose_item_does_not_exist_yet()
    {
        // RA nit: nothing would be saved, so nothing is downloaded; the state and health are still kept.
        var server = new Server();
        server.Http.Art.Add("c.example/api/cover/cov1");
        server.Library.ListedItems.Remove(server.Items["c"]);

        await server.PollAsync();
        await server.PollAsync();

        Assert.Equal(0, server.Http.Count("c.example/api/cover/cov1"));
        Assert.Equal("Song C", server.State.Snapshot(server.Key("c")).Feed?.NowPlaying?.Title);
        Assert.Equal(true, server.Health.Snapshot(server.Key("c")).Reachable);

        server.Library.ListedItems.Add(server.Items["c"]);
        await server.PollAsync();

        Assert.Equal(1, server.Http.Count("c.example/api/cover/cov1"));
        Assert.Equal(new[] { "image/jpeg" }, server.SavedFor("c"));
    }

    [Fact]
    public async Task Stopping_during_the_write_lookup_of_an_unchanged_item_looks_up_no_further_item()
    {
        // RA E14: an item with nothing to write awaits no token, so the loop itself must stop before the next item.
        var server = new Server("a", "b");
        server.Library.HonourCancellation = true;
        await server.PollAsync();
        server.Http.NowPlaying["b.example"] = Server.Feed("Artist B", "Another song", "cov1");
        using var stopping = new CancellationTokenSource();
        var aQueries = 0;
        var bAfterStop = 0;
        server.Library.OnQuery = id =>
        {
            if (id == "a" && ++aQueries == 2)
            {
                stopping.Cancel();   // the write phase's lookup of a, which has nothing to write
            }
            else if (id == "b" && stopping.IsCancellationRequested)
            {
                bAfterStop++;
            }
        };

        var ex = await Record.ExceptionAsync(() => server.Poller.PollOnceAsync(stopping.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.Equal(0, bAfterStop);
    }

    [Fact]
    public async Task A_failing_item_lookup_before_the_cover_is_not_the_station_s_failure()
    {
        // RA S-A: a library lock during a scan. The station answered: health, state and its cover stay; nothing at Error.
        var server = new Server("a", "b");
        server.Http.Art.Add("b.example/api/cover/cov1");
        server.Http.Art.Add("b.example/api/cover/cov2");
        await server.PollAsync();
        server.Http.NowPlaying["b.example"] = Server.Feed("Artist B", "Next song", "cov2");
        var bQueries = 0;
        server.Library.OnQuery = id =>
        {
            if (id == "b" && ++bQueries == 1)
            {
                throw new InvalidOperationException("database is locked");
            }
        };

        await server.PollAsync();

        var b = server.Health.Snapshot(server.Key("b"));
        Assert.Equal((true, (string?)null), (b.Reachable, b.LastError));
        Assert.StartsWith("Now playing: Artist B – Next song", server.Overview("b"), StringComparison.Ordinal);
        Assert.Equal(new[] { "image/jpeg" }, server.SavedFor("b"));   // the cover kept, no logo over it
        Assert.Equal(0, server.Http.Count("b.example/api/cover/cov2"));
        Assert.Empty(server.Log.At(LogLevel.Error));
        Assert.Empty(server.Log.At(LogLevel.Warning));

        await server.PollAsync();   // the lookup works again: the new cover follows

        Assert.Equal(new[] { "image/jpeg", "image/jpeg" }, server.SavedFor("b"));
    }

    [Fact]
    public async Task A_failing_channel_lookup_before_the_fetch_skips_the_covers_but_not_the_stations()
    {
        // RA S-A: the pre-fetch resolve throws once; the write phase's resolve works.
        var server = new Server("a", "b");
        server.Http.Art.Add("a.example/api/cover/cov1");
        server.Http.Art.Add("b.example/api/cover/cov1");
        var throws = 1;
        server.Library.OnGetById = _ =>
        {
            if (throws-- > 0)
            {
                throw new InvalidOperationException("database is locked");
            }
        };

        await server.PollAsync();

        Assert.Equal(0, server.Http.Count("a.example/api/cover/cov1"));
        Assert.Equal(0, server.Http.Count("b.example/api/cover/cov1"));
        Assert.All(new[] { "a", "b" }, id =>
        {
            Assert.Equal(true, server.Health.Snapshot(server.Key(id)).Reachable);
            Assert.StartsWith("Now playing:", server.Overview(id), StringComparison.Ordinal);
        });
        Assert.Empty(server.Log.At(LogLevel.Error));

        await server.PollAsync();

        Assert.Equal(1, server.Http.Count("a.example/api/cover/cov1"));
        Assert.Equal(new[] { "image/jpeg" }, server.SavedFor("a"));
    }
}
