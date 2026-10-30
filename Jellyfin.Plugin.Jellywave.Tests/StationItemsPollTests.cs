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
/// Cards L2 and L4 through the real poll: every SUB/WAVE station drives its own item's now playing and cover; the plain
/// streams get their logo (their own, else the embedded one) and every item a sort name that carries its place.
/// </summary>
public class StationItemsPollTests
{
    private const string Tile = "Live Radio";
    private const string PrimaryHost = "primary.example";
    private const string SecondHost = "second.example";
    private const string LogoUrl = "https://cdn.example/paradise.gif";

    private sealed class Stations : HttpMessageHandler
    {
        public List<string> Seen { get; } = new();

        public HashSet<string> CoverArt { get; } = new();

        public HashSet<string> MoreLogos { get; } = new();

        /// <summary>Gets or sets what to call when the logo is requested (to see what the library was asked before).</summary>
        public Action? OnLogo { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            lock (Seen)
            {
                Seen.Add(url.Host + url.AbsolutePath);
            }

            if (url.AbsoluteUri == LogoUrl)
            {
                OnLogo?.Invoke();
            }

            if (url.AbsoluteUri == LogoUrl || MoreLogos.Contains(url.AbsoluteUri) || CoverArt.Contains(url.Host + url.AbsolutePath))
            {
                var gif = url.AbsoluteUri == LogoUrl || MoreLogos.Contains(url.AbsoluteUri);
                var image = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(gif ? ImageFixtures.Gif(64, 64) : ImageFixtures.For("image/jpeg")) };
                image.Content.Headers.ContentType = new MediaTypeHeaderValue(gif ? "image/gif" : "image/jpeg");
                return Task.FromResult(image);
            }

