using System;
using System.Collections.Generic;
using System.IO;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 17 through the real poll (<see cref="NowPlayingService.PollOnceAsync"/>): what the station page and the live
/// item get after a station switch (D2) and after an empty feed (D5). Each station is a host on one stub handler.
/// </summary>
public class NowPlayingPollTests
{
    private const string Station = "SUB/WAVE";
    private const string HostA = "station-a.example";
    private const string HostB = "station-b.example";

    /// <summary>Answers the feed, the state and the covers per host; a host's entry can be replaced or made to fail.</summary>
    private sealed class Stations : HttpMessageHandler
    {
        public Dictionary<string, string> NowPlaying { get; } = new()
        {
            [HostA] = """{"nowPlaying":{"artist":"Artist A","title":"Song A","subsonic_id":"cov1"},"streamOnline":true}""",
            [HostB] = """{"nowPlaying":{"artist":"Artist B","title":"Song B","subsonic_id":"cov1"},"streamOnline":true}"""
        };

        public Dictionary<string, string> State { get; } = new()
        {
            [HostA] = """{"history":[{"artist":"Old","title":"From A"}],"upcoming":[{"artist":"Next","title":"From A"}]}"""
        };

        public HashSet<string> Art { get; } = new();

        public HashSet<string> Broken { get; } = new();

        public List<string> Seen { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            var path = request.RequestUri.AbsolutePath;
            Seen.Add(host + path);
            if (Broken.Contains(host + path))
            {
                throw new HttpRequestException("timed out");
            }

            if (Art.Contains(host + path))
            {
                var image = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageFixtures.For("image/jpeg")) };
                image.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                return Task.FromResult(image);
            }
            var body = path switch
            {
                "/api/now-playing" => NowPlaying.GetValueOrDefault(host),
                "/api/state" => State.GetValueOrDefault(host),
                _ => null   // covers: the station answers 502 when it has no art
            };
            if (body is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
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
            var channel = new Channel { Id = Library.IdFor("Channel " + Station, typeof(Channel)), Name = Station };
            Library.Items[channel.Id] = channel;
            Library.ListedItems.Add(LiveItem);
            var client = new StationClient(new StubFactory(Stations));
            var finder = new LiveItemFinder(Library.Manager, () => new List<IChannel>(), NullLogger<LiveItemFinder>.Instance);
            Artwork = new LiveItemArtwork(client, SaveImageAsync, NullLogger<LiveItemArtwork>.Instance);
            Poller = new NowPlayingService(
                client,
                State,
                Artwork,
                finder,
                Health,
                Library.Manager,
                null!,
                NullLogger<NowPlayingService>.Instance,
                () => Config);
        }

        public FakeLibrary Library { get; } = FakeLibrary.Create();

        public Stations Stations { get; } = new();

        public NowPlayingState State { get; } = new();

        public PollerHealth Health { get; } = new();

        public Audio LiveItem { get; } = new();

        public PluginConfiguration Config { get; } = new() { StationName = Station, StationBaseUrl = "http://" + HostA };

        public NowPlayingService Poller { get; }

        public LiveItemArtwork Artwork { get; }

        public List<string> ImagesSaved { get; } = new();

        public Task PollAsync() => Poller.PollOnceAsync(CancellationToken.None);

