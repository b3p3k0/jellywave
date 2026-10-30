using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L5: every station's page asks for its own schedule, so fetches are serialized per station. A station that does
/// not answer holds up only its own schedule.
/// </summary>
public class ScheduleGateTests
{
    private static readonly Uri Slow = new("http://slow.example/");
    private static readonly Uri Quick = new("http://quick.example/");

    private sealed class Stations : HttpMessageHandler
    {
        public TaskCompletionSource SlowAnswers { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Seen { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            lock (Seen)
            {
                Seen.Add(host);
            }

            if (host == Slow.Host)
            {
                await SlowAnswers.Task.WaitAsync(cancellationToken);
            }

            var day = "[" + string.Join(',', Enumerable.Repeat("\"s1\"", 24)) + "]";
            var grid = string.Join(',', Enumerable.Range(0, 7).Select(d => "\"" + d + "\":" + day));
            var json = "{\"timezone\":\"UTC\",\"shows\":[{\"id\":\"s1\",\"name\":\"" + host + "\"}],\"schedule\":{" + grid + "}}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public Factory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    [Fact]
    public async Task A_station_that_does_not_answer_holds_up_only_its_own_schedule()
    {
        var stations = new Stations();
        using var cache = new ScheduleCache(new StationClient(new Factory(stations)));

        var slow = cache.GetAsync("slow", Slow, CancellationToken.None);
        while (!stations.Seen.Contains(Slow.Host))
        {
            await Task.Delay(5);
        }

        var quick = await cache.GetAsync("quick", Quick, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Quick.Host, quick!.Shows![0].Name);
        Assert.False(slow.IsCompleted);

        // The same station's second caller still waits for the first fetch, then gets its copy without asking again.
        var again = cache.GetAsync("slow", Slow, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(again.IsCompleted);
        stations.SlowAnswers.SetResult();
        Assert.Equal(Slow.Host, (await slow)!.Shows![0].Name);
        Assert.Equal(Slow.Host, (await again)!.Shows![0].Name);
        Assert.Equal(1, stations.Seen.Count(h => h == Slow.Host));
    }

    [Fact]
    public async Task Retain_drops_the_gates_of_stations_no_longer_listed()
    {
        var stations = new Stations();
        stations.SlowAnswers.SetResult();
        using var cache = new ScheduleCache(new StationClient(new Factory(stations)));
        await cache.GetAsync("slow", Slow, CancellationToken.None);
        await cache.GetAsync("quick", Quick, CancellationToken.None);
        Assert.Equal(2, cache.GateCount);

        cache.Retain(new HashSet<string>(StringComparer.Ordinal) { "quick" });

        Assert.Equal(1, cache.GateCount);
        Assert.Equal(Quick.Host, (await cache.GetAsync("quick", Quick, CancellationToken.None))!.Shows![0].Name);
        Assert.Equal(1, cache.GateCount);
        Assert.Equal(1, stations.Seen.Count(h => h == Quick.Host));   // its copy was kept with its gate
    }
}
