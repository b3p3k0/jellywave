using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card R2a: every user-driven station call goes through <see cref="StationCalls"/>: the caller's budget first (429), then
/// the shared limiter (503), then the station. The permit is held until the answer is read and disposed and given back on
/// every exit; a station's refusal and a call that never left give the budget back, an uncertain outcome does not.
/// </summary>
public class StationCallsTests
{
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Uri One = new("http://one.example/");
    private static readonly Uri Two = new("http://two.example/");
    private static readonly StationKey KeyOne = StationKey.Of("one", One);
    private static readonly StationKey KeyTwo = StationKey.Of("two", Two);

    /// <summary>Answers every call with what <see cref="Answer"/> says; counts calls by host.</summary>
    internal sealed class Station : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Answer { get; set; } = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{}"));

        public ConcurrentQueue<string> Seen { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Enqueue(request.RequestUri!.Host + " " + request.Method + " " + request.RequestUri.AbsolutePath);
            return Answer(request, cancellationToken);
        }

        public static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>A body that arrives only when the test lets it.</summary>
    private sealed class HeldContent(TaskCompletionSource release) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await release.Task.ConfigureAwait(false);
            await stream.WriteAsync("{\"liked\":true}"u8.ToArray()).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private sealed class Setup
    {
        public Setup(TimeSpan? timeout = null)
        {
            Calls = new StationCalls(new StationClient(new Factory(Station), timeout ?? TimeSpan.FromSeconds(8)), Budgets, Limiter, NullLogger<StationCalls>.Instance, Clock);
        }

        public Station Station { get; } = new();

        public UserBudgetsTests.ManualClock Clock { get; } = new();

        public UserBudgets Budgets => _budgets ??= new UserBudgets(Clock);

        public StationCallLimiter Limiter { get; } = new();

        public StationCalls Calls { get; }

        private UserBudgets? _budgets;
    }

