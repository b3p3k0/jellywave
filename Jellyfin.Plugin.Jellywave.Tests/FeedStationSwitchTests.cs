using System;
using System.Collections.Generic;
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
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 17 review (D2 on the routes): after the station URL changes, the Feed and Schedule routes answer as if nothing
/// has been heard until the poller has fetched the configured station: no track, DJ, history, song id, cover tag or
/// takeover from the old station. The station name and the channel and item ids still answer.
/// </summary>
public class FeedStationSwitchTests
{
    private const string StationA = "http://station-a.example";
    private const string StationB = "http://station-b.example";

    /// <summary>An <see cref="IImageProcessor"/> whose only answer is a fixed cache tag.</summary>
    public class TagProcessor : DispatchProxy
    {
        public const string Tag = "tag-1";

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == nameof(IImageProcessor.GetImageCacheTag) ? Tag : throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class StubStation : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var day = "[" + string.Join(',', Enumerable.Repeat("\"s1\"", 24)) + "]";
            var grid = string.Join(',', Enumerable.Range(0, 7).Select(d => "\"" + d + "\":" + day));
            var json = "{\"timezone\":\"UTC\",\"shows\":[{\"id\":\"s1\",\"name\":\"Grid show\"}],\"schedule\":{" + grid + "}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
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
        public Harness(bool heardStationA = true)
        {
            Library.ListedItems.Add(LiveItem);
            LiveItem.SetImagePath(ImageType.Primary, "http://saved.example/1");
            if (heardStationA)
            {
                var origin = StationUrls.Origin(new Uri(StationA + "/"));
                State.Update(
                    origin,
                    new NowPlayingFeed
                    {
                        NowPlaying = new TrackInfo { Artist = "Artist A", Title = "Song A", SubsonicId = "cov1" },
                        Dj = new DjInfo { Name = "DJ A" },
                        Context = new ContextInfo { ActiveShow = new ContextShowInfo { Id = "takeover", Name = "Old station show" } },
                        StreamOnline = true
                    },
                    DateTime.UtcNow);
                State.UpdateStationState(origin, new StationState { History = new List<QueueTrack> { new() { Artist = "Old", Title = "From A" } } });
            }
        }

        public FakeLibrary Library { get; } = FakeLibrary.Create();

        public NowPlayingState State { get; } = new();

        public Audio LiveItem { get; } = new() { Id = Guid.NewGuid() };

        public Channel Channel { get; } = new() { Id = Guid.NewGuid(), Name = "SUB/WAVE" };

        public PluginConfiguration Config { get; } = new() { StationName = "SUB/WAVE", StationBaseUrl = StationA };

        public JellywaveWebController Controller()
        {
            var http = new DefaultHttpContext();
            http.Items[StationAccess.ChannelItem] = Channel;
            var client = new StationClient(new StubFactory(new StubStation()));
            return new JellywaveWebController(
                State,
                new LiveItemFinder(Library.Manager, () => [], NullLogger<LiveItemFinder>.Instance),
                DispatchProxy.Create<IImageProcessor, TagProcessor>(),
                client,
                new ScheduleCache(client),
                new RequestReceipts(),
                NullLogger<JellywaveWebController>.Instance,
                () => Config)
            {
                ControllerContext = new ControllerContext { HttpContext = http }
            };
        }
    }

    private static void AssertNothingHeard(FeedResponse feed, Harness h)
    {
        Assert.Null(feed.Title);
        Assert.Null(feed.Artist);
        Assert.Null(feed.Dj);
        Assert.Null(feed.SongId);
        Assert.Null(feed.TrackLine);
        Assert.Null(feed.FetchedUtc);
        Assert.Empty(feed.Recent);
        Assert.Null(feed.ImageTag);
        Assert.Equal(JellywaveChannel.DefaultOverview, feed.Description);
        Assert.Equal("SUB/WAVE", feed.StationName);
        Assert.Equal(h.Channel.Id, feed.ChannelId);
        Assert.Equal(h.LiveItem.Id, feed.ItemId);
    }

    [Fact]
    public void Control_the_configured_station_s_data_is_served()
    {
        var h = new Harness();

        var feed = h.Controller().Feed().Value!;

        Assert.Equal(("Song A", "DJ A", "cov1", TagProcessor.Tag), (feed.Title, feed.Dj, feed.SongId, feed.ImageTag));
        Assert.Equal(new[] { "Old – From A" }, feed.Recent);
        Assert.NotNull(feed.FetchedUtc);
    }

    [Fact]
    public void After_a_station_url_change_the_feed_answers_as_if_nothing_was_heard()
    {
        var h = new Harness();
        h.Config.StationBaseUrl = StationB;

        AssertNothingHeard(h.Controller().Feed().Value!, h);
    }

    [Fact]
    public void With_the_station_url_cleared_the_feed_answers_as_if_nothing_was_heard()
    {
        var h = new Harness();
        h.Config.StationBaseUrl = string.Empty;

        AssertNothingHeard(h.Controller().Feed().Value!, h);
    }

    [Fact]
    public void With_nothing_heard_the_item_s_own_image_still_has_its_tag()
    {
        // Polling off or the station unreachable: the state is empty and the item shows the logo the poller saved.
        var h = new Harness(heardStationA: false);

        var feed = h.Controller().Feed().Value!;

        Assert.Null(feed.Title);
        Assert.Equal(TagProcessor.Tag, feed.ImageTag);
    }

    [Fact]
    public async Task After_a_station_url_change_the_schedule_does_not_name_the_old_station_s_takeover()
    {
        var h = new Harness();
        var before = (await h.Controller().Schedule(CancellationToken.None)).Value!;
        Assert.Equal("Old station show", before.Takeover);

        h.Config.StationBaseUrl = StationB;
        var after = (await h.Controller().Schedule(CancellationToken.None)).Value!;

        Assert.True(after.Available);
        Assert.Null(after.Takeover);
        Assert.Contains(after.Today, b => b.Now);
    }
}
