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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// The request routes wired to <see cref="RequestReceipts"/>: a receipt is recorded on submit, only its owner's
/// polls reach the station, and an id never issued here is answered without a station call. The caller's id is
/// put where <see cref="StationAccessAttribute"/> leaves it. Card R2a: the page gets and polls with our own receipt token;
/// the station's id stays on the server; a full store refuses before the station is asked.
/// </summary>
public class RequestRouteTests
{
    private const string RequestId = "req_a1b2c3";
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private sealed class StationStub : HttpMessageHandler
    {
        public List<string> Seen { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Seen.Add(request.Method + " " + path);
            var response = request.Method == HttpMethod.Post && path == "/api/request"
                ? Json(HttpStatusCode.Accepted, "{\"success\":true,\"requestId\":\"" + RequestId + "\",\"status\":\"pending\"}")
                : path == "/api/request/" + RequestId
                    ? Json(HttpStatusCode.OK, "{\"status\":\"done\",\"outcome\":\"queued\",\"ack\":\"Coming up\",\"queuePosition\":2}")
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class Harness
    {
        private readonly StationClient _client;

        public Harness()
        {
            _client = new StationClient(new StubFactory(Station));
            Calls = new StationCalls(_client, Budgets, new StationCallLimiter(), NullLogger<StationCalls>.Instance, TimeProvider.System);
        }

        /// <summary>One budget and cache set for every call of the test, as the singleton is in the server.</summary>
        public UserBudgets Budgets { get; } = new();

        public StationCalls Calls { get; }

        public StationStub Station { get; } = new();

        public RequestReceipts Receipts { get; } = new();

        public PluginConfiguration Config { get; } = new() { Stations = { new StationDefinition { Id = "live", BaseUrl = "http://station.example" } } };

        // Only the request routes run here; the finder, image processor and schedule are never touched by them.
        public JellywaveWebController As(Guid userId) => WebRoute.Controller(() => Config, client: _client, receipts: Receipts, user: userId, calls: Calls);

        public async Task<RequestOutcome> SubmitAsync(Guid userId)
            => WebRoute.Value<RequestOutcome>(await WebRoute.RunAsync(As(userId), nameof(JellywaveWebController.SubmitRequest), ("submission", new RequestSubmission { Text = "something with horns", Name = "A" })));

        public Task<IActionResult> PollAsync(Guid userId, string requestId)
            => WebRoute.RunAsync(As(userId), nameof(JellywaveWebController.RequestStatus), ("requestId", requestId));
    }

    [Fact]
    public async Task Another_user_polling_the_request_gets_403_and_the_station_is_not_asked()
    {
        var harness = new Harness();
        var submitted = await harness.SubmitAsync(Alice);
        Assert.True(submitted.Accepted);
        Assert.NotEqual(RequestId, submitted.RequestId);

        var polled = await harness.PollAsync(Bob, submitted.RequestId!);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(polled).StatusCode);
        Assert.Equal(new[] { "POST /api/request" }, harness.Station.Seen);
    }

    [Fact]
    public async Task The_owner_polling_the_request_reaches_the_station()
    {
        var harness = new Harness();
        var token = (await harness.SubmitAsync(Alice)).RequestId!;

        var polled = WebRoute.Value<RequestOutcome>(await harness.PollAsync(Alice, token));
        Assert.Equal(token, polled.RequestId);
        Assert.Equal("done", polled.Status);
        Assert.Equal("Coming up", polled.Ack);
        Assert.Equal(2, polled.QueuePosition);
        Assert.Equal(new[] { "POST /api/request", "GET /api/request/" + RequestId }, harness.Station.Seen);
    }

    [Fact]
    public async Task An_id_never_issued_here_is_unknown_without_a_station_call()
    {
        var harness = new Harness();

        var polled = WebRoute.Value<RequestOutcome>(await harness.PollAsync(Alice, RequestId));
        Assert.Equal("unknown", polled.Status);
        Assert.Equal(RequestId, polled.RequestId);
        Assert.Empty(harness.Station.Seen);
    }

    [Fact]
    public async Task Submitting_records_the_receipt_for_the_caller()
    {
        var harness = new Harness();
        var token = (await harness.SubmitAsync(Alice)).RequestId!;
        Assert.Equal(ReceiptOwnership.Owner, harness.Receipts.Check(token, Alice, out var station));
        Assert.Equal(StationKey.Of("live", new Uri("http://station.example/")), station);
        Assert.Equal(RequestId, harness.Receipts.StationRequestIdOf(token, Alice, station!));
        Assert.Equal(ReceiptOwnership.OtherUser, harness.Receipts.Check(token, Bob, out var other));
        Assert.Null(other);
        Assert.Equal(ReceiptOwnership.Unknown, harness.Receipts.Check(RequestId, Alice, out _));
    }

    [Fact]
    public async Task A_full_receipt_store_refuses_a_user_who_owns_none_before_the_station_is_asked()
    {
        var harness = new Harness();
        var key = StationKey.Of("live", new Uri("http://station.example/"));
        for (var u = 0; u < RequestReceipts.MaxEntries / RequestReceipts.MaxPerUser; u++)
        {
            var owner = Guid.NewGuid();
            for (var i = 0; i < RequestReceipts.MaxPerUser; i++)
            {
                Assert.NotNull(harness.Receipts.Record(owner, key, "other"));
            }
        }

        var controller = harness.As(Alice);
        var refused = await WebRoute.RunAsync(controller, nameof(JellywaveWebController.SubmitRequest), ("submission", new RequestSubmission { Text = "something with horns" }));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, WebRoute.StatusOf(refused));
        Assert.Equal("60", controller.Response.Headers.RetryAfter.ToString());
        Assert.Empty(harness.Station.Seen);
        Assert.Equal(RequestReceipts.MaxEntries, harness.Receipts.Count);
    }

