using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 17 (D2): <see cref="ScheduleCache"/> keeps one schedule per station origin. Fresh copies are served, a stale
/// copy of the same station survives a failure and is retried after <see cref="ScheduleCache.RetryDelay"/>, and a change
/// of station URL never serves the old station's copy, not even when the new station fails.
/// </summary>
public class ScheduleCacheTests
{
    private const string StationA = "http://station-a.example";
    private const string StationB = "http://station-b.example";

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Answers <c>/api/schedule</c> per host with a schedule whose only show is named after the host.</summary>
    private sealed class Stations : HttpMessageHandler
    {
        public HashSet<string> Down { get; } = new();

        public List<string> Seen { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            Seen.Add(host + request.RequestUri.AbsolutePath);
            if (Down.Contains(host))
            {
                throw new HttpRequestException("connection refused");
            }

            var day = "[" + string.Join(',', Enumerable.Repeat("\"s1\"", 24)) + "]";
            var grid = string.Join(',', Enumerable.Range(0, 7).Select(d => "\"" + d + "\":" + day));
            var json = "{\"timezone\":\"UTC\",\"shows\":[{\"id\":\"s1\",\"name\":\"" + host + "\"}],\"schedule\":{" + grid + "}}";
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
        public Harness()
        {
            Client = new StationClient(new StubFactory(Station));
            Cache = new ScheduleCache(Client, Clock);
        }

        public Stations Station { get; } = new();

        public ManualClock Clock { get; } = new();

        public StationClient Client { get; }

        public ScheduleCache Cache { get; }

        public PluginConfiguration Config { get; } = new() { StationBaseUrl = StationA };

        public async Task<string?> ShowAsync()
            => (await Cache.GetAsync(Config, CancellationToken.None))?.Shows?.Single().Name;

        public void Advance(TimeSpan by) => Clock.Now += by;
    }

    [Fact]
    public async Task A_fresh_copy_is_served_without_asking_the_station_again()
    {
        var h = new Harness();
        Assert.Equal("station-a.example", await h.ShowAsync());
        h.Advance(ScheduleCache.Lifetime - TimeSpan.FromSeconds(1));
        Assert.Equal("station-a.example", await h.ShowAsync());
        Assert.Single(h.Station.Seen);

        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("station-a.example", await h.ShowAsync());
        Assert.Equal(2, h.Station.Seen.Count);
    }

    [Fact]
    public async Task A_stale_copy_of_the_same_station_is_served_after_a_failure()
    {
        var h = new Harness();
        await h.ShowAsync();
        h.Advance(ScheduleCache.Lifetime);
        h.Station.Down.Add("station-a.example");

        Assert.Equal("station-a.example", await h.ShowAsync());
        Assert.Equal(2, h.Station.Seen.Count);
    }

    [Fact]
    public async Task After_a_failure_the_station_is_asked_again_only_after_the_retry_delay()
    {
        var h = new Harness();
        await h.ShowAsync();
        h.Advance(ScheduleCache.Lifetime);
        h.Station.Down.Add("station-a.example");
        await h.ShowAsync();

        h.Advance(ScheduleCache.RetryDelay - TimeSpan.FromSeconds(1));
        await h.ShowAsync();
        Assert.Equal(2, h.Station.Seen.Count);

        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("station-a.example", await h.ShowAsync());
        Assert.Equal(3, h.Station.Seen.Count);
    }

    [Fact]
    public async Task A_new_station_url_is_fetched_at_once_even_while_the_old_copy_is_fresh()
    {
        var h = new Harness();
        await h.ShowAsync();
        h.Config.StationBaseUrl = StationB;

        Assert.Equal("station-b.example", await h.ShowAsync());
        Assert.Equal(new[] { "station-a.example/api/schedule", "station-b.example/api/schedule" }, h.Station.Seen);
    }

    [Fact]
    public async Task A_new_station_that_fails_never_gets_the_old_stations_copy()
    {
        var h = new Harness();
        await h.ShowAsync();
        h.Config.StationBaseUrl = StationB;
        h.Station.Down.Add("station-b.example");

        // No copy of this station exists, so the failure propagates (the route turns it into an empty schedule).
        await Assert.ThrowsAsync<HttpRequestException>(h.ShowAsync);

        // Back to the first station: its old copy was dropped with the switch, so it is fetched again.
        h.Config.StationBaseUrl = StationA;
        Assert.Equal("station-a.example", await h.ShowAsync());
        Assert.Equal(3, h.Station.Seen.Count);
    }

    [Fact]
    public async Task Through_the_route_a_failing_new_station_shows_no_schedule()
    {
        var h = new Harness();
        var controller = new JellywaveWebController(
            new NowPlayingState(),
            new LiveItemFinder(null!, () => [], NullLogger<LiveItemFinder>.Instance),
            null!,
            h.Client,
            h.Cache,
            new RequestReceipts(),
            NullLogger<JellywaveWebController>.Instance,
            () => h.Config);

        var first = (await controller.Schedule(CancellationToken.None)).Value!;
        Assert.True(first.Available);
        Assert.Equal("station-a.example", first.Today.Single().Show);

        h.Config.StationBaseUrl = StationB;
        h.Station.Down.Add("station-b.example");
        var after = (await controller.Schedule(CancellationToken.None)).Value!;
        Assert.False(after.Available);
        Assert.Empty(after.Today);
    }

    [Fact]
    public async Task Without_a_copy_a_failing_station_is_asked_again_only_after_the_retry_delay()
    {
        var h = new Harness();
        await h.ShowAsync();
        h.Config.StationBaseUrl = StationB;
        h.Station.Down.Add("station-b.example");
        await Assert.ThrowsAsync<HttpRequestException>(h.ShowAsync);

        h.Advance(ScheduleCache.RetryDelay - TimeSpan.FromSeconds(1));
        Assert.Null(await h.ShowAsync());
        Assert.Equal(2, h.Station.Seen.Count);

        h.Station.Down.Clear();
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("station-b.example", await h.ShowAsync());
        Assert.Equal(3, h.Station.Seen.Count);
    }
}
