using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L6: the title sampler asks only for plain streams someone plays (Jellyfin's sessions), never for an HLS stream or a
/// SUB/WAVE station, records each stream's state under its id, warns once per failing streak, and drops a stream's state
/// a few intervals after the last listener.
/// </summary>
public class TitleSamplerTests
{
    private const string Tile = "Live Radio";

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Playing : IPlayingItems
    {
        public HashSet<Guid> Ids { get; } = new();

        public int Asked { get; private set; }

        public event EventHandler<PlaybackStartedEventArgs>? PlaybackStarted
        {
            add { }
            remove { }
        }

        public IReadOnlySet<Guid> PlayingItemIds()
        {
            Asked++;
            return Ids;
        }

        /// <summary>Gets the user of each played item whose session is not <see cref="FakePlayingItems.Listener"/>'s.</summary>
        public Dictionary<Guid, Guid> UserOf { get; } = new();

        public IReadOnlySet<Guid> PlayingItemIds(Func<Guid, bool> userCounts)
            => Ids.Where(id => userCounts(UserOf.GetValueOrDefault(id, FakePlayingItems.Listener))).ToHashSet();
    }

    private sealed class RecordingLogger : ILogger<TitleSampler>
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
    }

    /// <summary>The streams: per host, a title, no metadata, an error status or a redirect; records every request.</summary>
    private sealed class Streams : HttpMessageHandler
    {
        public List<string> Seen { get; } = new();

        public List<string> SeenWhole { get; } = new();

        public Dictionary<string, string> Titles { get; } = new() { ["cdn.example"] = "Artist - Title" };

        public HashSet<string> Down { get; } = new() { "down.example" };

        /// <summary>Card L13: hosts that answer 200 with icy-metaint and then end before the metadata block.</summary>
        public HashSet<string> Short { get; } = new() { "short.example" };

        /// <summary>Card L13: hosts whose request fails with an exception that is not a station failure (a bug).</summary>
        public HashSet<string> Buggy { get; } = new() { "bug.example" };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            lock (Seen)
            {
                Seen.Add(url.Host + url.AbsolutePath);
                SeenWhole.Add(url.AbsoluteUri);
            }

            if (Down.Contains(url.Host))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            if (Buggy.Contains(url.Host))
            {
                throw new InvalidOperationException("a bug in the stream code");
            }

            if (Short.Contains(url.Host))
            {
                var cut = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) };
                cut.Headers.TryAddWithoutValidation("icy-metaint", "1000");
                return Task.FromResult(cut);
            }

            if (url.Host == "moved.example")
            {
                var moved = new HttpResponseMessage(HttpStatusCode.Found);
                moved.Headers.Location = new Uri("http://elsewhere.example/");
                return Task.FromResult(moved);
            }

            if (!Titles.TryGetValue(url.Host, out var title))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[64]) });
            }

            const int MetaInt = 1000;
            var block = IcyMetadataTests.Block("StreamTitle='" + title + "';StreamUrl='';");
            var body = new byte[MetaInt].Concat(new[] { (byte)(block.Length / IcyMetadata.BlockUnit) }).Concat(block).Concat(new byte[MetaInt]).ToArray();
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            response.Headers.TryAddWithoutValidation("icy-metaint", MetaInt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(response);
        }
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public Factory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class Harness
    {
        public Harness()
        {
            var channel = new Channel { Id = Library.IdFor("Channel " + Tile, typeof(Channel)), Name = Tile };
            Library.Items[channel.Id] = channel;
            foreach (var station in Config.Stations)
            {
                var item = new Audio { Id = Guid.NewGuid(), ExternalId = station.Id };
                Items[station.Id] = item.Id;
                Library.ListedItems.Add(item);
            }

            Sampler = new TitleSampler(
                new StationClient(new Factory(Handler)),
                Titles,
                new LiveItemFinder(Library.Manager, () => new List<IChannel>(), NullLogger<LiveItemFinder>.Instance),
                Playing,
                channel => TileGate(channel),
                null!,
                Logger,
                () => Config,
                Clock);
        }

        public PluginConfiguration Config { get; } = new()
        {
            LibraryName = Tile,
            Stations =
            {
                new StationDefinition { Id = "plain", Name = "Plain", Kind = StationKind.Stream, StreamUrl = "http://cdn.example/a.mp3?token=secret", SortOrder = 0 },
                new StationDefinition { Id = "hls", Name = "Hls", Kind = StationKind.Stream, StreamUrl = "https://hls.example/live/index.m3u8", SortOrder = 1 },
                new StationDefinition { Id = "quiet", Name = "Quiet", Kind = StationKind.Stream, StreamUrl = "http://quiet.example/q.mp3", SortOrder = 2 },
                new StationDefinition { Id = "down", Name = "Down", Kind = StationKind.Stream, StreamUrl = "http://down.example/d.mp3", SortOrder = 3 },
                new StationDefinition { Id = "moved", Name = "Moved", Kind = StationKind.Stream, StreamUrl = "http://moved.example/m.mp3", SortOrder = 4 },
                new StationDefinition { Id = "sw", Name = "SubWave", BaseUrl = "http://sw.example", SortOrder = 5 },
                new StationDefinition { Id = "off", Name = "Off", Kind = StationKind.Stream, StreamUrl = "http://off.example/o.mp3", Enabled = false, SortOrder = 6 },
            }
        };

        public Dictionary<string, Guid> Items { get; } = new();

        public FakeLibrary Library { get; } = FakeLibrary.Create();

        public Streams Handler { get; } = new();

        public StationTitles Titles { get; } = new();

        public Playing Playing { get; } = new();

        /// <summary>Gets or sets the tile gate for the sessions' users, given the resolved channel (default: everyone sees the tile).</summary>
        public Func<Channel, Func<Guid, bool>> TileGate { get; set; } = _ => _ => true;

        public ManualClock Clock { get; } = new();

        public RecordingLogger Logger { get; } = new();

        public TitleSampler Sampler { get; }

        public void Play(params string[] stationIds)
        {
            Playing.Ids.Clear();
            foreach (var id in stationIds)
            {
                Playing.Ids.Add(Items[id]);
            }
        }

        public Task RoundAsync() => Sampler.SampleOnceAsync(CancellationToken.None);

        public async Task AfterAsync(TimeSpan wait)
        {
            Clock.Now += wait;
            await RoundAsync();
        }

        public string StateOf(string id) => Titles.Snapshot(id).State;
    }

    [Fact]
    public async Task Nobody_playing_means_no_request()
    {
        var h = new Harness();

        await h.RoundAsync();

        Assert.Empty(h.Handler.Seen);
        Assert.Empty(h.Library.Queries);   // not even an item lookup
        Assert.Equal(StationTitles.StateWaiting, h.StateOf("plain"));
    }

    [Fact]
    public async Task Something_else_playing_means_no_request()
    {
        var h = new Harness();
        h.Playing.Ids.Add(Guid.NewGuid());   // a film, a song from the music library

        await h.RoundAsync();

        Assert.Empty(h.Handler.Seen);
        Assert.Equal(0, h.Titles.Count());
    }

    [Fact]
    public async Task Only_a_user_who_can_see_the_tile_makes_a_stream_sampled()
    {
        // Card L14 fix round 2: a user blocked from the tile can report playback of a stream's item through the session
        // API; that must not make the server open the stream every 15 s.
        var h = new Harness();
        var blocked = Guid.NewGuid();
        Channel? gated = null;
        h.TileGate = channel =>
        {
            gated = channel;
            return user => user != blocked;
        };
        h.Play("plain", "quiet");
        h.Playing.UserOf[h.Items["plain"]] = blocked;

        await h.RoundAsync();

        Assert.Equal(new[] { "quiet.example/q.mp3" }, h.Handler.Seen);
        Assert.Equal(StationTitles.StateWaiting, h.StateOf("plain"));
        Assert.Equal(Tile, gated?.Name);   // the channel the round resolved

        h.TileGate = _ => _ => throw new InvalidOperationException("database is locked");
        h.Handler.Seen.Clear();
        await h.AfterAsync(TitleSampler.SampleInterval);
        Assert.Empty(h.Handler.Seen);   // a failing gate samples nothing this round
    }

    [Fact]
    public async Task A_played_plain_stream_is_sampled_and_its_title_kept()
    {
        var h = new Harness();
        h.Play("plain");

        await h.RoundAsync();

        Assert.Equal(new[] { "cdn.example/a.mp3" }, h.Handler.Seen);
        var heard = h.Titles.Snapshot("plain");
        Assert.Equal(StationTitles.StateTitle, heard.State);
        Assert.Equal("Artist - Title", heard.Title);
        Assert.Equal(h.Clock.Now.UtcDateTime, heard.LastGoodUtc);
        Assert.Equal(StationTitles.StateWaiting, h.StateOf("quiet"));   // not played: not sampled
    }

    [Fact]
    public async Task The_sample_reads_the_url_the_relay_reads()
    {
        // Card L10: one URL per station. The sampler opens exactly the media source's Path (what ffmpeg relays), query
        // included; there is no client URL to read instead.
        var h = new Harness();
        h.Play("plain");

        await h.RoundAsync();

        var items = (await new JellywaveChannel(NullLogger<JellywaveChannel>.Instance, new NowPlayingState(), null!, new RenameBridge(_ => Guid.Empty), () => h.Config)
            .GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None)).Items;
        var path = Assert.Single(items.Single(i => i.Id == "plain").MediaSources).Path;
        Assert.Equal("http://cdn.example/a.mp3?token=secret", path);
        Assert.Equal(new[] { path }, h.Handler.SeenWhole);
    }

    [Fact]
    public async Task Each_played_stream_is_sampled_once_per_round_and_only_those()
    {
        var h = new Harness();
        h.Play("plain", "quiet", "sw");

        await h.RoundAsync();

        Assert.Equal(new[] { "cdn.example/a.mp3", "quiet.example/q.mp3" }, h.Handler.Seen.Order());
        Assert.Equal(StationTitles.StateNoTitles, h.StateOf("quiet"));
        Assert.Equal(StationTitles.StateWaiting, h.StateOf("sw"));   // a SUB/WAVE station is never sampled
        Assert.DoesNotContain(h.Handler.Seen, s => s.StartsWith("sw.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_hls_stream_has_no_titles_and_is_never_requested()
    {
        var h = new Harness();
        h.Play("hls");

        await h.RoundAsync();

        Assert.Empty(h.Handler.Seen);
        Assert.Equal(StationTitles.StateNoTitles, h.StateOf("hls"));
    }

    [Fact]
    public async Task A_disabled_stream_is_never_sampled()
    {
        var h = new Harness();
        h.Play("off");

        await h.RoundAsync();

        Assert.Empty(h.Handler.Seen);
        Assert.Equal(0, h.Titles.Count());
    }

    [Fact]
    public async Task Titles_are_cleaned_and_cut()
    {
        var h = new Harness();
        h.Handler.Titles["cdn.example"] = "  Line\r\nbreak\t<b>bold</b>  " + new string('y', 300);
        h.Play("plain");

        await h.RoundAsync();

        var title = h.Titles.Snapshot("plain").Title!;
        Assert.StartsWith("Line break <b>bold</b> yyy", title, StringComparison.Ordinal);
        Assert.Equal(IcyMetadata.MaxTitleLength, title.Length);
        Assert.EndsWith("…", title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_title_counts_as_no_titles()
    {
        var h = new Harness();
        h.Handler.Titles["cdn.example"] = "   ";
        h.Play("plain");

        await h.RoundAsync();

        Assert.Equal(StationTitles.StateNoTitles, h.StateOf("plain"));
        Assert.Null(h.Titles.Snapshot("plain").Title);
    }

    [Fact]
    public async Task A_failing_stream_is_failing_and_warns_once_per_streak()
    {
        var h = new Harness();
        h.Play("down", "moved");

        await h.RoundAsync();
        await h.AfterAsync(TitleSampler.SampleInterval);
        await h.AfterAsync(TitleSampler.SampleInterval);

        Assert.Equal(StationTitles.StateFailing, h.StateOf("down"));
        Assert.Equal(StationTitles.StateFailing, h.StateOf("moved"));
        Assert.Equal(3, h.Handler.Seen.Count(s => s.StartsWith("down.example", StringComparison.Ordinal)));
        Assert.DoesNotContain(h.Handler.Seen, s => s.StartsWith("elsewhere.example", StringComparison.Ordinal));   // the redirect is not followed
        var warnings = h.Logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("Down", StringComparison.Ordinal) && w.Contains("HTTP 503", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("Moved", StringComparison.Ordinal) && w.Contains(StationFailures.Redirected, StringComparison.Ordinal));
        Assert.DoesNotContain(h.Logger.Entries, e => e.Message.Contains("://", StringComparison.Ordinal) || e.Message.Contains(".example", StringComparison.Ordinal) || e.Message.Contains("secret", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Logger.Entries, e => e.Level == LogLevel.Error);

        // It answers again: one Information line, then a new failure warns again.
        h.Handler.Down.Remove("down.example");
        h.Handler.Titles["down.example"] = "Back";
        await h.AfterAsync(TitleSampler.SampleInterval);
        Assert.Equal("Back", h.Titles.Snapshot("down").Title);
        Assert.Single(h.Logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Down", StringComparison.Ordinal));
        h.Handler.Titles.Remove("down.example");
        h.Handler.Down.Add("down.example");
        await h.AfterAsync(TitleSampler.SampleInterval);
        Assert.Equal(3, h.Logger.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task The_last_good_time_is_kept_while_failing()
    {
        // RA S29: UpdatedUtc says when the stream last answered with its metadata, not when a sample last failed.
        var h = new Harness();
        h.Handler.Titles["down.example"] = "Was - Fine";
        h.Handler.Down.Remove("down.example");
        h.Play("down");
        await h.RoundAsync();
        var good = h.Clock.Now.UtcDateTime;

        h.Handler.Down.Add("down.example");
        await h.AfterAsync(TitleSampler.SampleInterval);

        var heard = h.Titles.Snapshot("down");
        Assert.Equal(StationTitles.StateFailing, heard.State);
        Assert.Equal(good, heard.LastGoodUtc);
    }

    [Fact]
    public async Task A_failing_stream_does_not_touch_another()
    {
        var h = new Harness();
        h.Play("down", "plain");

        await h.RoundAsync();

        Assert.Equal(StationTitles.StateFailing, h.StateOf("down"));
        Assert.Equal("Artist - Title", h.Titles.Snapshot("plain").Title);
    }

    [Fact]
    public async Task The_state_goes_a_few_intervals_after_the_last_listener()
    {
        var h = new Harness();
        h.Play("plain");
        await h.RoundAsync();
        Assert.Equal(StationTitles.StateTitle, h.StateOf("plain"));

        // The listener stops: no more requests, and the state stays for IdleIntervalsKept intervals, then goes.
        h.Play();
        var seen = h.Handler.Seen.Count;
        for (var i = 1; i <= TitleSampler.IdleIntervalsKept; i++)
        {
            await h.AfterAsync(TitleSampler.SampleInterval);
            Assert.Equal(StationTitles.StateTitle, h.StateOf("plain"));
        }

        await h.AfterAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(StationTitles.StateWaiting, h.StateOf("plain"));
        Assert.Equal(0, h.Titles.Count());
        Assert.Equal(seen, h.Handler.Seen.Count);
    }

    [Fact]
    public async Task A_stream_that_leaves_the_list_loses_its_state_at_once()
    {
        var h = new Harness();
        h.Play("plain");
        await h.RoundAsync();

        h.Config.Stations[0].Enabled = false;
        await h.AfterAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(StationTitles.StateWaiting, h.StateOf("plain"));
        Assert.Single(h.Handler.Seen);
    }

    [Fact]
    public async Task A_station_that_became_sub_wave_is_not_sampled_and_loses_its_title()
    {
        var h = new Harness();
        h.Play("plain");
        await h.RoundAsync();

        h.Config.Stations[0].Kind = StationKind.SubWave;
        h.Config.Stations[0].BaseUrl = "http://now-subwave.example";
        await h.AfterAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(StationTitles.StateWaiting, h.StateOf("plain"));
        Assert.Single(h.Handler.Seen);
    }

    // ---- Card L13: the last read's outcome and time, for the configuration page's health ----

    private static Uri UrlOf(Harness h, string id) => new(h.Config.Stations.Single(s => s.Id == id).StreamUrl);

    [Fact]
    public async Task Each_read_records_whether_the_stream_answered_and_when()
    {
        var h = new Harness();
        h.Play("plain", "quiet", "down", "moved");

        await h.RoundAsync();

        foreach (var (id, outcome) in new[] { ("plain", TitleReadOutcome.Answered), ("quiet", TitleReadOutcome.Answered), ("down", TitleReadOutcome.NoAnswer), ("moved", TitleReadOutcome.Moved) })
        {
            var read = h.Titles.LastRead(id, UrlOf(h, id));
            Assert.NotNull(read);
            Assert.Equal(outcome, read!.Outcome);
            Assert.Equal(h.Clock.Now.UtcDateTime, read.AtUtc);
        }
    }

    [Fact]
    public async Task A_stream_that_stops_answering_reads_no_answer_with_the_time_of_that_read()
    {
        var h = new Harness();
        h.Play("plain");
        await h.RoundAsync();
        var firstUtc = h.Clock.Now.UtcDateTime;

        h.Handler.Down.Add("cdn.example");
        await h.AfterAsync(TitleSampler.SampleInterval);

        var read = h.Titles.LastRead("plain", UrlOf(h, "plain"))!;
        Assert.Equal(TitleReadOutcome.NoAnswer, read.Outcome);
        Assert.Equal(firstUtc + TitleSampler.SampleInterval, read.AtUtc);
    }

    [Fact]
    public async Task A_stream_that_answers_but_breaks_off_before_its_title_is_answered()
    {
        // Fix round S2: a success status with its headers means the stream is up, even when the title does not arrive.
        var h = new Harness();
        h.Config.Stations[2].StreamUrl = "http://short.example/s.mp3";   // "quiet"
        h.Play("quiet");

        await h.RoundAsync();

        Assert.Equal(StationTitles.StateFailing, h.StateOf("quiet"));   // the page's title line still says it is delayed
        Assert.Equal(TitleReadOutcome.Answered, h.Titles.LastRead("quiet", UrlOf(h, "quiet"))!.Outcome);
    }

    [Fact]
    public async Task An_unexpected_failure_is_no_answer()
    {
        // RA S3 (D3): a failure that is not a station's (a bug) records no answer, never answered.
        var h = new Harness();
        h.Config.Stations[2].StreamUrl = "http://bug.example/b.mp3";   // "quiet"
        h.Play("quiet");

        await h.RoundAsync();

        Assert.Equal(StationTitles.StateFailing, h.StateOf("quiet"));
        Assert.Equal(TitleReadOutcome.NoAnswer, h.Titles.LastRead("quiet", UrlOf(h, "quiet"))!.Outcome);
        Assert.Contains(h.Logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task An_hls_stream_and_an_unplayed_stream_have_no_read()
    {
        var h = new Harness();
        h.Play("hls");

        await h.RoundAsync();

        Assert.Null(h.Titles.LastRead("hls", UrlOf(h, "hls")));
        Assert.Null(h.Titles.LastRead("quiet", UrlOf(h, "quiet")));
        Assert.Equal(0, h.Titles.ReadCount());
    }

    [Fact]
    public async Task The_read_outlives_the_title_state_and_switching_titles_off()
    {
        var h = new Harness();
        h.Play("plain");
        await h.RoundAsync();
        var readUtc = h.Clock.Now.UtcDateTime;

        // Nobody plays it any more: the title state goes after a few intervals, the read stays (it is history).
        h.Play();
        await h.AfterAsync(TimeSpan.FromHours(2));
        Assert.Equal(StationTitles.StateWaiting, h.StateOf("plain"));
        Assert.Equal(readUtc, h.Titles.LastRead("plain", UrlOf(h, "plain"))!.AtUtc);

        h.Config.NowPlayingEnabled = false;
        await h.AfterAsync(TitleSampler.SampleInterval);
        Assert.Equal(StationTitles.StateOff, h.StateOf("plain"));
        Assert.Equal(TitleReadOutcome.Answered, h.Titles.LastRead("plain", UrlOf(h, "plain"))!.Outcome);
    }

    [Fact]
    public async Task A_changed_url_or_a_station_that_left_the_list_has_no_read()
    {
        var h = new Harness();
        h.Play("plain", "quiet");
        await h.RoundAsync();
        Assert.Equal(2, h.Titles.ReadCount());

        // The admin points "plain" at another stream: the old read is not shown for it, at once and after the next round.
        var oldUrl = UrlOf(h, "plain");
        h.Config.Stations[0].StreamUrl = "http://cdn.example/other.mp3";
        Assert.Null(h.Titles.LastRead("plain", UrlOf(h, "plain")));
        Assert.NotNull(h.Titles.LastRead("plain", oldUrl));

        h.Play();
        h.Config.Stations[2].Enabled = false;   // "quiet" leaves the list
        await h.AfterAsync(TimeSpan.FromSeconds(1));
        Assert.Null(h.Titles.LastRead("plain", oldUrl));
        Assert.Null(h.Titles.LastRead("quiet", UrlOf(h, "quiet")));
        Assert.Equal(0, h.Titles.ReadCount());
    }

    [Fact]
    public void The_read_record_keeps_no_url()
    {
        // Only a hash of the URL is kept: no field of the record's type is a string or a Uri.
        var record = typeof(StationTitles).GetNestedTypes(BindingFlags.NonPublic).Single(t => t.Name == "ReadEntry");
        var fields = record.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotEmpty(fields);
        Assert.DoesNotContain(fields, f => f.FieldType == typeof(string) || f.FieldType == typeof(Uri));
        Assert.DoesNotContain(typeof(TitleRead).GetProperties(), p => p.PropertyType == typeof(string) || p.PropertyType == typeof(Uri));
    }

    [Fact]
    public async Task No_channel_yet_means_no_request()
    {
        var h = new Harness();
        h.Library.Items.Clear();
        h.Play("plain");

        await h.RoundAsync();

        Assert.Empty(h.Handler.Seen);
    }

    [Fact]
    public async Task A_failing_item_lookup_samples_nothing_this_round()
    {
        var h = new Harness();
        h.Library.OnQuery = _ => throw new InvalidOperationException("library locked");
        h.Play("plain");

        await h.RoundAsync();

        Assert.Empty(h.Handler.Seen);
        Assert.DoesNotContain(h.Logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task No_configuration_means_no_request()
    {
        var h = new Harness();
        h.Play("plain");
        var sampler = new TitleSampler(new StationClient(new Factory(h.Handler)), h.Titles, new LiveItemFinder(h.Library.Manager, () => new List<IChannel>(), NullLogger<LiveItemFinder>.Instance), h.Playing, null!, h.Logger, () => null, h.Clock);

        await sampler.SampleOnceAsync(CancellationToken.None);

        Assert.Empty(h.Handler.Seen);
        Assert.Equal(0, h.Playing.Asked);
    }

    [Fact]
    public async Task With_show_what_s_playing_off_nothing_is_sampled_and_every_station_reads_off()
    {
        // HI 2026-10-28: the one checkbox covers SUB/WAVE now playing and plain-stream titles.
        var h = new Harness();
        h.Play("plain");
        await h.RoundAsync();
        Assert.Equal(StationTitles.StateTitle, h.StateOf("plain"));

        h.Config.NowPlayingEnabled = false;
        var seen = h.Handler.Seen.Count;
        var asked = h.Playing.Asked;
        for (var i = 0; i < 3; i++)
        {
            await h.AfterAsync(TitleSampler.SampleInterval);
        }

        Assert.Equal(seen, h.Handler.Seen.Count);
        Assert.Equal(asked, h.Playing.Asked);   // not even the sessions are asked
        var off = h.Titles.Snapshot("plain");
        Assert.Equal(StationTitles.StateOff, off.State);
        Assert.Null(off.Title);
        Assert.Equal(StationTitles.StateOff, h.StateOf("quiet"));
        Assert.Equal(0, h.Titles.Count());

        h.Config.NowPlayingEnabled = true;
        await h.AfterAsync(TitleSampler.SampleInterval);
        Assert.Equal(seen + 1, h.Handler.Seen.Count);
        Assert.Equal("Artist - Title", h.Titles.Snapshot("plain").Title);
    }

    [Fact]
    public async Task With_show_what_s_playing_on_by_default_titles_are_sampled()
    {
        var h = new Harness();
        Assert.True(h.Config.NowPlayingEnabled);
        h.Play("plain");

        await h.RoundAsync();

        Assert.Single(h.Handler.Seen);
        Assert.Equal(StationTitles.StateTitle, h.StateOf("plain"));
    }

    [Fact]
    public async Task At_most_four_samples_run_at_once()
    {
        var h = new Harness();
        for (var i = 0; i < 6; i++)
        {
            var id = "s" + i;
            h.Config.Stations.Add(new StationDefinition { Id = id, Name = id, Kind = StationKind.Stream, StreamUrl = "http://slow" + i + ".example/x.mp3", SortOrder = 10 + i });
            var item = new Audio { Id = Guid.NewGuid(), ExternalId = id };
            h.Items[id] = item.Id;
            h.Library.ListedItems.Add(item);
        }

        var gate = new SemaphoreSlim(0);
        var inFlight = 0;
        var most = 0;
        var started = new SemaphoreSlim(0);
        var slow = new BlockingHandler(async () =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref most, now);
            started.Release();
            await gate.WaitAsync();
            Interlocked.Decrement(ref inFlight);
        });
        var sampler = new TitleSampler(new StationClient(new Factory(slow)), h.Titles, new LiveItemFinder(h.Library.Manager, () => new List<IChannel>(), NullLogger<LiveItemFinder>.Instance), h.Playing, null!, h.Logger, () => h.Config, h.Clock);
        h.Play("s0", "s1", "s2", "s3", "s4", "s5");

        var round = sampler.SampleOnceAsync(CancellationToken.None);
        for (var i = 0; i < 4; i++)
        {
            Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.False(await started.WaitAsync(TimeSpan.FromMilliseconds(300)));   // the fifth waits for a free slot
        gate.Release(6);
        await round.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TitleSampler.MaxConcurrentSamples, most);
        Assert.Equal(6, slow.Requests);
    }

    [Fact]
    public async Task A_stream_that_breaks_off_is_failing_with_its_own_category()
    {
        var h = new Harness();
        var broken = new BlockingHandler(() => Task.CompletedTask) { Body = () => new System.IO.MemoryStream(new byte[100]), MetaInt = "1000" };
        var sampler = new TitleSampler(new StationClient(new Factory(broken)), h.Titles, new LiveItemFinder(h.Library.Manager, () => new List<IChannel>(), NullLogger<LiveItemFinder>.Instance), h.Playing, null!, h.Logger, () => h.Config, h.Clock);
        h.Play("plain");

        await sampler.SampleOnceAsync(CancellationToken.None);

        Assert.Equal(StationTitles.StateFailing, h.StateOf("plain"));
        var warning = Assert.Single(h.Logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("the stream ended or broke off before its title", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task A_colliding_channel_samples_nothing()
    {
        var h = new Harness();
        var other = OtherPluginChannel.Named(Tile);
        var sampler = new TitleSampler(new StationClient(new Factory(h.Handler)), h.Titles, new LiveItemFinder(h.Library.Manager, () => new List<IChannel> { other }, NullLogger<LiveItemFinder>.Instance), h.Playing, null!, h.Logger, () => h.Config, h.Clock);
        h.Play("plain");

        await sampler.SampleOnceAsync(CancellationToken.None);

        Assert.Empty(h.Handler.Seen);
        Assert.Equal(StationTitles.StateWaiting, h.StateOf("plain"));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        for (var seen = Volatile.Read(ref target); value > seen; seen = Volatile.Read(ref target))
        {
            if (Interlocked.CompareExchange(ref target, value, seen) == seen)
            {
                return;
            }
        }
    }

    /// <summary>Waits on a hook before answering 200 with an icy-metaint and a body (counts requests).</summary>
    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly Func<Task> _hook;
        private int _requests;

        public BlockingHandler(Func<Task> hook) => _hook = hook;

        public int Requests => _requests;

        public Func<System.IO.Stream>? Body { get; init; }

        public string MetaInt { get; init; } = "100";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            await _hook();
            var block = IcyMetadataTests.Block("StreamTitle='x';");
            var bytes = new byte[100].Concat(new[] { (byte)(block.Length / IcyMetadata.BlockUnit) }).Concat(block).ToArray();
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(Body?.Invoke() ?? new System.IO.MemoryStream(bytes)) };
            response.Headers.TryAddWithoutValidation("icy-metaint", MetaInt);
            return response;
        }
    }

    [Fact]
    public void The_interval_and_bounds_are_the_card_s()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), TitleSampler.SampleInterval);
        Assert.Equal(3, TitleSampler.IdleIntervalsKept);
        Assert.Equal(4, TitleSampler.MaxConcurrentSamples);
    }

    /// <summary>
    /// A minimal <see cref="ISessionManager"/>: its sessions and its playback start handlers (card L14), anything else throws.
    /// </summary>
    public class FakeSessions : DispatchProxy
    {
        public List<SessionInfo> List { get; } = new();

        public List<EventHandler<PlaybackProgressEventArgs>> StartHandlers { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_Sessions":
                    return List;
                case "add_PlaybackStart":
                    StartHandlers.Add((EventHandler<PlaybackProgressEventArgs>)args![0]!);
                    return null;
                case "remove_PlaybackStart":
                    StartHandlers.Remove((EventHandler<PlaybackProgressEventArgs>)args![0]!);
                    return null;
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }

    [Fact]
    public void The_session_view_lists_what_every_session_plays()
    {
        var proxy = DispatchProxy.Create<ISessionManager, FakeSessions>();
        var sessions = (FakeSessions)(object)proxy;
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        sessions.List.Add(new SessionInfo(proxy, NullLogger.Instance) { NowPlayingItem = new BaseItemDto { Id = a } });
        sessions.List.Add(new SessionInfo(proxy, NullLogger.Instance) { NowPlayingItem = new BaseItemDto { Id = b } });
        sessions.List.Add(new SessionInfo(proxy, NullLogger.Instance) { NowPlayingItem = new BaseItemDto { Id = a } });
        sessions.List.Add(new SessionInfo(proxy, NullLogger.Instance));   // signed in, playing nothing
        sessions.List.Add(new SessionInfo(proxy, NullLogger.Instance) { NowPlayingItem = new BaseItemDto { Id = Guid.Empty } });

        var ids = new SessionPlayingItems(proxy).PlayingItemIds();

        Assert.Equal(new[] { a, b }.Order(), ids.Order());
        sessions.List.Clear();
        Assert.Empty(new SessionPlayingItems(proxy).PlayingItemIds());
    }
}
