using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 17: the live item's applied cover is keyed by station origin and cover id (D2), and a cover the station has no
/// art for is remembered for <see cref="LiveItemArtwork.MissingCoverRetry"/> instead of being asked for every poll (D4).
/// The stub handler counts the station requests; the clock is manual.
/// </summary>
public class LiveItemArtworkOriginTests
{
    private static readonly Uri StationA = new("http://station-a.example/");
    private static readonly Uri StationB = new("http://station-b.example/");

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Serves art for the (host, id) pairs in <see cref="Art"/> and answers 502 (no art) for anything else.</summary>
    private sealed class Covers : HttpMessageHandler
    {
        public HashSet<string> Art { get; } = new();

        public List<string> Seen { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.Host + request.RequestUri.AbsolutePath;
            Seen.Add(key);
            if (!Art.Contains(key))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
            }

            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageFixtures.For("image/jpeg")) };
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(r);
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
            Artwork = new LiveItemArtwork(new StationClient(new StubFactory(Station)), SaveAsync, NullLogger<LiveItemArtwork>.Instance, Clock);
        }

        public Covers Station { get; } = new();

        public ManualClock Clock { get; } = new();

        public LiveItemArtwork Artwork { get; }

        public Audio Item { get; } = new();

        public List<string> Saved { get; } = new();

        /// <summary>One poll's worth: prepare, then apply. Returns the prepared artwork.</summary>
        public async Task<PreparedArtwork> PollAsync(Uri baseUrl, string coverId)
        {
            var prepared = await Artwork.PrepareAsync(baseUrl, coverId, CancellationToken.None);
            await Artwork.ApplyAsync(Item, prepared, CancellationToken.None);
            return prepared;
        }

        private Task SaveAsync(BaseItem item, Stream stream, string mime, CancellationToken ct)
        {
            Saved.Add(mime);
            item.SetImagePath(ImageType.Primary, "http://saved.example/" + Saved.Count);   // http path: no file system needed
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_missing_cover_is_not_asked_for_again_while_the_negative_cache_holds()
    {
        var h = new Harness();
        Assert.Equal(LiveItemArtwork.LogoKey, (await h.PollAsync(StationA, "no-art")).Key);

        for (var poll = 0; poll < 10; poll++)
        {
            h.Clock.Now += TimeSpan.FromSeconds(10);
            Assert.Equal(LiveItemArtwork.LogoKey, (await h.PollAsync(StationA, "no-art")).Key);
        }

        Assert.Single(h.Station.Seen);
        Assert.Equal(["image/png"], h.Saved);
    }

    [Fact]
    public async Task After_the_retry_time_the_station_is_asked_again_and_new_art_shows()
    {
        var h = new Harness();
        await h.PollAsync(StationA, "id-1");
        h.Station.Art.Add("station-a.example/api/cover/id-1");   // the station gets art for the track later

        h.Clock.Now += LiveItemArtwork.MissingCoverRetry - TimeSpan.FromSeconds(1);
        Assert.Equal(LiveItemArtwork.LogoKey, (await h.PollAsync(StationA, "id-1")).Key);
        Assert.Single(h.Station.Seen);

        h.Clock.Now += TimeSpan.FromSeconds(1);
        var prepared = await h.PollAsync(StationA, "id-1");
        Assert.Equal("id-1", prepared.Key);
        Assert.NotNull(prepared.Image);
        Assert.Equal(2, h.Station.Seen.Count);
        Assert.Equal("id-1", h.Artwork.Applied);
    }

    [Fact]
    public async Task A_new_cover_id_is_asked_for_at_once()
    {
        var h = new Harness();
        await h.PollAsync(StationA, "no-art");
        await h.PollAsync(StationA, "other");

        Assert.Equal(new[] { "station-a.example/api/cover/no-art", "station-a.example/api/cover/other" }, h.Station.Seen);
    }

    [Fact]
    public async Task The_same_missing_id_on_another_station_is_asked_for_at_once()
    {
        var h = new Harness();
        await h.PollAsync(StationA, "id-1");
        h.Station.Art.Add("station-b.example/api/cover/id-1");

        var prepared = await h.PollAsync(StationB, "id-1");

        Assert.Equal(new[] { "station-a.example/api/cover/id-1", "station-b.example/api/cover/id-1" }, h.Station.Seen);
        Assert.NotNull(prepared.Image);
    }

    [Fact]
    public async Task The_applied_key_includes_the_origin_so_another_stations_same_id_is_fetched_and_saved()
    {
        var h = new Harness();
        h.Station.Art.Add("station-a.example/api/cover/id-1");
        h.Station.Art.Add("station-b.example/api/cover/id-1");
        await h.PollAsync(StationA, "id-1");
        Assert.Equal(("id-1", StationUrls.Origin(StationA)), (h.Artwork.Applied, h.Artwork.AppliedOrigin));

        var prepared = await h.PollAsync(StationB, "id-1");

        Assert.NotNull(prepared.Image);
        Assert.Equal(2, h.Saved.Count);
        Assert.Equal(("id-1", StationUrls.Origin(StationB)), (h.Artwork.Applied, h.Artwork.AppliedOrigin));
    }

    [Fact]
    public async Task The_logo_has_no_origin()
    {
        var h = new Harness();
        await h.PollAsync(StationA, "no-art");

        Assert.Equal(LiveItemArtwork.LogoKey, h.Artwork.Applied);
        Assert.Null(h.Artwork.AppliedOrigin);
    }
}
