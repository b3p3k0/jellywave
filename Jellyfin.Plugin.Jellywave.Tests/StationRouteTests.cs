using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L5: every station route serves the listed SUB/WAVE station its <c>stationId</c> names (the primary one when the
/// caller sends none), answers 404 for any other id without asking a station, and the request status route follows the
/// receipt's station, never the caller's.
/// </summary>
public class StationRouteTests
{
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly string[] Routes =
    [
        nameof(JellywaveWebController.Feed),
        nameof(JellywaveWebController.Avatar),
        nameof(JellywaveWebController.LikeStatus),
        nameof(JellywaveWebController.Like),
        nameof(JellywaveWebController.Schedule),
        nameof(JellywaveWebController.Lyrics),
        nameof(JellywaveWebController.SubmitRequest)
    ];

    public static IEnumerable<object[]> RouteNames() => Routes.Select(r => new object[] { r });

    public static IEnumerable<object[]> RoutesWithUnlistedIds()
        => Routes.SelectMany(r => MultiStationHarness.UnlistedIds().Select(id => new object[] { r, id[0] }));

    [Theory]
    [MemberData(nameof(RoutesWithUnlistedIds))]
    public async Task An_unlisted_station_id_is_404_and_no_station_is_asked(string action, string stationId)
    {
        var h = new MultiStationHarness();

        var result = await h.RunAsync(action, stationId);

        Assert.Equal(StatusCodes.Status404NotFound, WebRoute.StatusOf(result));
        Assert.Empty(h.Seen);
    }

    [Theory]
    [MemberData(nameof(RouteNames))]
    public async Task A_listed_station_id_reaches_that_station_only(string action)
    {
        var h = new MultiStationHarness();

        var result = await h.RunAsync(action, "two");

        Assert.Equal(StatusCodes.Status200OK, WebRoute.StatusOf(result));
        if (action != nameof(JellywaveWebController.Feed))
        {
            Assert.NotEmpty(h.Seen);
        }

        // Never the duplicate that shares the id, never the primary.
        Assert.All(h.Seen, s => Assert.StartsWith(MultiStationHarness.HostTwo + " ", s, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(RouteNames))]
    public async Task Without_a_station_id_the_primary_station_is_served(string action)
    {
        // An older cached ui.js sends no station id: it keeps working against the primary SUB/WAVE station.
        var h = new MultiStationHarness();

        var result = await h.RunAsync(action, null);

        Assert.Equal(StatusCodes.Status200OK, WebRoute.StatusOf(result));
        if (action != nameof(JellywaveWebController.Feed))
        {
            Assert.NotEmpty(h.Seen);
        }

        Assert.All(h.Seen, s => Assert.StartsWith(MultiStationHarness.HostLive + " ", s, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(RouteNames))]
    public async Task Without_a_station_id_and_no_listed_primary_the_route_is_404(string action)
    {
        // The primary candidate is refused (its base URL carries a query): no fallback to the next SUB/WAVE station.
        var h = new MultiStationHarness();
        h.Config.Stations[0].BaseUrl = "http://" + MultiStationHarness.HostLive + "/?q=1";

        var result = await h.RunAsync(action, null);

        Assert.Equal(StatusCodes.Status404NotFound, WebRoute.StatusOf(result));
        Assert.Empty(h.Seen);
    }

    [Fact]
    public async Task The_feed_is_the_named_station_s_own()
    {
        var h = new MultiStationHarness();

        var two = WebRoute.Value<FeedResponse>(await h.RunAsync(nameof(JellywaveWebController.Feed), "two"));
        var live = WebRoute.Value<FeedResponse>(await h.RunAsync(nameof(JellywaveWebController.Feed), "live"));
        var primary = WebRoute.Value<FeedResponse>(await h.RunAsync(nameof(JellywaveWebController.Feed), null));

        Assert.Equal(("two", "Station <b>Two</b>", "Title two", "song-two", "dj_two", h.TwoItem.Id), (two.StationId, two.StationName, two.Title, two.SongId, two.DjAvatarId, two.ItemId!.Value));
        Assert.Equal(FeedStationSwitchTests.TagProcessor.Tag, two.ImageTag);
        Assert.Equal(h.Channel.Id, two.ChannelId);
        Assert.Equal(("live", "Live One", "Title live", h.LiveItem.Id), (live.StationId, live.StationName, live.Title, live.ItemId!.Value));
        Assert.Equal(("live", "Title live"), (primary.StationId, primary.Title));
    }

    [Fact]
    public async Task A_station_not_heard_yet_answers_its_own_empty_feed_not_another_station_s()
    {
        var h = new MultiStationHarness();
        h.State.Clear("two");

        var two = WebRoute.Value<FeedResponse>(await h.RunAsync(nameof(JellywaveWebController.Feed), "two"));

        Assert.Equal("two", two.StationId);
        Assert.Null(two.Title);
        Assert.Null(two.SongId);
        Assert.Equal(h.TwoItem.Id, two.ItemId);
    }

    [Fact]
    public async Task Before_the_plugin_is_ready_nothing_resolves_and_no_station_is_asked()
    {
        var h = new MultiStationHarness();
        var resolver = new StationResolver(h.Receipts, () => null, Microsoft.Extensions.Logging.Abstractions.NullLogger<StationResolver>.Instance);
        var token = h.Receipts.Record(Alice, MultiStationHarness.KeyOf("live", MultiStationHarness.HostLive), "req-x");

        Assert.Null(resolver.Resolve(null));
        Assert.Null(resolver.Resolve("live"));
        Assert.Empty(resolver.SubWaveStations());
        Assert.Equal(ReceiptOwnership.Unknown, resolver.ResolveReceipt(token, Alice, out var station));
        Assert.Null(station);

        var controller = WebRoute.Controller(() => null, client: h.Client, receipts: h.Receipts, channel: h.Channel);
        Assert.Equal(StatusCodes.Status404NotFound, WebRoute.StatusOf(await WebRoute.RunAsync(controller, nameof(JellywaveWebController.Feed))));
        Assert.Empty(WebRoute.Value<StationsResponse>(await WebRoute.RunAsync(controller, nameof(JellywaveWebController.Stations))).Stations);
        Assert.Empty(h.Seen);
    }

    [Fact]
    public async Task Like_posts_the_song_id_to_the_station_the_body_names()
    {
        var h = new MultiStationHarness();
        var submission = new LikeSubmission { SongId = " song-two ", StationId = "two" };

        var outcome = WebRoute.Value<LikeOutcome>(await WebRoute.RunAsync(h.Controller(), nameof(JellywaveWebController.Like), ("submission", submission)));

        Assert.True(outcome.Liked);
        Assert.Equal("song-station-two", outcome.SongId);
        Assert.Equal(new[] { MultiStationHarness.HostTwo + " POST /api/like {\"songId\":\"song-two\"}" }, h.Seen);
    }

    [Theory]
    [InlineData("two")]
    [InlineData(null)]
    public async Task An_invalid_song_id_is_never_sent_to_the_station(string? stationId)
    {
        var h = new MultiStationHarness();
        var controller = h.Controller();

        var like = WebRoute.Value<LikeOutcome>(await WebRoute.RunAsync(controller, nameof(JellywaveWebController.Like), ("submission", new LikeSubmission { SongId = "../x", StationId = stationId })));
        var lyrics = WebRoute.Value<LyricsResponse>(await WebRoute.RunAsync(controller, nameof(JellywaveWebController.Lyrics), ("songId", "a b"), ("stationId", stationId)));
        var avatar = await WebRoute.RunAsync(controller, nameof(JellywaveWebController.Avatar), ("personaId", "../p"), ("stationId", stationId));

        Assert.False(like.Enabled);
        Assert.Equal(("unavailable", "No track to look up."), (lyrics.Status, lyrics.Message));
        Assert.IsType<NotFoundResult>(avatar);
        Assert.Empty(h.Seen);
    }

    [Fact]
    public async Task Lyrics_schedule_and_like_status_come_from_the_named_station()
    {
        var h = new MultiStationHarness();

        var lyrics = WebRoute.Value<LyricsResponse>(await WebRoute.RunAsync(h.Controller(), nameof(JellywaveWebController.Lyrics), ("songId", "song-two"), ("stationId", "two")));
        var schedule = WebRoute.Value<ScheduleResponse>(await WebRoute.RunAsync(h.Controller(), nameof(JellywaveWebController.Schedule), ("stationId", "two")));
        var liveSchedule = WebRoute.Value<ScheduleResponse>(await WebRoute.RunAsync(h.Controller(), nameof(JellywaveWebController.Schedule), ("stationId", "live")));
        var like = WebRoute.Value<LikeOutcome>(await WebRoute.RunAsync(h.Controller(), nameof(JellywaveWebController.LikeStatus), ("stationId", "two")));

        Assert.Equal(new[] { MultiStationHarness.HostTwo }, lyrics.Lines);
        Assert.Equal(MultiStationHarness.HostTwo, schedule.Today.Single().Show);
        Assert.Equal(MultiStationHarness.HostLive, liveSchedule.Today.Single().Show);

        // Each station keeps its own copy: asking the first station again is served from the cache.
        var again = WebRoute.Value<ScheduleResponse>(await WebRoute.RunAsync(h.Controller(), nameof(JellywaveWebController.Schedule), ("stationId", "two")));
        Assert.Equal(MultiStationHarness.HostTwo, again.Today.Single().Show);
        Assert.Single(h.Seen, s => s.StartsWith(MultiStationHarness.HostTwo + " GET /api/schedule", StringComparison.Ordinal));
        Assert.Equal("song-station-two", like.SongId);
        Assert.Contains(MultiStationHarness.HostTwo + " GET /api/lyrics/current", h.Seen);
    }

    [Fact]
    public async Task A_request_goes_to_the_named_station_and_its_receipt_records_that_station()
    {
        var h = new MultiStationHarness();

        var outcome = WebRoute.Value<RequestOutcome>(await h.RunAsync(nameof(JellywaveWebController.SubmitRequest), "two", Alice));

        Assert.True(outcome.Accepted);

        // Card R2a: the page gets our receipt token, never the station's request id.
        Assert.NotEqual("req-station-two", outcome.RequestId);
        Assert.Equal(RequestReceipts.TokenLength, outcome.RequestId!.Length);
        Assert.Equal(ReceiptOwnership.Owner, h.Receipts.Check(outcome.RequestId, Alice, out var station));
        Assert.Equal(ReceiptOwnership.Unknown, h.Receipts.Check("req-station-two", Alice, out _));
        Assert.Equal(MultiStationHarness.KeyOf("two", MultiStationHarness.HostTwo), station);
        Assert.Equal("req-station-two", h.Receipts.StationRequestIdOf(outcome.RequestId, Alice, station!));
    }

    [Fact]
    public async Task A_receipt_routes_to_its_own_station_when_the_primary_has_changed()
    {
        var h = new MultiStationHarness();
        var token = Token(await h.RunAsync(nameof(JellywaveWebController.SubmitRequest), null, Alice));   // primary: "live"
        Assert.Equal(new[] { MultiStationHarness.HostLive + " POST /radio/api/request {\"text\":\"something with horns\",\"name\":\"A\"}" }, h.Seen);

        h.Config.Stations[0].SortOrder = 10;   // "two" is the primary station now
        h.Station.Seen.Clear();
        var polled = WebRoute.Value<RequestOutcome>(await Poll(h, Alice, token));

        Assert.Equal("from " + MultiStationHarness.HostLive, polled.Ack);
        Assert.Equal(new[] { MultiStationHarness.HostLive + " GET /radio/api/request/req-station-live" }, h.Seen);
    }

    [Fact]
    public async Task The_poll_ignores_a_station_id_the_caller_adds()
    {
        var h = new MultiStationHarness();
        var token = Token(await h.RunAsync(nameof(JellywaveWebController.SubmitRequest), "two", Alice));
        h.Station.Seen.Clear();

        var polled = WebRoute.Value<RequestOutcome>(await Poll(h, Alice, token, ("stationId", "live")));

        Assert.Equal("from " + MultiStationHarness.HostTwo, polled.Ack);
        Assert.All(h.Seen, s => Assert.StartsWith(MultiStationHarness.HostTwo + " ", s, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Another_user_s_receipt_is_403_and_no_station_is_asked()
    {
        var h = new MultiStationHarness();
        var token = Token(await h.RunAsync(nameof(JellywaveWebController.SubmitRequest), "two", Alice));
        h.Station.Seen.Clear();

        var polled = await Poll(h, Bob, token);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(polled).StatusCode);
        Assert.Empty(h.Seen);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("removed")]
    [InlineData("plain")]
    [InlineData("moved")]
    [InlineData("refused")]
    public async Task A_receipt_whose_station_is_no_longer_listed_is_lost_track_without_a_station_call(string change)
    {
        var h = new MultiStationHarness();
        var token = Token(await h.RunAsync(nameof(JellywaveWebController.SubmitRequest), "two", Alice));
        h.Station.Seen.Clear();
        var two = h.Config.Stations[1];
        switch (change)
        {
            case "disabled": two.Enabled = false; break;
            case "removed": h.Config.Stations.Remove(two); break;
            case "plain": two.Kind = Configuration.StationKind.Stream; two.StreamUrl = "https://" + MultiStationHarness.HostTwo + "/s.mp3"; break;
            case "moved": two.BaseUrl = "http://" + MultiStationHarness.HostDup; break;   // same id, another origin
            case "refused": two.BaseUrl = "http://" + MultiStationHarness.HostTwo + ":7700/#x"; break;
        }

        var polled = WebRoute.Value<RequestOutcome>(await Poll(h, Alice, token));

        Assert.Equal("unknown", polled.Status);
        Assert.Equal(token, polled.RequestId);
        Assert.Empty(h.Seen);
    }

    [Fact]
    public async Task An_unknown_request_id_is_lost_track_without_a_station_call()
    {
        var h = new MultiStationHarness();

        var polled = WebRoute.Value<RequestOutcome>(await Poll(h, Alice, "req-never"));

        Assert.Equal(("unknown", "req-never"), (polled.Status, polled.RequestId));
        Assert.Empty(h.Seen);
    }

    [Fact]
    public async Task The_route_filter_refuses_to_run_without_the_access_filter_s_caller()
    {
        var h = new MultiStationHarness();
        var controller = h.Controller();
        controller.HttpContext.Items.Remove(StationAccess.UserIdItem);

        await Assert.ThrowsAsync<InvalidOperationException>(() => WebRoute.RunAsync(controller, nameof(JellywaveWebController.LikeStatus), ("stationId", "two")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WebRoute.RunAsync(controller, nameof(JellywaveWebController.RequestStatus), ("requestId", "req-x")));
        Assert.Empty(h.Seen);
    }

    [Fact]
    public async Task An_action_run_without_the_route_filter_fails_instead_of_serving_a_station()
    {
        var h = new MultiStationHarness();
        var controller = h.Controller();

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.LikeStatus("two", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RequestStatus("req-x", CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => controller.Feed("two"));
        Assert.Empty(h.Seen);
    }

    [Fact]
    public void The_body_s_station_id_is_read_only_when_no_query_parameter_is_bound()
    {
        Assert.Equal("two", StationRouteAttribute.StationIdOf(new Dictionary<string, object?> { ["submission"] = new LikeSubmission { StationId = "two" } }));
        Assert.Null(StationRouteAttribute.StationIdOf(new Dictionary<string, object?> { ["submission"] = new LikeSubmission() }));
        Assert.Null(StationRouteAttribute.StationIdOf(new Dictionary<string, object?>()));
        Assert.Equal("q", StationRouteAttribute.StationIdOf(new Dictionary<string, object?> { ["stationId"] = "q" }));
    }

    private static Task<IActionResult> Poll(MultiStationHarness h, Guid user, string requestId, params (string Name, object? Value)[] more)
        => WebRoute.RunAsync(h.Controller(user), nameof(JellywaveWebController.RequestStatus), [("requestId", requestId), .. more]);

    /// <summary>The receipt token an accepted request answered with (card R2a), which the page polls with.</summary>
    private static string Token(IActionResult submitted)
    {
        var outcome = WebRoute.Value<RequestOutcome>(submitted);
        Assert.True(outcome.Accepted);
        return outcome.RequestId!;
    }
}
