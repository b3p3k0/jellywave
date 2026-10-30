using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L2: a plain stream's logo is downloaded by the plugin (no redirects, sniffed), saved as the item's Primary, and
/// fetched again only when the station id + logo URL key changes or the image is gone. A failed logo is remembered for
/// <see cref="StationLogos.FailedLogoRetry"/>; meanwhile, and without a usable logo URL, the embedded logo stands in.
/// </summary>
public class StationLogosTests
{
    private const string LogoUrl = "https://cdn.example/logo.png?size=512";

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingLogger : ILogger<StationLogos>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>Answers each URL from <see cref="Respond"/>; anything else is a 404. Records every request.</summary>
    private sealed class Server : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Respond { get; } = new();

        public List<string> Seen { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Seen.Add(url);
            return Task.FromResult(Respond.TryGetValue(url, out var answer) ? answer() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class Harness
    {
        public Harness()
        {
            Logos = new StationLogos(new StationClient(new StubFactory(Server)), SaveAsync, Log, Clock);
        }

        public Server Server { get; } = new();

        public ManualClock Clock { get; } = new();

        public RecordingLogger Log { get; } = new();

        public StationLogos Logos { get; }

        public Audio Item { get; } = new();

        public List<string> Saved { get; } = new();

        public StationDefinition Station { get; } = new() { Id = "c07e", Name = "Radio Paradise", Kind = StationKind.Stream, StreamUrl = "https://cdn.example/live.mp3", LogoUrl = LogoUrl };

        public void LogoIs(string url, string declared, byte[] bytes)
            => Server.Respond[url] = () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue(declared);
                return response;
            };

        /// <summary>One reconcile as the poller does it: prepare with the item's image state, then apply.</summary>
        public async Task<bool> ReconcileAsync()
        {
            Logos.BeginPoll();
            var prepared = await Logos.PrepareAsync(Station, Item.HasImage(ImageType.Primary), CancellationToken.None);
            return prepared is not null && await Logos.ApplyAsync(Item, prepared, CancellationToken.None);
        }

        public int Requests(string url) => Server.Seen.Count(s => s == url);

        /// <summary>Gets or sets a value indicating whether saving throws (metadata folder not writable).</summary>
        public bool SaveThrows { get; set; }

        public List<string> Attempts { get; } = new();

        private Task SaveAsync(BaseItem item, Stream stream, string mime, CancellationToken cancellationToken)
        {
            Attempts.Add(mime);
            if (SaveThrows)
            {
                throw new IOException("disk full");
            }

            Saved.Add(mime);
            item.SetImagePath(ImageType.Primary, "http://saved.example/" + Saved.Count);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task The_logo_is_downloaded_sniffed_and_saved_once()
    {
        var h = new Harness();
        h.LogoIs(LogoUrl, "image/jpeg", ImageFixtures.Png(256, 256));   // the bytes decide the type, not the header

        Assert.True(await h.ReconcileAsync());
        Assert.False(await h.ReconcileAsync());
        Assert.False(await h.ReconcileAsync());

        Assert.Equal(new[] { ImageSniffer.Png }, h.Saved);
        Assert.Equal(1, h.Requests(LogoUrl));
    }

    [Fact]
    public async Task A_missing_image_is_downloaded_again()
    {
        var h = new Harness();
        h.LogoIs(LogoUrl, "image/png", ImageFixtures.Png(256, 256));
        await h.ReconcileAsync();

        h.Item.ImageInfos = [];   // a re-save by Jellyfin dropped it
        Assert.True(await h.ReconcileAsync());

        Assert.Equal(2, h.Requests(LogoUrl));
        Assert.Equal(new[] { ImageSniffer.Png, ImageSniffer.Png }, h.Saved);
    }

    [Fact]
    public async Task A_new_logo_url_is_a_new_key_and_downloaded()
    {
        var h = new Harness();
        h.LogoIs(LogoUrl, "image/png", ImageFixtures.Png(256, 256));
        const string Other = "https://cdn.example/other.gif";
        h.LogoIs(Other, "image/gif", ImageFixtures.Gif(64, 64));
        await h.ReconcileAsync();

        h.Station.LogoUrl = Other;
        Assert.True(await h.ReconcileAsync());

        Assert.Equal(new[] { ImageSniffer.Png, ImageSniffer.Gif }, h.Saved);
    }

    [Fact]
    public async Task Another_station_id_is_a_new_key()
    {
        var h = new Harness();
        h.LogoIs(LogoUrl, "image/png", ImageFixtures.Png(256, 256));
        await h.ReconcileAsync();

        h.Station.Id = "77aa";
        Assert.True(await h.ReconcileAsync());

        Assert.Equal(2, h.Requests(LogoUrl));
    }

    [Fact]
    public async Task A_redirect_is_refused_and_the_embedded_logo_stands_in()
    {
        var h = new Harness();
        h.Server.Respond[LogoUrl] = () =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("http://metadata.example/latest/logo.png");
            return redirect;
        };

        Assert.True(await h.ReconcileAsync());

        Assert.Equal(new[] { ImageSniffer.Png }, h.Saved);   // the embedded logo is a PNG
        Assert.DoesNotContain(h.Server.Seen, s => s.Contains("metadata.example", StringComparison.Ordinal));
        Assert.Single(h.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Radio Paradise", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Log.Entries, e => e.Message.Contains("cdn.example", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/png")]
    public async Task Bytes_that_are_not_an_image_count_as_a_failed_logo(string declared)
    {
        var h = new Harness();
        h.LogoIs(LogoUrl, declared, "<html>not a logo</html>"u8.ToArray());

        Assert.True(await h.ReconcileAsync());

        Assert.Equal(new[] { ImageSniffer.Png }, h.Saved);
        Assert.False(await h.ReconcileAsync());
        Assert.Equal(1, h.Requests(LogoUrl));
    }

    [Fact]
    public async Task An_oversized_logo_counts_as_a_failed_logo()
    {
        var h = new Harness();
        h.LogoIs(LogoUrl, "image/png", ImageFixtures.Png((uint)ImageSniffer.MaxImageDimension + 1, 16));

        await h.ReconcileAsync();

        Assert.Equal(new[] { ImageSniffer.Png }, h.Saved);
        Assert.Equal(1, h.Requests(LogoUrl));
    }

    [Fact]
    public async Task A_failed_logo_is_not_asked_for_again_until_the_retry_time_has_passed()
    {
        var h = new Harness();
        h.Server.Respond[LogoUrl] = () => throw new HttpRequestException("timed out");

        Assert.True(await h.ReconcileAsync());   // embedded logo
        h.Clock.Now += StationLogos.FailedLogoRetry - TimeSpan.FromSeconds(1);
        Assert.False(await h.ReconcileAsync());
        Assert.Equal(1, h.Requests(LogoUrl));

        h.LogoIs(LogoUrl, "image/png", ImageFixtures.Png(256, 256));
        h.Clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(await h.ReconcileAsync());

        Assert.Equal(2, h.Requests(LogoUrl));
        Assert.Equal(new[] { ImageSniffer.Png, ImageSniffer.Png }, h.Saved);
        Assert.Single(h.Log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_logo_that_keeps_failing_warns_once_per_streak()
    {
        var h = new Harness();
        await h.ReconcileAsync();   // 404
        h.Clock.Now += StationLogos.FailedLogoRetry;
        await h.ReconcileAsync();   // asked again, fails again: same streak
        Assert.Equal(2, h.Requests(LogoUrl));
        Assert.Single(h.Log.Entries, e => e.Level == LogLevel.Warning);

        h.LogoIs(LogoUrl, "image/png", ImageFixtures.Png(256, 256));
        h.Clock.Now += StationLogos.FailedLogoRetry;
        await h.ReconcileAsync();   // works: the streak ends
        h.Server.Respond.Remove(LogoUrl);
        h.Item.ImageInfos = [];
        await h.ReconcileAsync();   // fails again: a new streak

        Assert.Equal(2, h.Log.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task A_refused_logo_url_warns_with_the_station_and_the_reason_but_not_the_url()
    {
        var h = new Harness();
        h.Station.LogoUrl = "https://user:pw@cdn.example/logo.png";

        await h.ReconcileAsync();
        await h.ReconcileAsync();

        var warning = Assert.Single(h.Log.Entries, e => e.Level == LogLevel.Warning).Message;
        Assert.Contains("Radio Paradise", warning, StringComparison.Ordinal);
        Assert.Contains(StationUrls.RefusedCredentials, warning, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn.example", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_logo_url_that_is_fixed_and_broken_again_warns_again()
    {
        var h = new Harness();
        h.LogoIs(LogoUrl, "image/png", ImageFixtures.Png(256, 256));
        h.Station.LogoUrl = "ftp://cdn.example/logo.png";
        await h.ReconcileAsync();

        h.Station.LogoUrl = LogoUrl;   // fixed: downloaded, the streak ends
        await h.ReconcileAsync();
        h.Station.LogoUrl = "ftp://cdn.example/logo.png";   // the same refused value again
        await h.ReconcileAsync();

        Assert.Equal(2, h.Log.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task A_refused_logo_url_that_is_cleared_and_set_again_warns_again()
    {
        var h = new Harness();
        h.Station.LogoUrl = "ftp://cdn.example/logo.png";
        await h.ReconcileAsync();

        h.Station.LogoUrl = string.Empty;   // no logo: the streak ends
        await h.ReconcileAsync();
        h.Station.LogoUrl = "ftp://cdn.example/logo.png";
        await h.ReconcileAsync();

        Assert.Equal(2, h.Log.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task A_logo_timeout_is_swallowed_and_cached()
    {
        var h = new Harness();
        h.Server.Respond[LogoUrl] = () => throw new TaskCanceledException("the 8 s budget ran out");

        Assert.True(await h.ReconcileAsync());
        Assert.False(await h.ReconcileAsync());

        Assert.Equal(1, h.Requests(LogoUrl));
        Assert.Equal(new[] { ImageSniffer.Png }, h.Saved);
    }

    [Fact]
    public async Task Recording_a_failure_keeps_the_other_stations_active_failures()
    {
        var h = new Harness();
        await h.ReconcileAsync();                     // station c07e fails at 12:00, cached until 12:30
        h.Clock.Now += TimeSpan.FromMinutes(10);
        h.Station.Id = "77aa";
        h.Item.ImageInfos = [];
        await h.ReconcileAsync();                     // station 77aa fails at 12:10; pruning must keep c07e's entry
        h.Clock.Now += TimeSpan.FromMinutes(10);
        h.Station.Id = "c07e";
        h.Item.ImageInfos = [];

        await h.ReconcileAsync();                     // 12:20: c07e is still failing, so no request

        Assert.Equal(2, h.Requests(LogoUrl));
    }

    [Fact]
    public async Task A_save_that_throws_records_nothing_so_the_next_poll_saves_again()
    {
        var h = new Harness();
        h.Station.LogoUrl = string.Empty;
        h.Item.SetImagePath(ImageType.Primary, "http://saved.example/old-cover");   // the item still shows something else
        h.SaveThrows = true;

        await Assert.ThrowsAsync<IOException>(h.ReconcileAsync);
        h.SaveThrows = false;
        Assert.True(await h.ReconcileAsync());

        Assert.Equal(new[] { ImageSniffer.Png, ImageSniffer.Png }, h.Attempts);
    }

    [Fact]
    public async Task A_downloaded_logo_whose_save_failed_is_not_downloaded_again_every_poll()
    {
        var h = new Harness();
        h.LogoIs(LogoUrl, "image/png", ImageFixtures.Png(256, 256));
        h.SaveThrows = true;
        h.Logos.BeginPoll();
        var prepared = (await h.Logos.PrepareAsync(h.Station, false, CancellationToken.None))!;
        await Assert.ThrowsAsync<IOException>(() => h.Logos.ApplyAsync(h.Item, prepared, CancellationToken.None));
        h.Logos.SaveFailed(prepared);
        h.SaveThrows = false;

        await h.ReconcileAsync();
        await h.ReconcileAsync();

        Assert.Equal(1, h.Requests(LogoUrl));
        Assert.Equal(new[] { ImageSniffer.Png }, h.Saved);   // the embedded logo meanwhile
    }

    [Fact]
    public async Task A_failed_save_of_the_embedded_logo_leaves_the_negative_cache_alone()
    {
        // The disk failed while the embedded logo was being saved; once it recovers the real logo is fetched at once.
        var h = new Harness();
        h.LogoIs(LogoUrl, "image/png", ImageFixtures.Png(256, 256));
        h.Logos.SaveFailed(new PreparedLogo(h.Station.Id, StationLogos.KeyOf(h.Station), null));

        Assert.True(await h.ReconcileAsync());

        Assert.Equal(1, h.Requests(LogoUrl));
    }

    [Fact]
    public async Task One_poll_starts_at_most_the_download_budget()
    {
        var h = new Harness();
        var urls = Enumerable.Range(0, StationLogos.MaxDownloadsPerPoll + 1).Select(i => "https://cdn.example/logo" + i + ".png").ToList();
        urls.ForEach(u => h.LogoIs(u, "image/png", ImageFixtures.Png(32, 32)));
        var stations = urls.Select((u, i) => new StationDefinition { Id = "s" + i, Kind = StationKind.Stream, LogoUrl = u }).ToList();

        h.Logos.BeginPoll();
        var first = new List<PreparedLogo?>();
        foreach (var station in stations)
        {
            first.Add(await h.Logos.PrepareAsync(station, false, CancellationToken.None));
        }

        Assert.Equal(StationLogos.MaxDownloadsPerPoll, h.Server.Seen.Count);
        var deferred = Assert.IsType<PreparedLogo>(first[^1]);   // over budget: the embedded logo stands in meanwhile
        Assert.Null(deferred.Image);
        Assert.Null(await h.Logos.PrepareAsync(stations[^1], true, CancellationToken.None));   // an item with an image waits

        // The embedded logo is applied; the next poll still downloads the real one.
        await h.Logos.ApplyAsync(h.Item, deferred, CancellationToken.None);
        h.Logos.BeginPoll();
        Assert.NotNull((await h.Logos.PrepareAsync(stations[^1], true, CancellationToken.None))?.Image);
        Assert.Equal(StationLogos.MaxDownloadsPerPoll + 1, h.Server.Seen.Count);
    }

    [Fact]
    public async Task The_negative_cache_is_keyed_by_station_and_logo_url()
    {
        var h = new Harness();
        await h.ReconcileAsync();   // 404: failed
        const string Other = "https://cdn.example/other.png";
        h.LogoIs(Other, "image/png", ImageFixtures.Png(32, 32));

        h.Station.LogoUrl = Other;
        await h.ReconcileAsync();

        Assert.Equal(1, h.Requests(Other));
        Assert.Equal(new[] { ImageSniffer.Png, ImageSniffer.Png }, h.Saved);
    }

    [Fact]
    public async Task The_negative_cache_is_per_station_even_for_a_shared_logo_url()
    {
        var h = new Harness();
        await h.ReconcileAsync();   // 404 for station c07e
        h.Station.Id = "77aa";      // another station with the same logo URL
        h.Item.ImageInfos = [];

        await h.ReconcileAsync();

        Assert.Equal(2, h.Requests(LogoUrl));
    }

    [Fact]
    public async Task While_the_retry_time_holds_a_lost_image_gets_the_embedded_logo_without_a_request()
    {
        var h = new Harness();
        await h.ReconcileAsync();   // 404: failed, embedded logo saved
        h.Item.ImageInfos = [];

        Assert.True(await h.ReconcileAsync());

        Assert.Equal(1, h.Requests(LogoUrl));
        Assert.Equal(2, h.Saved.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://user:pw@cdn.example/logo.png")]
    [InlineData("ftp://cdn.example/logo.png")]
    [InlineData("https://cdn.example/logo.png#x")]
    public async Task Without_a_usable_logo_url_the_embedded_logo_is_saved_without_a_request(string logoUrl)
    {
        var h = new Harness();
        h.Station.LogoUrl = logoUrl;

        Assert.True(await h.ReconcileAsync());
        Assert.False(await h.ReconcileAsync());

        Assert.Empty(h.Server.Seen);
        Assert.Equal(new[] { ImageSniffer.Png }, h.Saved);
        Assert.DoesNotContain(h.Log.Entries, e => e.Message.Contains("pw@", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_subwave_station_gets_the_embedded_logo_even_with_a_logo_url()
    {
        var h = new Harness();
        h.Station.Kind = StationKind.SubWave;
        h.LogoIs(LogoUrl, "image/png", ImageFixtures.Png(256, 256));

        Assert.True(await h.ReconcileAsync());

        Assert.Empty(h.Server.Seen);
        Assert.Equal(new[] { ImageSniffer.Png }, h.Saved);
    }

    [Fact]
    public async Task Retain_without_the_station_makes_the_next_reconcile_decide_afresh()
    {
        // Card L4: a stream whose id became a SUB/WAVE station had a cover from the cover keeper; when it is a stream again
        // its item must get the logo, not keep the cover. The poller retains only the listed streams.
        var h = new Harness();
        h.Station.Kind = StationKind.SubWave;
        await h.ReconcileAsync();
        h.Logos.Retain(new HashSet<string>());

        Assert.True(await h.ReconcileAsync());
        Assert.Equal(2, h.Saved.Count);
    }

    [Fact]
    public async Task Status_says_what_the_stream_s_item_shows_without_its_url()
    {
        // Card L4, for the configuration page's station table.
        var h = new Harness();
        Assert.Equal(StationLogos.StatusDeferred, h.Logos.Status(h.Station));   // not downloaded yet

        await h.ReconcileAsync();   // 404: the embedded logo stands in and the logo waits FailedLogoRetry
        Assert.Equal(StationLogos.StatusFailed, h.Logos.Status(h.Station));

        h.Clock.Now += StationLogos.FailedLogoRetry;
        h.LogoIs(LogoUrl, "image/png", ImageFixtures.Png(64, 64));
        Assert.Equal(StationLogos.StatusDeferred, h.Logos.Status(h.Station));   // the embedded logo is applied, the own one due

        await h.ReconcileAsync();
        Assert.Equal(StationLogos.StatusOwn, h.Logos.Status(h.Station));

        h.Station.LogoUrl = "https://cdn.example/other.png";   // another key: its own logo is not applied yet
        Assert.Equal(StationLogos.StatusDeferred, h.Logos.Status(h.Station));
        h.Station.LogoUrl = " ";
        Assert.Equal(StationLogos.StatusEmbedded, h.Logos.Status(h.Station));
        h.Station.LogoUrl = "https://user:pw@cdn.example/x.png";
        Assert.Equal(StationLogos.StatusFailed, h.Logos.Status(h.Station));
    }

    [Fact]
    public async Task A_logo_save_is_never_cancelled_midway_and_a_stopped_poll_starts_none()
    {
        // As LiveItemArtwork: Jellyfin 12.3's ImageSaver truncates the file before it observes the token.
        var tokens = new List<CancellationToken>();
        var logos = new StationLogos(new StationClient(null!), (item, _, _, token) =>
        {
            tokens.Add(token);
            return Task.CompletedTask;
        }, NullLogger<StationLogos>.Instance, TimeProvider.System);
        using var poll = new CancellationTokenSource();
        var station = new StationDefinition { Id = "s", Kind = StationKind.Stream, StreamUrl = "https://cdn.example/s.mp3" };

        Assert.True(await logos.ApplyAsync(new Audio(), new PreparedLogo("s", StationLogos.KeyOf(station), null), poll.Token));
        Assert.False(Assert.Single(tokens).CanBeCanceled);

        await poll.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => logos.ApplyAsync(new Audio(), new PreparedLogo("s", StationLogos.KeyOf(station), null), poll.Token));
        Assert.Single(tokens);
    }
}
