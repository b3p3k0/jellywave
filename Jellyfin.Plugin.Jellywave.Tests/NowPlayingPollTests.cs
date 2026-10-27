using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
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

        public List<string> Seen { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            var path = request.RequestUri.AbsolutePath;
            Seen.Add(host + path);
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
            Poller = new NowPlayingService(
                client,
                State,
                new LiveItemArtwork(client, SaveImageAsync, NullLogger<LiveItemArtwork>.Instance),
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

        public Task PollAsync() => Poller.PollOnceAsync(CancellationToken.None);

        private static Task SaveImageAsync(BaseItem item, Stream stream, string mime, CancellationToken cancellationToken)
        {
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
}
