using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 17 (found with D6): a null entry in a station list (history, upcoming, lyric lines) is skipped instead of making
/// the route answer 500.
/// </summary>
public class StationListNullEntryTests
{
    private const string Origin = "http://station.example/";

    private sealed class StubStation : HttpMessageHandler
    {
        public string Json { get; set; } = "{}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json, Encoding.UTF8, "application/json") });
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    [Fact]
    public void Null_history_and_upcoming_entries_are_skipped()
    {
        var state = new NowPlayingState();
        state.Update(Origin, new NowPlayingFeed { NowPlaying = new TrackInfo { Artist = "A", Title = "B" }, StreamOnline = true }, DateTime.UtcNow);
        state.UpdateStationState(Origin, new StationState
        {
            History = new List<QueueTrack> { null!, new() { Artist = "Old", Title = "Song", RequestedBy = "Sam" } },
            Upcoming = new List<QueueTrack> { null! }
        });
        var http = new DefaultHttpContext();
        http.Items[StationAccess.ChannelItem] = new Channel { Id = Guid.NewGuid(), Name = "SUB/WAVE" };
        var library = FakeLibrary.Create();
        var controller = new JellywaveWebController(
            state,
            new LiveItemFinder(library.Manager, () => [], NullLogger<LiveItemFinder>.Instance),
            null!,
            new StationClient(null!),
            new ScheduleCache(new StationClient(null!)),
            new RequestReceipts(),
            NullLogger<JellywaveWebController>.Instance,
            () => new PluginConfiguration { StationBaseUrl = "http://station.example" })
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

        var feed = controller.Feed().Value!;

        Assert.Equal(new[] { "Old – Song · requested by Sam" }, feed.Recent);
        Assert.Equal(string.Empty, feed.UpNext);
    }

    [Fact]
    public async Task Null_lyric_lines_are_skipped()
    {
        var client = new StationClient(new StubFactory(new StubStation { Json = """{"songId":"s1","synced":false,"lines":[null,{"text":"La la"}]}""" }));
        var controller = new JellywaveWebController(
            new NowPlayingState(),
            new LiveItemFinder(null!, () => [], NullLogger<LiveItemFinder>.Instance),
            null!,
            client,
            new ScheduleCache(client),
            new RequestReceipts(),
            NullLogger<JellywaveWebController>.Instance,
            () => new PluginConfiguration { StationBaseUrl = "http://station.example" });

        var lyrics = (await controller.Lyrics("s1", CancellationToken.None)).Value!;

        Assert.Equal("ok", lyrics.Status);
        Assert.Equal(new[] { "La la" }, lyrics.Lines);
    }
}
