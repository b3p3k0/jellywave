using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L6: <c>GET /Jellywave/Title?stationId=</c> resolves only an enabled, listed plain stream (the plain-stream variant
/// of the station route filter), answers from the sampler's state without sampling, and carries no URL. SUB/WAVE, disabled,
/// refused, unknown and missing ids are 404; the SUB/WAVE routes still refuse a plain stream's id.
/// </summary>
public class TitleRouteTests
{
    public static IEnumerable<object?[]> NotPlainStreams()
        => new[] { "live", "two", "off", "bad", "nope", string.Empty, " ", "PLAIN", "Plain", "plain ", " plain", "../plain", null }.Select(id => new object?[] { id });

    [Fact]
    public async Task A_listed_plain_stream_answers_its_state()
    {
        var h = new MultiStationHarness();
        h.Titles.Record("plain", StationTitles.StateTitle, "Artist - Title", new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc));

        var answer = WebRoute.Value<TitleResponse>(await h.RunAsync(nameof(JellywaveWebController.Title), "plain"));

        Assert.Equal("plain", answer.StationId);
        Assert.Equal(StationTitles.StateTitle, answer.State);
        Assert.Equal("Artist - Title", answer.Title);
        Assert.Equal(new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc), answer.UpdatedUtc);
    }

    [Theory]
    [InlineData(StationTitles.StateNoTitles)]
    [InlineData(StationTitles.StateFailing)]
    public async Task Other_states_carry_no_title(string state)
    {
        var h = new MultiStationHarness();
        h.Titles.Record("plain", StationTitles.StateTitle, "Old title", DateTime.UtcNow);
        h.Titles.Record("plain", state, "ignored", DateTime.UtcNow);

        var answer = WebRoute.Value<TitleResponse>(await h.RunAsync(nameof(JellywaveWebController.Title), "plain"));

        Assert.Equal(state, answer.State);
        Assert.Null(answer.Title);
    }

    [Fact]
    public async Task With_show_what_s_playing_off_the_route_says_off()
    {
        var h = new MultiStationHarness();
        h.Titles.Record("plain", StationTitles.StateTitle, "Artist - Title", DateTime.UtcNow);
        h.Titles.SetEnabled(false);

        var answer = WebRoute.Value<TitleResponse>(await h.RunAsync(nameof(JellywaveWebController.Title), "plain"));

        Assert.Equal(StationTitles.StateOff, answer.State);
        Assert.Null(answer.Title);
        Assert.Equal(StatusCodes.Status404NotFound, WebRoute.StatusOf(await h.RunAsync(nameof(JellywaveWebController.Title), "live")));   // still only plain streams
    }

    [Fact]
    public async Task Without_a_sample_the_route_says_waiting_and_samples_nothing()
    {
        var h = new MultiStationHarness();

        var answer = WebRoute.Value<TitleResponse>(await h.RunAsync(nameof(JellywaveWebController.Title), "plain"));

        Assert.Equal(StationTitles.StateWaiting, answer.State);
        Assert.Null(answer.Title);
        Assert.Null(answer.UpdatedUtc);
        Assert.Empty(h.Seen);
        Assert.Equal(0, h.Titles.Count());   // reading makes no entry: only the sampler writes
    }

    [Theory]
    [MemberData(nameof(NotPlainStreams))]
    public async Task Anything_but_a_listed_plain_stream_is_404(string? stationId)
    {
        var h = new MultiStationHarness();
        h.Titles.Record("live", StationTitles.StateTitle, "never served", DateTime.UtcNow);
        h.Titles.Record("off", StationTitles.StateTitle, "never served", DateTime.UtcNow);

        var result = await h.RunAsync(nameof(JellywaveWebController.Title), stationId);

        Assert.Equal(StatusCodes.Status404NotFound, WebRoute.StatusOf(result));
        Assert.Empty(h.Seen);
    }

    [Fact]
    public async Task A_disabled_or_refused_plain_stream_is_404()
    {
        var h = new MultiStationHarness();
        var plain = h.Config.Stations.Single(s => s.Id == "plain");
        h.Titles.Record("plain", StationTitles.StateTitle, "x", DateTime.UtcNow);

        plain.Enabled = false;
        Assert.Equal(StatusCodes.Status404NotFound, WebRoute.StatusOf(await h.RunAsync(nameof(JellywaveWebController.Title), "plain")));

        plain.Enabled = true;
        plain.StreamUrl = "https://" + MultiStationHarness.HostPlain + "/list.pls";
        Assert.Equal(StatusCodes.Status404NotFound, WebRoute.StatusOf(await h.RunAsync(nameof(JellywaveWebController.Title), "plain")));

        plain.StreamUrl = "https://" + MultiStationHarness.HostPlain + "/p.mp3";
        Assert.Equal(StatusCodes.Status200OK, WebRoute.StatusOf(await h.RunAsync(nameof(JellywaveWebController.Title), "plain")));
    }

    [Fact]
    public async Task A_plain_stream_that_became_sub_wave_is_404_on_the_title_route()
    {
        var h = new MultiStationHarness();
        var plain = h.Config.Stations.Single(s => s.Id == "plain");
        plain.Kind = StationKind.SubWave;
        plain.BaseUrl = "http://" + MultiStationHarness.HostPlain;

        Assert.Equal(StatusCodes.Status404NotFound, WebRoute.StatusOf(await h.RunAsync(nameof(JellywaveWebController.Title), "plain")));
    }

    [Fact]
    public async Task The_answer_carries_no_url()
    {
        // Every field of the answer, serialized as the route sends it, holds no stream address or query.
        var h = new MultiStationHarness();
        h.Titles.Record("plain", StationTitles.StateTitle, "Artist - Title", DateTime.UtcNow);
        var answer = WebRoute.Value<TitleResponse>(await h.RunAsync(nameof(JellywaveWebController.Title), "plain"));
        var json = JsonSerializer.Serialize(answer);

        Assert.DoesNotContain("http", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(MultiStationHarness.HostPlain, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-query", json, StringComparison.Ordinal);
        Assert.All(typeof(TitleResponse).GetProperties(), p => Assert.True(p.PropertyType == typeof(string) || p.PropertyType == typeof(DateTime?), p.Name));
    }

    [Theory]
    [InlineData(nameof(JellywaveWebController.Feed))]
    [InlineData(nameof(JellywaveWebController.LikeStatus))]
    [InlineData(nameof(JellywaveWebController.Schedule))]
    [InlineData(nameof(JellywaveWebController.Lyrics))]
    [InlineData(nameof(JellywaveWebController.Avatar))]
    public async Task The_sub_wave_routes_still_refuse_a_plain_stream_with_a_title(string action)
    {
        var h = new MultiStationHarness();
        h.Titles.Record("plain", StationTitles.StateTitle, "Artist - Title", DateTime.UtcNow);

        var result = await h.RunAsync(action, "plain");

        Assert.Equal(StatusCodes.Status404NotFound, WebRoute.StatusOf(result));
        Assert.Empty(h.Seen);
    }

    [Fact]
    public void The_plain_filter_leaves_the_id_alone_and_the_sub_wave_filter_never_a_plain_stream()
    {
        var h = new MultiStationHarness();
        var resolver = new StationResolver(h.Receipts, () => h.Config, NullLogger<StationResolver>.Instance);

        var plain = Run(new StationRouteAttribute { PlainStream = true }, resolver, "plain");
        Assert.Null(plain.Result);
        var left = Assert.IsType<PlainStation>(plain.HttpContext.Items[StationRouteAttribute.PlainStationItem]);
        Assert.Equal("plain", left.StationId);
        Assert.False(plain.HttpContext.Items.ContainsKey(StationRouteAttribute.StationItem));
        Assert.Single(typeof(PlainStation).GetProperties());   // the id, nothing else

        var subWave = Run(new StationRouteAttribute(), resolver, "two");
        Assert.Null(subWave.Result);
        Assert.False(subWave.HttpContext.Items.ContainsKey(StationRouteAttribute.PlainStationItem));

        Assert.IsType<StatusCodeResult>(Run(new StationRouteAttribute(), resolver, "plain").Result);
        Assert.IsType<StatusCodeResult>(Run(new StationRouteAttribute { PlainStream = true }, resolver, "two").Result);
        Assert.IsType<StatusCodeResult>(Run(new StationRouteAttribute { PlainStream = true }, resolver, null).Result);
    }

    [Fact]
    public void The_plain_resolver_follows_the_listed_streams()
    {
        var h = new MultiStationHarness();
        var resolver = new StationResolver(h.Receipts, () => h.Config, NullLogger<StationResolver>.Instance);

        Assert.Equal("plain", resolver.ResolvePlainStream("plain")!.Station.Id);
        Assert.Null(resolver.ResolvePlainStream("live"));
        Assert.Null(resolver.ResolvePlainStream(null));
        Assert.Null(new StationResolver(h.Receipts, () => null, NullLogger<StationResolver>.Instance).ResolvePlainStream("plain"));
    }

    private static ActionExecutingContext Run(StationRouteAttribute filter, StationResolver resolver, string? stationId)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(resolver).AddSingleton(new StationDemand(new FakePlayingItems())).BuildServiceProvider() };
        http.Items[StationAccess.UserIdItem] = WebRoute.Listener;
        var arguments = new Dictionary<string, object?>();
        if (stationId is not null)
        {
            arguments[StationRouteAttribute.StationIdParameter] = stationId;
        }

        var context = new ActionExecutingContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>(), arguments, null!);
        filter.OnActionExecuting(context);
        return context;
    }
}