        private Task SaveImageAsync(BaseItem item, Stream stream, string mime, CancellationToken cancellationToken)
        {
            ImagesSaved.Add(mime);
            item.SetImagePath(ImageType.Primary, "http://saved.example/" + Guid.NewGuid().ToString("N"));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Control_the_first_station_s_history_reaches_the_state()
    {
        var server = new Server();
        await server.PollAsync();

        Assert.Equal("From A", server.State.StationState()?.History?[0].Title);
        Assert.StartsWith("Now playing: Artist A – Song A", server.LiveItem.Overview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_a_station_switch_a_failed_state_fetch_does_not_keep_the_old_history()
    {
        var server = new Server();
        await server.PollAsync();
        server.Config.StationBaseUrl = "http://" + HostB;   // station B has no /api/state answer: 502

        await server.PollAsync();

        Assert.Contains(HostB + "/api/state", server.Stations.Seen);
        Assert.Equal("Song B", server.State.Feed()?.NowPlaying?.Title);
        Assert.Null(server.State.StationState());
    }

    [Fact]
    public async Task On_the_same_station_a_failed_state_fetch_keeps_the_last_history()
    {
        var server = new Server();
        await server.PollAsync();
        server.Stations.State.Remove(HostA);

        await server.PollAsync();

        Assert.Equal("From A", server.State.StationState()?.History?[0].Title);
    }

    [Fact]
    public async Task A_track_without_art_costs_one_cover_request_across_polls()
    {
        var server = new Server();
        for (var poll = 0; poll < 5; poll++)
        {
            await server.PollAsync();
        }

        Assert.Single(server.Stations.Seen, r => r == HostA + "/api/cover/cov1");
        Assert.Equal(new[] { "image/png" }, server.ImagesSaved);
    }

    [Fact]
    public async Task After_a_station_switch_the_same_cover_id_is_fetched_from_the_new_station()
    {
        var server = new Server();
        server.Stations.Art.Add(HostA + "/api/cover/cov1");
        server.Stations.Art.Add(HostB + "/api/cover/cov1");
        await server.PollAsync();
        server.Config.StationBaseUrl = "http://" + HostB;

        await server.PollAsync();

        Assert.Contains(HostB + "/api/cover/cov1", server.Stations.Seen);
        Assert.Equal(new[] { "image/jpeg", "image/jpeg" }, server.ImagesSaved);
    }

    [Fact]
    public async Task A_null_feed_records_a_failure_clears_the_state_and_falls_the_item_back()
    {
        var server = new Server();
        server.Stations.Art.Add(HostA + "/api/cover/cov1");
        await server.PollAsync();
        Assert.NotNull(server.State.StationState());
        Assert.Equal(true, server.Health.Snapshot().Reachable);

        server.Stations.NowPlaying[HostA] = "null";
        await server.PollAsync();

        var health = server.Health.Snapshot();
        Assert.Equal(false, health.Reachable);
        Assert.Equal(NowPlayingService.EmptyFeedMessage, health.LastError);
        Assert.NotNull(health.LastErrorUtc);
        Assert.Null(server.State.Feed());
        Assert.Null(server.State.StationState());
        Assert.Equal(JellywaveChannel.DefaultOverview, server.LiveItem.Overview);
        Assert.Equal(new[] { "image/jpeg", "image/png" }, server.ImagesSaved);
    }

    [Fact]
    public async Task After_a_null_feed_the_next_real_feed_is_a_success_again()
    {
        var server = new Server();
        server.Stations.NowPlaying[HostA] = "null";
        await server.PollAsync();
        server.Stations.NowPlaying[HostA] = """{"nowPlaying":{"artist":"Artist A","title":"Back"},"streamOnline":true}""";

        await server.PollAsync();

        Assert.Equal(true, server.Health.Snapshot().Reachable);
        Assert.Equal("Back", server.State.Feed()?.NowPlaying?.Title);
    }

    [Fact]
    public async Task After_a_station_switch_a_failing_cover_fetch_shows_the_logo_not_the_old_cover()
    {
        var server = new Server();
        server.Stations.Art.Add(HostA + "/api/cover/cov1");
        await server.PollAsync();
        server.Config.StationBaseUrl = "http://" + HostB;
        server.Stations.Broken.Add(HostB + "/api/cover/cov1");

        await server.PollAsync();

        Assert.StartsWith("Now playing: Artist B – Song B", server.LiveItem.Overview, StringComparison.Ordinal);
        Assert.Equal(new[] { "image/jpeg", "image/png" }, server.ImagesSaved);
        Assert.Equal(LiveItemArtwork.LogoKey, server.Artwork.Applied);
    }

    [Fact]
    public async Task On_the_same_station_a_failing_cover_fetch_keeps_the_current_cover()
    {
        var server = new Server();
        server.Stations.Art.Add(HostA + "/api/cover/cov1");
        await server.PollAsync();
        server.Stations.NowPlaying[HostA] = """{"nowPlaying":{"artist":"Artist A","title":"Next","subsonic_id":"cov2"},"streamOnline":true}""";
        server.Stations.Broken.Add(HostA + "/api/cover/cov2");

        await server.PollAsync();

        Assert.Equal(new[] { "image/jpeg" }, server.ImagesSaved);
        Assert.Equal("cov1", server.Artwork.Applied);
    }
}
