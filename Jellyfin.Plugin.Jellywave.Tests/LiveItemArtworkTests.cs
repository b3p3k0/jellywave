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

public class LiveItemArtworkTests
{
    private static readonly Uri Base = new("http://station.example/");

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(Respond(request));
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
            Handler = new StubHandler();
            Artwork = new LiveItemArtwork(new StationClient(new StubFactory(Handler)), SaveAsync, NullLogger<LiveItemArtwork>.Instance);
        }

        public StubHandler Handler { get; }

        public LiveItemArtwork Artwork { get; }

        public Audio Item { get; } = new();

        public List<string> Saved { get; } = [];

        public void StationHasArt(string mime = "image/jpeg")
        {
            Handler.Respond = _ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageFixtures.For(mime)) };
                r.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
                return r;
            };
        }

        public void StationIsDown() => Handler.Respond = _ => throw new HttpRequestException("boom");

        private Task SaveAsync(BaseItem item, Stream stream, string mime, CancellationToken ct)
        {
            Saved.Add(mime);
            item.SetImagePath(ImageType.Primary, "http://saved.example/" + Saved.Count);   // http path: no file system needed
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Fresh_start_with_no_track_saves_the_logo_once()
    {
        var h = new Harness();
        var prepared = await h.Artwork.PrepareAsync("live", Base, null, CancellationToken.None);
        Assert.Equal(LiveItemArtwork.LogoKey, prepared.Key);
        Assert.True(await h.Artwork.ApplyAsync(h.Item, prepared, CancellationToken.None));
        Assert.Equal(["image/png"], h.Saved);
        Assert.Equal(LiveItemArtwork.LogoKey, h.Artwork.Applied("live"));

        prepared = await h.Artwork.PrepareAsync("live", Base, null, CancellationToken.None);
        Assert.False(await h.Artwork.ApplyAsync(h.Item, prepared, CancellationToken.None));
        Assert.Single(h.Saved);
        Assert.Equal(0, h.Handler.Requests);
    }

    [Fact]
    public async Task Cover_is_downloaded_once_per_id_and_saved_with_its_type()
    {
        var h = new Harness();
        h.StationHasArt("image/webp");
        var prepared = await h.Artwork.PrepareAsync("live", Base, "id-1", CancellationToken.None);
        Assert.Equal("id-1", prepared.Key);
        Assert.NotNull(prepared.Image);
        Assert.True(await h.Artwork.ApplyAsync(h.Item, prepared, CancellationToken.None));
        Assert.Equal(["image/webp"], h.Saved);
        Assert.Equal("id-1", h.Artwork.Applied("live"));

        prepared = await h.Artwork.PrepareAsync("live", Base, "id-1", CancellationToken.None);
        Assert.Null(prepared.Image);
        Assert.False(await h.Artwork.ApplyAsync(h.Item, prepared, CancellationToken.None));
        Assert.Equal(1, h.Handler.Requests);
    }

    [Fact]
    public async Task Track_without_art_falls_back_to_the_logo_and_does_not_refetch_while_it_lasts()
    {
        var h = new Harness();
        var prepared = await h.Artwork.PrepareAsync("live", Base, "no-art", CancellationToken.None);
        Assert.Equal(LiveItemArtwork.LogoKey, prepared.Key);
        Assert.True(await h.Artwork.ApplyAsync(h.Item, prepared, CancellationToken.None));
        Assert.Equal(["image/png"], h.Saved);

        // Same track, next poll: the missing cover is remembered, so the station is not asked again and nothing is saved.
        prepared = await h.Artwork.PrepareAsync("live", Base, "no-art", CancellationToken.None);
        Assert.False(await h.Artwork.ApplyAsync(h.Item, prepared, CancellationToken.None));
        Assert.Single(h.Saved);
        Assert.Equal(1, h.Handler.Requests);
    }

    [Fact]
    public async Task Lost_image_is_restored_on_the_next_poll()
    {
        var h = new Harness();
        h.StationHasArt();
        await h.Artwork.ApplyAsync(h.Item, await h.Artwork.PrepareAsync("live", Base, "id-1", CancellationToken.None), CancellationToken.None);

        // Jellyfin's refresh re-saved the item without our image.
        h.Item.ImageInfos = [];
        var prepared = await h.Artwork.PrepareAsync("live", Base, "id-1", CancellationToken.None);
        Assert.False(await h.Artwork.ApplyAsync(h.Item, prepared, CancellationToken.None));
        Assert.Null(h.Artwork.Applied("live"));

        prepared = await h.Artwork.PrepareAsync("live", Base, "id-1", CancellationToken.None);
        Assert.True(await h.Artwork.ApplyAsync(h.Item, prepared, CancellationToken.None));
        Assert.Equal(2, h.Saved.Count);
    }

    [Fact]
    public async Task Station_failure_propagates_and_leaves_applied_untouched()
    {
        var h = new Harness();
        h.StationHasArt();
        await h.Artwork.ApplyAsync(h.Item, await h.Artwork.PrepareAsync("live", Base, "id-1", CancellationToken.None), CancellationToken.None);
        h.StationIsDown();
        await Assert.ThrowsAsync<HttpRequestException>(() => h.Artwork.PrepareAsync("live", Base, "id-2", CancellationToken.None));
        Assert.Equal("id-1", h.Artwork.Applied("live"));
    }

    [Fact]
    public async Task No_base_url_means_logo_without_a_request()
    {
        var h = new Harness();
        var prepared = await h.Artwork.PrepareAsync("live", null, "id-1", CancellationToken.None);
        Assert.Equal(LiveItemArtwork.LogoKey, prepared.Key);
        Assert.Equal(0, h.Handler.Requests);
    }

    [Fact]
    public async Task A_save_is_never_cancelled_midway_and_a_stopped_poll_starts_none()
    {
        // Jellyfin 12.3's ImageSaver opens the file with FileMode.Create before it observes the token: a token passed in
        // could truncate the image. A poll stopped before the save starts saves nothing.
        var tokens = new List<CancellationToken>();
        var artwork = new LiveItemArtwork(new StationClient(null!), (item, _, _, token) =>
        {
            tokens.Add(token);
            item.SetImagePath(ImageType.Primary, "http://saved.example/1");
            return Task.CompletedTask;
        }, NullLogger<LiveItemArtwork>.Instance);
        using var poll = new CancellationTokenSource();

        Assert.True(await artwork.ApplyAsync(new Audio(), LiveItemArtwork.Logo("live"), poll.Token));
        Assert.False(Assert.Single(tokens).CanBeCanceled);

        await poll.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => artwork.ApplyAsync(new Audio(), LiveItemArtwork.Logo("other"), poll.Token));
        Assert.Single(tokens);
    }
}