    [Fact]
    public async Task Over_the_budget_is_429_with_the_wait_and_the_station_is_not_asked()
    {
        var s = new Setup();
        Assert.True((await s.Calls.SubmitRequestAsync(Alice, KeyOne, One, "a", "", () => true, CancellationToken.None)).Answered);

        var refused = await s.Calls.SubmitRequestAsync(Alice, KeyTwo, Two, "b", "", () => true, CancellationToken.None);

        Assert.Equal(StatusCodes.Status429TooManyRequests, refused.Refusal);
        Assert.Equal(TimeSpan.FromMinutes(1), refused.RetryAfter);
        Assert.False(refused.Answered);
        Assert.Single(s.Station.Seen);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_station_s_refusal_gives_back_the_request_windows_but_keeps_the_general_slot(HttpStatusCode status)
    {
        // Fix round (RA): a station's refusal does not use the request budget (1 a minute, 4 an hour), but it still costs
        // one of the 60 general calls, so refused calls cannot be looped for free.
        var s = new Setup();
        s.Station.Answer = (_, _) => Task.FromResult(Station.Json(status, "{\"message\":\"no\"}"));
        for (var i = 0; i < 5; i++)
        {
            var call = await s.Calls.SubmitRequestAsync(Alice, KeyOne, One, "a", "", () => true, CancellationToken.None);
            Assert.Equal(0, call.Refusal);
            Assert.Equal((int)status, call.Value!.StatusCode);
        }

        Assert.Equal(5, s.Station.Seen.Count);
        Assert.Equal(UserBudgets.CallsPerMinute - 5, Enumerable.Range(0, 100).Count(i => s.Budgets.TryReserve(Alice, StationCallKind.Lyrics, out _) is not null));
    }

    [Fact]
    public async Task Lyrics_the_station_refuses_with_409_cost_the_general_budget_and_are_not_kept()
    {
        // SUB/WAVE answers 409 to any song id not on air: a loop of made-up ids is refused here after 60 in a minute.
        var s = new Setup();
        s.Station.Answer = (_, _) => Task.FromResult(Station.Json(HttpStatusCode.Conflict, "{\"error\":\"not on air\"}"));
        var refusals = 0;
        for (var i = 0; i < 100; i++)
        {
            var call = await s.Calls.GetLyricsAsync(Alice, KeyOne, One, "x" + i, CancellationToken.None);
            refusals += call.Refusal == StatusCodes.Status429TooManyRequests ? 1 : 0;
        }

        Assert.Equal(UserBudgets.CallsPerMinute, s.Station.Seen.Count);
        Assert.Equal(100 - UserBudgets.CallsPerMinute, refusals);
        Assert.Equal(0, s.Calls.Lyrics.Count);   // a 409 is never kept, so it cannot push out real lyrics
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Any_other_answer_uses_it(HttpStatusCode status)
    {
        var s = new Setup();
        s.Station.Answer = (_, _) => Task.FromResult(Station.Json(status, "{}"));
        await s.Calls.SubmitRequestAsync(Alice, KeyOne, One, "a", "", () => true, CancellationToken.None);

        Assert.Equal(StatusCodes.Status429TooManyRequests, (await s.Calls.SubmitRequestAsync(Alice, KeyOne, One, "a", "", () => true, CancellationToken.None)).Refusal);
    }

    [Fact]
    public async Task A_timeout_is_uncertain_so_it_uses_the_quota_and_answers_nothing()
    {
        var s = new Setup(TimeSpan.FromMilliseconds(100));
        s.Station.Answer = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Station.Json(HttpStatusCode.Accepted, "{}");
        };

        var call = await s.Calls.SubmitRequestAsync(Alice, KeyOne, One, "a", "", () => true, CancellationToken.None);

        Assert.False(call.Answered);
        Assert.Equal(0, call.Refusal);
        Assert.Equal(0, s.Limiter.InUse);
        Assert.Equal(StatusCodes.Status429TooManyRequests, (await s.Calls.SubmitRequestAsync(Alice, KeyOne, One, "a", "", () => true, CancellationToken.None)).Refusal);
    }

    [Fact]
    public async Task A_connection_lost_after_sending_uses_the_quota_and_one_never_made_does_not()
    {
        var s = new Setup();
        s.Station.Answer = (_, _) => throw new HttpRequestException(HttpRequestError.ResponseEnded, "lost");
        Assert.False((await s.Calls.SubmitRequestAsync(Alice, KeyOne, One, "a", "", () => true, CancellationToken.None)).Answered);
        Assert.Equal(StatusCodes.Status429TooManyRequests, (await s.Calls.SubmitRequestAsync(Alice, KeyOne, One, "a", "", () => true, CancellationToken.None)).Refusal);

        var t = new Setup();
        t.Station.Answer = (_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused");
        for (var i = 0; i < 3; i++)
        {
            Assert.False((await t.Calls.SubmitRequestAsync(Alice, KeyOne, One, "a", "", () => true, CancellationToken.None)).Answered);
        }

        Assert.Equal(3, t.Station.Seen.Count);
        Assert.Equal(0, t.Limiter.InUse);
    }

    [Fact]
    public async Task A_full_station_refuses_the_next_call_with_503_while_another_station_is_served()
    {
        var s = new Setup();
        var release = new TaskCompletionSource();
        s.Station.Answer = async (r, _) =>
        {
            if (r.RequestUri!.Host == One.Host)
            {
                await release.Task;
            }

            return Station.Json(HttpStatusCode.OK, "{\"lines\":[]}");
        };

        // One song per call: one song's misses would share one fetch.
        var held = Enumerable.Range(0, StationCallLimiter.MaxPerStation).Select(i => s.Calls.GetLyricsAsync(Guid.NewGuid(), KeyOne, One, "song" + i, CancellationToken.None)).ToList();
        await WaitFor(() => s.Limiter.InUse == StationCallLimiter.MaxPerStation);

        var next = await s.Calls.GetLyricsAsync(Guid.NewGuid(), KeyOne, One, "song-next", CancellationToken.None);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, next.Refusal);
        Assert.Equal(StationCalls.BusyRetryAfter, next.RetryAfter);
        Assert.True((await s.Calls.GetLyricsAsync(Alice, KeyTwo, Two, "song", CancellationToken.None)).Answered);

        release.SetResult();
        Assert.All(await Task.WhenAll(held), a => Assert.True(a.Answered));
        Assert.Equal(0, s.Limiter.InUse);
        Assert.Equal(StationCallLimiter.MaxPerStation + 1, s.Station.Seen.Count);   // the refused call never reached the station
    }

    [Fact]
    public async Task At_most_four_portraits_are_fetched_at_once_and_other_calls_still_go()
    {
        var s = new Setup();
        var release = new TaskCompletionSource();
        s.Station.Answer = async (r, _) =>
        {
            if (r.RequestUri!.AbsolutePath.StartsWith("/api/persona-avatar/", StringComparison.Ordinal))
            {
                await release.Task;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageFixtures.Png(64, 64)) };
            }

            return Station.Json(HttpStatusCode.OK, "{\"lines\":[]}");
        };
        StationKey KeyOf(int i) => StationKey.Of("s" + i, new Uri("http://s" + i + ".example/"));

        // One portrait per station, so only the portrait cap can refuse.
        var held = Enumerable.Range(0, StationCallLimiter.MaxPortraits).Select(i => s.Calls.GetAvatarAsync(Guid.NewGuid(), KeyOf(i), new Uri("http://s" + i + ".example/"), "dj_one", CancellationToken.None)).ToList();
        await WaitFor(() => s.Limiter.PortraitsInUse == StationCallLimiter.MaxPortraits);

