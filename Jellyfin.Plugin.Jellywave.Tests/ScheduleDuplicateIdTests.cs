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
/// Card 17 (D6): the station's show and persona ids are not trusted to be unique. The first one wins and a repeat
/// never throws, so the Schedule route answers instead of 500.
/// </summary>
public class ScheduleDuplicateIdTests
{
    private static readonly DateTime Tuesday1030 = new(2026, 10, 27, 10, 30, 0, DateTimeKind.Utc);

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

    private static ScheduleInfo Info(IReadOnlyList<ScheduleShow> shows, IReadOnlyList<SchedulePersona> personas)
    {
        var tuesday = new List<string?>(new string?[24]);
        for (var h = 8; h < 12; h++) { tuesday[h] = "s1"; }
        return new ScheduleInfo { Timezone = "UTC", Shows = shows, Personas = personas, Schedule = new Dictionary<string, List<string?>> { ["2"] = tuesday } };
    }

    [Fact]
    public void A_duplicate_show_id_keeps_the_first_show()
    {
        var info = Info(
            [new ScheduleShow { Id = "s1", Name = "First", PersonaId = "p1" }, new ScheduleShow { Id = "s1", Name = "Second", PersonaId = "p1" }],
            [new SchedulePersona { Id = "p1", Name = "Host" }]);

        var r = JellywaveWebController.BuildToday(info, Tuesday1030);

        Assert.True(r.Available);
        Assert.Equal(("First", "Host", 8, 12), (r.Today.Single().Show, r.Today.Single().Host, r.Today.Single().StartHour, r.Today.Single().EndHour));
    }

    [Fact]
    public void A_duplicate_persona_id_keeps_the_first_persona()
    {
        var info = Info(
            [new ScheduleShow { Id = "s1", Name = "Show", PersonaId = "p1" }],
            [new SchedulePersona { Id = "p1", Name = "First host" }, new SchedulePersona { Id = "p1", Name = "Second host" }]);

        var r = JellywaveWebController.BuildToday(info, Tuesday1030);

        Assert.Equal("First host", r.Today.Single().Host);
    }

    [Fact]
    public void Null_entries_in_the_lists_are_skipped()
    {
        var info = Info([null!, new ScheduleShow { Id = "s1", Name = "Show", PersonaId = "p1" }], [null!, new SchedulePersona { Id = "p1", Name = "Host" }]);

        var r = JellywaveWebController.BuildToday(info, Tuesday1030);

        Assert.Equal(("Show", "Host"), (r.Today.Single().Show, r.Today.Single().Host));
    }

    [Fact]
    public async Task Through_the_route_a_station_with_duplicate_ids_gets_a_schedule()
    {
        var day = "[" + string.Join(',', Enumerable.Repeat("\"s1\"", 24)) + "]";
        var grid = string.Join(',', Enumerable.Range(0, 7).Select(d => "\"" + d + "\":" + day));
        var station = new StubStation
        {
            Json = "{\"timezone\":\"UTC\",\"shows\":[{\"id\":\"s1\",\"name\":\"First\",\"personaId\":\"p1\"},{\"id\":\"s1\",\"name\":\"Second\"},null],"
                + "\"personas\":[{\"id\":\"p1\",\"name\":\"Host\"},{\"id\":\"p1\",\"name\":\"Other\"}],\"schedule\":{" + grid + "}}"
        };
        var client = new StationClient(new StubFactory(station));
        var controller = new JellywaveWebController(
            new NowPlayingState(),
            new LiveItemFinder(null!, () => [], NullLogger<LiveItemFinder>.Instance),
            null!,
            client,
            new ScheduleCache(client),
            new RequestReceipts(),
            NullLogger<JellywaveWebController>.Instance,
            () => new PluginConfiguration { StationBaseUrl = "http://station.example" });

        var r = (await controller.Schedule(CancellationToken.None)).Value!;

        Assert.True(r.Available);
        Assert.Equal(("First", "Host"), (r.Today.Single().Show, r.Today.Single().Host));
    }
}
