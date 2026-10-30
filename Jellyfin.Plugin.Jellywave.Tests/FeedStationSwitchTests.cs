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
        public int Requests { get; private set; }

        public List<string> Hosts { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Hosts.Add(request.RequestUri!.Host);
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
                var origin = StationKey.Of("live", new Uri(StationA + "/"));
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

        public LiveItemArtwork Artwork { get; } = new(new StationClient(null!), (item, _, _, _) =>
        {
            item.SetImagePath(ImageType.Primary, "http://saved.example/" + Guid.NewGuid().ToString("N"));
            return Task.CompletedTask;
        }, NullLogger<LiveItemArtwork>.Instance);

        public Audio LiveItem { get; } = new() { Id = Guid.NewGuid(), ExternalId = "live" };

        public Channel Channel { get; } = new() { Id = Guid.NewGuid(), Name = "SUB/WAVE" };

        public PluginConfiguration Config { get; } = new() { LibraryName = "SUB/WAVE", Stations = { new StationDefinition { Id = "live", BaseUrl = StationA } } };

        public StubStation Http { get; } = new();

        public JellywaveWebController Controller()
        {
            var client = new StationClient(new StubFactory(Http));
            return WebRoute.Controller(
                () => Config,
                State,
                Artwork,
                new LiveItemFinder(Library.Manager, () => [], NullLogger<LiveItemFinder>.Instance),
                DispatchProxy.Create<IImageProcessor, TagProcessor>(),
                client,
                channel: Channel);
        }

        public FeedResponse Feed(string? stationId = null)
            => WebRoute.Value<FeedResponse>(Run(nameof(JellywaveWebController.Feed), stationId).Result);

        public async Task<ScheduleResponse> ScheduleAsync(string? stationId = null)
            => WebRoute.Value<ScheduleResponse>(await Run(nameof(JellywaveWebController.Schedule), stationId));

        public async Task<LikeOutcome> LikeStatusAsync(string? stationId = null)
            => WebRoute.Value<LikeOutcome>(await Run(nameof(JellywaveWebController.LikeStatus), stationId));

        public Task<IActionResult> Run(string action, string? stationId, params (string Name, object? Value)[] more)
            => WebRoute.RunAsync(Controller(), action, stationId is null ? more : [.. more, ("stationId", stationId)]);
    }

    private static void AssertNothingHeard(FeedResponse feed, Harness h, bool listed = true)
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
        Assert.Equal(listed ? h.LiveItem.Id : null, feed.ItemId);
    }

    [Fact]
    public void Control_the_configured_station_s_data_is_served()
    {
        var h = new Harness();

        var feed = h.Feed();

        Assert.Equal(("Song A", "DJ A", "cov1", TagProcessor.Tag), (feed.Title, feed.Dj, feed.SongId, feed.ImageTag));
        Assert.Equal(new[] { "Old – From A" }, feed.Recent);
        Assert.NotNull(feed.FetchedUtc);
    }

    [Fact]
    public void After_a_station_url_change_the_feed_answers_as_if_nothing_was_heard()
    {
        var h = new Harness();
        h.Config.Stations[0].BaseUrl = StationB;

        AssertNothingHeard(h.Feed(), h);
    }

    [Fact]
    public async Task With_the_station_url_cleared_the_feed_is_404()
    {
        var h = new Harness();
        h.Config.Stations[0].BaseUrl = string.Empty;

        // Card L2: a station without a URL is not listed, so it is no primary station. Card L5: a route whose station does
        // not resolve answers 404 (the page drops its header) instead of an empty feed, by id or without one.
        Assert.Equal(StatusCodes.Status404NotFound, WebRoute.StatusOf(await h.Run(nameof(JellywaveWebController.Feed), null)));
        Assert.Equal(StatusCodes.Status404NotFound, WebRoute.StatusOf(await h.Run(nameof(JellywaveWebController.Feed), "live")));
    }

    [Fact]
    public void With_nothing_heard_the_item_s_own_image_still_has_its_tag()
    {
        // Polling off or the station unreachable: the state is empty and the item shows the logo the poller saved.
        var h = new Harness(heardStationA: false);

        var feed = h.Feed();

        Assert.Null(feed.Title);
        Assert.Equal(TagProcessor.Tag, feed.ImageTag);
    }

    [Fact]
    public async Task After_a_station_url_change_the_schedule_does_not_name_the_old_station_s_takeover()
    {
        var h = new Harness();
        var before = await h.ScheduleAsync();
        Assert.Equal("Old station show", before.Takeover);

        h.Config.Stations[0].BaseUrl = StationB;
        var after = await h.ScheduleAsync();

        Assert.True(after.Available);
        Assert.Null(after.Takeover);
        Assert.Contains(after.Today, b => b.Now);
    }

    [Fact]
    public async Task A_refused_primary_serves_nothing_from_its_station()
    {
        // RA S-b: a stream ahead of it holds the id "live", so the SUB/WAVE station is refused as a duplicate and is no
        // primary station. The routes fail closed without asking the station.
        var h = new Harness();
        h.Config.Stations.Insert(0, new StationDefinition { Id = "live", Name = "Stream", Kind = StationKind.Stream, StreamUrl = "https://cdn.example/a.mp3", SortOrder = -1 });
        // Card L5: a request without a station id serves the primary station; with none listed, every route answers 404.
        foreach (var action in new[] { "Feed", "Schedule", "LikeStatus", "Lyrics", "Avatar" })
        {
            var result = await h.Run(action, null, ("songId", "s1"), ("personaId", "dj_nova"));
            Assert.Equal(StatusCodes.Status404NotFound, WebRoute.StatusOf(result));
        }

        Assert.Equal(0, h.Http.Requests);
    }

    [Fact]
    public async Task With_two_sub_wave_stations_a_route_without_a_station_id_serves_the_primary_one()
    {
        // Card L4: both stations are polled and keep their own state. Card L5: a route called without a station id (an
        // older cached page) reads the primary station's (the first enabled SUB/WAVE station by SortOrder) through its key,
        // and follows a reorder at once.
        var h = new Harness();
        h.Config.Stations.Add(new StationDefinition { Id = "two", Name = "Two", BaseUrl = StationB, SortOrder = 1 });
        var two = new Audio { Id = Guid.NewGuid(), ExternalId = "two" };
        h.Library.ListedItems.Add(two);
        var keyTwo = StationKey.Of("two", new Uri(StationB + "/"));
        h.State.Update(
            keyTwo,
            new NowPlayingFeed
            {
                NowPlaying = new TrackInfo { Artist = "Artist B", Title = "Song B", SubsonicId = "cov2" },
                Context = new ContextInfo { ActiveShow = new ContextShowInfo { Id = "other", Name = "Station two show" } },
                StreamOnline = true
            },
            DateTime.UtcNow);
        h.State.UpdateStationState(keyTwo, new StationState { History = new List<QueueTrack> { new() { Artist = "Old", Title = "From B" } } });

        var feed = h.Feed();
        Assert.Equal(("Song A", h.LiveItem.Id, "Old – From A"), (feed.Title, feed.ItemId, feed.Recent.Single()));
        Assert.Equal("Old station show", (await h.ScheduleAsync()).Takeover);
        await h.LikeStatusAsync();
        Assert.All(h.Http.Hosts, host => Assert.Equal("station-a.example", host));

        h.Config.Stations[1].SortOrder = -1;
        h.Http.Hosts.Clear();

        feed = h.Feed();
        Assert.Equal(("Song B", two.Id, "Old – From B", "Two"), (feed.Title, feed.ItemId, feed.Recent.Single(), feed.StationName));
        Assert.Equal("Station two show", (await h.ScheduleAsync()).Takeover);
        await h.LikeStatusAsync();
        Assert.Contains("station-b.example", h.Http.Hosts);
        Assert.DoesNotContain("station-a.example", h.Http.Hosts);
    }

    [Fact]
    public async Task After_a_url_change_the_old_origin_s_cover_has_no_tag_until_the_new_image_is_saved()
    {
        // RA S2: the poll after a URL change stores the new origin's feed in its fetch phase (up to ceil(N / 4) × 32 s)
        // before its write phase replaces the item's cover. Meanwhile the item still shows the old origin's cover.
        var h = new Harness();
        var originA = StationUrls.Origin(new Uri(StationA + "/"));
        await h.Artwork.ApplyAsync(h.LiveItem, new PreparedArtwork("live", originA, "cov1", new StationImage("image/jpeg", ImageFixtures.For("image/jpeg"))), CancellationToken.None);
        Assert.Equal(TagProcessor.Tag, h.Feed().ImageTag);   // control: the same origin's cover

        h.Config.Stations[0].BaseUrl = StationB;
        var keyB = StationKey.Of("live", new Uri(StationB + "/"));
        h.State.Update(keyB, new NowPlayingFeed { NowPlaying = new TrackInfo { Artist = "Artist B", Title = "Song B", SubsonicId = "cov9" }, StreamOnline = true }, DateTime.UtcNow);

        var during = h.Feed();
        Assert.Equal("Song B", during.Title);
        Assert.Null(during.ImageTag);

        // The write phase saves the new origin's image (here the logo, which has no origin): the tag is back.
        await h.Artwork.ApplyAsync(h.LiveItem, LiveItemArtwork.Logo("live"), CancellationToken.None);
        Assert.Equal(TagProcessor.Tag, h.Feed().ImageTag);
    }
}