        var next = await s.Calls.GetAvatarAsync(Guid.NewGuid(), KeyOf(9), new Uri("http://s9.example/"), "dj_one", CancellationToken.None);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, next.Refusal);
        Assert.True((await s.Calls.GetLyricsAsync(Alice, KeyOf(9), new Uri("http://s9.example/"), "song", CancellationToken.None)).Answered);

        release.SetResult();
        Assert.All(await Task.WhenAll(held), a => Assert.NotNull(a.Value));
        Assert.Equal(0, s.Limiter.PortraitsInUse);
        Assert.Equal(0, s.Limiter.InUse);
    }

    [Fact]
    public async Task A_busy_refusal_gives_the_budget_back()
    {
        var s = new Setup();
        var held = Enumerable.Range(0, StationCallLimiter.MaxPerStation).Select(_ => s.Limiter.TryEnter(KeyOne.Origin)).ToArray();

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, (await s.Calls.SubmitRequestAsync(Alice, KeyOne, One, "a", "", () => true, CancellationToken.None)).Refusal);
        }

        Array.ForEach(held, p => p!.Dispose());
        Assert.True((await s.Calls.SubmitRequestAsync(Alice, KeyOne, One, "a", "", () => true, CancellationToken.None)).Answered);
    }

    [Fact]
    public async Task The_permit_is_held_until_the_body_is_read()
    {
        var s = new Setup();
        var release = new TaskCompletionSource();
        s.Station.Answer = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new HeldContent(release) });

        var call = s.Calls.GetLikeAsync(Alice, KeyOne, One, null, CancellationToken.None);
        await WaitFor(() => s.Station.Seen.Count == 1);
        await Task.Delay(50);

        Assert.False(call.IsCompleted);
        Assert.Equal(1, s.Limiter.InUse);   // headers are in, the body is not: still held

        release.SetResult();
        Assert.True((await call).Value!.Body!.Liked);
        Assert.Equal(0, s.Limiter.InUse);
    }

    [Fact]
    public async Task The_caller_s_cancellation_propagates_and_gives_the_permit_back()
    {
        var s = new Setup();
        s.Station.Answer = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Station.Json(HttpStatusCode.OK, "{}");
        };
        using var cancel = new CancellationTokenSource();

        var call = s.Calls.GetLikeAsync(Alice, KeyOne, One, null, cancel.Token);
        await WaitFor(() => s.Limiter.InUse == 1);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(0, s.Limiter.InUse);
    }

    [Fact]
    public async Task Any_other_exception_propagates_and_gives_the_permit_back()
    {
        var s = new Setup();
        s.Station.Answer = (_, _) => throw new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Calls.GetLikeAsync(Alice, KeyOne, One, null, CancellationToken.None));
        Assert.Equal(0, s.Limiter.InUse);
    }

    [Fact]
    public async Task The_routes_answer_429_and_503_with_retry_after_and_no_body()
    {
        var h = new MultiStationHarness();
        var budgets = new UserBudgets(new UserBudgetsTests.ManualClock());
        var limiter = new StationCallLimiter();
        var calls = new StationCalls(h.Client, budgets, limiter, NullLogger<StationCalls>.Instance, TimeProvider.System);
        for (var i = 0; i < UserBudgets.CallsPerMinute; i++)
        {
            Assert.NotNull(budgets.TryReserve(WebRoute.Listener, StationCallKind.Lyrics, out _));
        }

        foreach (var action in new[] { nameof(JellywaveWebController.LikeStatus), nameof(JellywaveWebController.Lyrics), nameof(JellywaveWebController.Avatar), nameof(JellywaveWebController.Like), nameof(JellywaveWebController.SubmitRequest) })
        {
            var controller = h.Controller(calls: calls);
            var result = await WebRoute.RunAsync(controller, action, ArgsOf(action));
            Assert.Equal(StatusCodes.Status429TooManyRequests, WebRoute.StatusOf(result));
            Assert.Equal("60", controller.Response.Headers.RetryAfter.ToString());
        }

        Assert.Empty(h.Seen);
        Assert.Equal(0, h.Receipts.Count);   // the request's reserved slot was given back

        var busy = new StationCalls(h.Client, new UserBudgets(), limiter, NullLogger<StationCalls>.Instance, TimeProvider.System);
        var full = Enumerable.Range(0, StationCallLimiter.MaxPerStation).Select(_ => limiter.TryEnter(MultiStationHarness.KeyOf("two", MultiStationHarness.HostTwo).Origin)).ToList();
        var busyController = h.Controller(calls: busy);
        var refused = await WebRoute.RunAsync(busyController, nameof(JellywaveWebController.LikeStatus), ("stationId", "two"));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, WebRoute.StatusOf(refused));
        Assert.Equal("2", busyController.Response.Headers.RetryAfter.ToString());
        Assert.Empty(h.Seen);
        full.ForEach(p => p!.Dispose());
    }

    private static (string Name, object? Value)[] ArgsOf(string action) => action switch
    {
        nameof(JellywaveWebController.Like) => [("submission", new LikeSubmission { SongId = "song-x", StationId = "two" })],
        nameof(JellywaveWebController.SubmitRequest) => [("submission", new RequestSubmission { Text = "something", StationId = "two" })],
        _ => [("stationId", "two"), ("songId", "song-x"), ("personaId", "dj_x")]
    };

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