            var body = url.AbsolutePath switch
            {
                "/api/now-playing" => """{"nowPlaying":{"artist":"Artist","title":"Song","subsonic_id":"cov1"},"streamOnline":true}""",
                "/api/state" => "{}",
                _ => null
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
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
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class Server
    {
        public Server()
        {
            var channel = new Channel { Id = Library.IdFor("Channel " + Tile, typeof(Channel)), Name = Tile };
            Library.Items[channel.Id] = channel;
            foreach (var item in new[] { Primary, Plain, Second, Refused })
            {
                Library.ListedItems.Add(item);
            }

            var client = new StationClient(new StubFactory(Http));
            Artwork = new LiveItemArtwork(client, SaveImageAsync, NullLogger<LiveItemArtwork>.Instance);
            Poller = new NowPlayingService(
                client,
                State,
                Artwork,
                new StationLogos(client, SaveImageAsync, NullLogger<StationLogos>.Instance, TimeProvider.System),
                new LiveItemFinder(Library.Manager, () => new List<IChannel>(), NullLogger<LiveItemFinder>.Instance),
                Health,
                new ScheduleCache(client),
                Library.Manager,
                null!,
                Log,
                () => Config);
        }

        public FakeLibrary Library { get; } = FakeLibrary.Create();

        public Stations Http { get; } = new();

        public Audio Primary { get; } = new() { Id = Guid.NewGuid(), ExternalId = "live" };

        public Audio Plain { get; } = new() { Id = Guid.NewGuid(), ExternalId = "plain" };

        public Audio Second { get; } = new() { Id = Guid.NewGuid(), ExternalId = "second" };

        public Audio Refused { get; } = new() { Id = Guid.NewGuid(), ExternalId = "refused" };

        public PluginConfiguration Config { get; } = new()
        {
            LibraryName = Tile,
            Stations =
            {
                new StationDefinition { Id = "plain", Name = "Radio Paradise", Kind = StationKind.Stream, StreamUrl = "https://cdn.example/live.mp3", LogoUrl = LogoUrl, SortOrder = 0 },
                new StationDefinition { Id = "live", Name = "IHateRadio", BaseUrl = "http://" + PrimaryHost, SortOrder = 1 },
                new StationDefinition { Id = "second", Name = "Station Two", BaseUrl = "http://" + SecondHost, SortOrder = 2 },
                new StationDefinition { Id = "refused", Name = "Refused", Kind = StationKind.Stream, StreamUrl = "ftp://cdn.example/x.mp3", SortOrder = 3 },
            }
        };

        public NowPlayingService Poller { get; }

        public NowPlayingState State { get; } = new();

        public PollerHealth Health { get; } = new();

        public RecordingLogger Log { get; } = new();

        /// <summary>Gets the external ids whose image saves throw (metadata folder not writable).</summary>
        public HashSet<string> FailSaves { get; } = new();

        public LiveItemArtwork Artwork { get; }

        public List<(string? Item, string Mime)> Saved { get; } = new();

        public Task PollAsync() => Poller.PollOnceAsync(CancellationToken.None);

        private Task SaveImageAsync(BaseItem item, Stream stream, string mime, CancellationToken cancellationToken)
        {
            if (FailSaves.Contains(item.ExternalId))
            {
                throw new IOException("disk full");
            }

            Saved.Add((item.ExternalId, mime));
            item.SetImagePath(ImageType.Primary, "http://saved.example/" + Guid.NewGuid().ToString("N"));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Every_sub_wave_station_drives_its_own_item()
    {
        // Card L4: both SUB/WAVE stations are polled; each item gets its station's line and cover (or the logo without art).
        var server = new Server();
        server.Http.CoverArt.Add(PrimaryHost + "/api/cover/cov1");

        await server.PollAsync();

        Assert.Contains(PrimaryHost + "/api/now-playing", server.Http.Seen);
        Assert.Contains(SecondHost + "/api/now-playing", server.Http.Seen);
        Assert.StartsWith("Now playing: Artist – Song", server.Primary.Overview, StringComparison.Ordinal);
        Assert.StartsWith("Now playing: Artist – Song", server.Second.Overview, StringComparison.Ordinal);
        Assert.Equal(JellywaveChannel.DefaultOverview, server.Plain.Overview);
        Assert.Contains(("live", "image/jpeg"), server.Saved);
        Assert.Contains(("second", ImageSniffer.Png), server.Saved);
        Assert.Equal("cov1", server.Artwork.Applied("live"));
        Assert.Equal(LiveItemArtwork.LogoKey, server.Artwork.Applied("second"));
    }

    [Fact]
    public async Task The_other_items_get_their_logo_once()
    {
        var server = new Server();

        await server.PollAsync();
        await server.PollAsync();

        Assert.Single(server.Saved, s => s.Item == "plain");
        Assert.Contains(("plain", ImageSniffer.Gif), server.Saved);
        Assert.Single(server.Saved, s => s.Item == "second");
        Assert.Contains(("second", ImageSniffer.Png), server.Saved);
        Assert.Single(server.Http.Seen, s => s == "cdn.example/paradise.gif");
        Assert.DoesNotContain(server.Saved, s => s.Item == "refused");
    }

    [Fact]
    public async Task The_logo_is_downloaded_before_the_item_it_is_saved_on_is_read()
    {
        // Card 7: hold the instance that is written for milliseconds, not for a download of up to 8 s.
        var server = new Server();
        var queriesBeforeDownload = -1;
        server.Http.OnLogo = () => queriesBeforeDownload = server.Library.Queries.Count;

        await server.PollAsync();

        Assert.True(queriesBeforeDownload >= 0);
        Assert.Contains(server.Library.Queries.Skip(queriesBeforeDownload), q => q.ExternalId == "plain");
        Assert.Contains(("plain", ImageSniffer.Gif), server.Saved);
    }

    [Fact]
    public async Task No_logo_is_downloaded_for_an_item_that_does_not_exist_yet()
    {
        var server = new Server();
        server.Library.ListedItems.Remove(server.Plain);

        await server.PollAsync();
        await server.PollAsync();

        Assert.DoesNotContain("cdn.example/paradise.gif", server.Http.Seen);
    }

    [Fact]
    public async Task A_plain_stream_s_notes_become_its_overview_and_follow_an_edit()
    {
        // Card L8: Jellyfin applies an overview only when it creates the item, so the poller writes edited notes.
        var server = new Server();
        server.Config.Stations[0].Notes = "Eclectic, listener-supported.";

        await server.PollAsync();
        Assert.Equal("Eclectic, listener-supported.", server.Plain.Overview);
        var writes = server.Library.Updated.Count;

        await server.PollAsync();
        Assert.Equal(writes, server.Library.Updated.Count);   // unchanged notes: nothing written

        server.Config.Stations[0].Notes = new string('n', 400);
        await server.PollAsync();
        Assert.Equal(StationDefinition.MaxNotesLength, server.Plain.Overview!.Length);

        server.Config.Stations[0].Notes = string.Empty;
        await server.PollAsync();
        Assert.Equal(JellywaveChannel.DefaultOverview, server.Plain.Overview);
        Assert.StartsWith("Now playing: Artist – Song", server.Primary.Overview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_listed_item_gets_its_place_as_its_sort_name_and_a_second_poll_writes_nothing()
    {
        var server = new Server();

        await server.PollAsync();
        var writes = server.Library.Updated.Count;
        await server.PollAsync();

        Assert.Equal("0000 Radio Paradise", server.Plain.ForcedSortName);
        Assert.Equal("0001 IHateRadio", server.Primary.ForcedSortName);
        Assert.Equal("0002 Station Two", server.Second.ForcedSortName);
        Assert.Null(server.Refused.ForcedSortName);
        Assert.Equal(writes, server.Library.Updated.Count);
    }

    [Fact]
    public async Task A_renamed_station_renames_its_item_once_and_a_reverted_name_is_repaired()
    {
        // Card L3: Jellyfin sets a channel item's name only when it creates it, so the poller follows a rename.
        var server = new Server();
        server.Primary.Name = "IHateRadio — Live";
        server.Plain.Name = "Radio Paradise — Live";
        server.Second.Name = "Station Two — Live";
        await server.PollAsync();

        server.Config.Stations[1].Name = "  Night Radio ";
        server.Config.Stations[0].Name = string.Empty;
        server.Library.Updated.Clear();
        await server.PollAsync();

        Assert.Equal("Night Radio — Live", server.Primary.Name);
        Assert.Equal("cdn.example — Live", server.Plain.Name);   // card L11: a blank plain stream is named after its host
        Assert.Equal("Station Two — Live", server.Second.Name);
        Assert.Null(server.Refused.Name);
        Assert.Contains(server.Primary, server.Library.Updated);
        Assert.Contains(server.Plain, server.Library.Updated);
        Assert.DoesNotContain(server.Second, server.Library.Updated);
        Assert.Contains(server.Log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Night Radio item renamed", StringComparison.Ordinal));

        // A re-save by Jellyfin's own refresh from an older copy is repaired on the next poll; an unchanged name writes nothing.
        server.Primary.Name = "IHateRadio — Live";
        server.Library.Updated.Clear();
        await server.PollAsync();
        Assert.Equal("Night Radio — Live", server.Primary.Name);
        Assert.Single(server.Library.Updated);
        server.Library.Updated.Clear();
        await server.PollAsync();
        Assert.Empty(server.Library.Updated);
    }

    [Fact]
    public async Task A_rename_that_only_changes_case_is_written()
    {
        var server = new Server();
        server.Primary.Name = "IHateRadio — Live";
        server.Config.Stations[1].Name = "IHATERADIO";

        await server.PollAsync();

        Assert.Equal("IHATERADIO — Live", server.Primary.Name);
    }

    [Fact]
    public async Task A_reorder_moves_the_sort_names()
    {
        var server = new Server();
        await server.PollAsync();

        server.Config.Stations[0].SortOrder = 9;
        await server.PollAsync();

        Assert.Equal("0002 Radio Paradise", server.Plain.ForcedSortName);
        Assert.Equal("0000 IHateRadio", server.Primary.ForcedSortName);
    }

    [Fact]
    public async Task With_polling_off_the_logos_are_still_kept()
    {
        var server = new Server();
        server.Config.NowPlayingEnabled = false;

        await server.PollAsync();

        Assert.DoesNotContain(server.Http.Seen, s => s.Contains("/api/", StringComparison.Ordinal));
        Assert.Contains(("plain", ImageSniffer.Gif), server.Saved);
        Assert.Contains(("second", ImageSniffer.Png), server.Saved);
        Assert.Equal(JellywaveChannel.DefaultOverview, server.Primary.Overview);
    }

    [Fact]
    public async Task A_reorder_keeps_each_station_s_cover_on_its_own_item()
    {
        // Card L4: the L2 primary switch is gone; which station is primary no longer decides who gets a cover.
        var server = new Server();
        server.Http.CoverArt.Add(PrimaryHost + "/api/cover/cov1");
        server.Http.CoverArt.Add(SecondHost + "/api/cover/cov1");
        await server.PollAsync();
        var saves = server.Saved.Count;

        // "second" moves ahead of "live": it is the primary station now.
        server.Config.Stations[2].SortOrder = -1;
        await server.PollAsync();

        Assert.Equal(saves, server.Saved.Count);
        Assert.Equal("image/jpeg", server.Saved.Last(s => s.Item == "live").Mime);
        Assert.Equal("image/jpeg", server.Saved.Last(s => s.Item == "second").Mime);
        Assert.StartsWith("Now playing:", server.Primary.Overview, StringComparison.Ordinal);
        Assert.StartsWith("Now playing:", server.Second.Overview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_duplicate_primary_lends_nothing_to_the_item_that_holds_its_id()
    {
        // RA R1: a stream with the id "live" sorts ahead of the SUB/WAVE station "live", which is refused as a duplicate.
        var server = new Server();
        server.Config.Stations.Clear();
        server.Config.Stations.Add(new StationDefinition { Id = "live", Name = "Stream", Kind = StationKind.Stream, StreamUrl = "https://cdn.example/live.mp3", LogoUrl = LogoUrl, SortOrder = 0 });
        server.Config.Stations.Add(new StationDefinition { Id = "live", Name = "IHateRadio", BaseUrl = "http://" + PrimaryHost, SortOrder = 1 });
        server.Http.CoverArt.Add(PrimaryHost + "/api/cover/cov1");

        for (var poll = 0; poll < 3; poll++)
        {
            await server.PollAsync();
        }

        Assert.DoesNotContain(server.Http.Seen, s => s.StartsWith(PrimaryHost, StringComparison.Ordinal));
        Assert.Equal(JellywaveChannel.DefaultOverview, server.Primary.Overview);
        Assert.Equal(new[] { ("live", ImageSniffer.Gif) }, server.Saved.Where(s => s.Item == "live").Select(s => (s.Item!, s.Mime)));
        Assert.Single(server.Http.Seen, s => s == "cdn.example/paradise.gif");
        Assert.Null(server.Artwork.Applied("live"));
    }

    [Fact]
    public async Task Two_stations_on_one_origin_each_get_the_cover()
    {
        // L2 RA S6, now per station: the applied cover is kept per station id, so a shared origin and track never make
        // one station's item skip its cover because the other's already shows it.
        var server = new Server();
        server.Config.Stations[2].BaseUrl = "http://" + PrimaryHost;
        server.Http.CoverArt.Add(PrimaryHost + "/api/cover/cov1");

        await server.PollAsync();

        Assert.Equal("image/jpeg", server.Saved.Last(s => s.Item == "live").Mime);
        Assert.Equal("image/jpeg", server.Saved.Last(s => s.Item == "second").Mime);
        Assert.Equal(2, server.Http.Seen.Count(s => s == PrimaryHost + "/api/cover/cov1"));
    }

    [Fact]
    public async Task A_failing_save_is_logged_once_per_station_and_does_not_download_again()
    {
        // RA S1.
        var server = new Server();
        server.Config.Stations[2] = new StationDefinition { Id = "second", Name = "Station Two", Kind = StationKind.Stream, StreamUrl = "https://cdn.example/two.mp3", LogoUrl = "https://cdn.example/two.gif", SortOrder = 2 };
        server.Http.MoreLogos.Add("https://cdn.example/two.gif");
        server.FailSaves.Add("plain");
        server.FailSaves.Add("second");

        await server.PollAsync();
        await server.PollAsync();
        await server.PollAsync();

        Assert.Single(server.Http.Seen, s => s == "cdn.example/paradise.gif");
        Assert.Single(server.Http.Seen, s => s == "cdn.example/two.gif");
        var errors = server.Log.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToList();
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("Radio Paradise", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Station Two", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_poll_downloads_at_most_the_budget_and_the_rest_follow_next_poll()
    {
        // RA S2: logo downloads must not hold up now playing for long.
        var server = new Server();
        for (var i = 0; i < 3; i++)
        {
            var url = "https://cdn.example/extra" + i + ".gif";
            server.Http.MoreLogos.Add(url);
            server.Config.Stations.Add(new StationDefinition { Id = "x" + i, Name = "Extra " + i, Kind = StationKind.Stream, StreamUrl = "https://cdn.example/x.mp3", LogoUrl = url, SortOrder = 10 + i });
            server.Library.ListedItems.Add(new Audio { Id = Guid.NewGuid(), ExternalId = "x" + i });
        }

        await server.PollAsync();
        var first = server.Http.Seen.Count(s => s.StartsWith("cdn.example/", StringComparison.Ordinal));
        await server.PollAsync();
        var second = server.Http.Seen.Count(s => s.StartsWith("cdn.example/", StringComparison.Ordinal));

        Assert.Equal(StationLogos.MaxDownloadsPerPoll, first);
        Assert.Equal(4, second);   // paradise.gif plus the three extras
        Assert.Contains(PrimaryHost + "/api/now-playing", server.Http.Seen);
    }

    [Fact]
    public async Task Over_budget_every_new_item_gets_an_image_at_once_and_its_real_logo_later()
    {
        // RA F1: the deferred items show the embedded logo, not a blank card, until their download's turn.
        var server = new Server();
        var extras = new List<Audio>();
        for (var i = 0; i < 3; i++)
        {
            var url = "https://cdn.example/extra" + i + ".gif";
            server.Http.MoreLogos.Add(url);
            server.Config.Stations.Add(new StationDefinition { Id = "x" + i, Name = "Extra " + i, Kind = StationKind.Stream, StreamUrl = "https://cdn.example/x.mp3", LogoUrl = url, SortOrder = 10 + i });
            var item = new Audio { Id = Guid.NewGuid(), ExternalId = "x" + i };
            extras.Add(item);
            server.Library.ListedItems.Add(item);
        }

        var streams = new[] { "plain", "x0", "x1", "x2" };
        await server.PollAsync();

        Assert.All(extras.Append(server.Plain).Append(server.Second), i => Assert.True(i.HasImage(ImageType.Primary)));
        Assert.Equal(StationLogos.MaxDownloadsPerPoll, streams.Count(id => server.Saved.Any(s => s.Item == id && s.Mime == ImageSniffer.Gif)));

        await server.PollAsync();

        Assert.All(streams, id => Assert.Equal(ImageSniffer.Gif, server.Saved.Last(s => s.Item == id).Mime));
    }
}