    [Fact]
    public async Task An_over_budget_send_is_429_and_leaves_the_caller_s_own_receipts_alone()
    {
        // Fix round (RA): the budget is checked before a receipt slot is reserved, so an over-budget send never evicts the
        // caller's own oldest accepted receipt.
        var harness = new Harness();
        var key = StationKey.Of("live", new Uri("http://station.example/"));
        var own = Enumerable.Range(0, RequestReceipts.MaxPerUser).Select(i => harness.Receipts.Record(Alice, key, "old" + i)!).ToList();
        Assert.NotNull(harness.Budgets.TryReserve(Alice, StationCallKind.Request, out _));

        var controller = harness.As(Alice);
        var refused = await WebRoute.RunAsync(controller, nameof(JellywaveWebController.SubmitRequest), ("submission", new RequestSubmission { Text = "something" }));

        Assert.Equal(StatusCodes.Status429TooManyRequests, WebRoute.StatusOf(refused));
        Assert.Equal("60", controller.Response.Headers.RetryAfter.ToString());
        Assert.All(own, t => Assert.Equal(ReceiptOwnership.Owner, harness.Receipts.Check(t, Alice, out _)));
        Assert.Equal(RequestReceipts.MaxPerUser, harness.Receipts.Count);
        Assert.Empty(harness.Station.Seen);
    }

    [Fact]
    public async Task A_full_store_shows_an_over_budget_caller_the_true_429_and_a_caller_within_budget_503_with_the_budget_back()
    {
        var harness = new Harness();
        FillStore(harness);

        Assert.NotNull(harness.Budgets.TryReserve(Bob, StationCallKind.Request, out _));
        var bob = await WebRoute.RunAsync(harness.As(Bob), nameof(JellywaveWebController.SubmitRequest), ("submission", new RequestSubmission { Text = "something" }));
        Assert.Equal(StatusCodes.Status429TooManyRequests, WebRoute.StatusOf(bob));

        var alice = await WebRoute.RunAsync(harness.As(Alice), nameof(JellywaveWebController.SubmitRequest), ("submission", new RequestSubmission { Text = "something" }));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, WebRoute.StatusOf(alice));
        Assert.NotNull(harness.Budgets.TryReserve(Alice, StationCallKind.Request, out _));   // nothing was sent: the budget is back
        Assert.Empty(harness.Station.Seen);
    }

    private static void FillStore(Harness harness)
    {
        var key = StationKey.Of("live", new Uri("http://station.example/"));
        for (var u = 0; u < RequestReceipts.MaxEntries / RequestReceipts.MaxPerUser; u++)
        {
            var owner = Guid.NewGuid();
            for (var i = 0; i < RequestReceipts.MaxPerUser; i++)
            {
                Assert.NotNull(harness.Receipts.Record(owner, key, "other"));
            }
        }
    }

    [Fact]
    public async Task A_refused_or_failed_request_gives_its_reserved_slot_back()
    {
        var harness = new Harness();
        harness.Config.Stations[0].BaseUrl = "http://station.example/refuses";
        var outcome = await harness.SubmitAsync(Alice);

        Assert.False(outcome.Accepted);
        Assert.Null(outcome.RequestId);
        Assert.Equal(0, harness.Receipts.Count);
    }
}
