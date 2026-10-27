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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// The request routes wired to <see cref="RequestReceipts"/>: a receipt is recorded on submit, only its owner's
/// polls reach the station, and an id never issued here is answered without a station call. The caller's id is
/// put where <see cref="StationAccessAttribute"/> leaves it.
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
        }

        public StationStub Station { get; } = new();

        public RequestReceipts Receipts { get; } = new();

        // Only the request routes run here; the finder, image processor and schedule are never touched by them.
        public JellywaveWebController As(Guid userId)
        {
            var http = new DefaultHttpContext();
            http.Items[StationAccess.UserIdItem] = userId;
            return new JellywaveWebController(
                new NowPlayingState(),
                new LiveItemFinder(null!),
                null!,
                _client,
                new ScheduleCache(_client),
                Receipts,
                NullLogger<JellywaveWebController>.Instance,
                () => new PluginConfiguration { StationBaseUrl = "http://station.example" })
            {
                ControllerContext = new ControllerContext { HttpContext = http }
            };
        }

        public async Task<RequestOutcome> SubmitAsync(Guid userId)
        {
            var result = await As(userId).SubmitRequest(new RequestSubmission { Text = "something with horns", Name = "A" }, CancellationToken.None);
            return result.Value!;
        }
    }

    [Fact]
    public async Task Another_user_polling_the_request_gets_403_and_the_station_is_not_asked()
    {
        var harness = new Harness();
        var submitted = await harness.SubmitAsync(Alice);
        Assert.True(submitted.Accepted);
        Assert.Equal(RequestId, submitted.RequestId);

        var polled = await harness.As(Bob).RequestStatus(RequestId, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(polled.Result).StatusCode);
        Assert.Equal(new[] { "POST /api/request" }, harness.Station.Seen);
    }

    [Fact]
    public async Task The_owner_polling_the_request_reaches_the_station()
    {
        var harness = new Harness();
        await harness.SubmitAsync(Alice);

        var polled = await harness.As(Alice).RequestStatus(RequestId, CancellationToken.None);
        Assert.Null(polled.Result);
        Assert.Equal("done", polled.Value!.Status);
        Assert.Equal("Coming up", polled.Value.Ack);
        Assert.Equal(2, polled.Value.QueuePosition);
        Assert.Equal(new[] { "POST /api/request", "GET /api/request/" + RequestId }, harness.Station.Seen);
    }

    [Fact]
    public async Task An_id_never_issued_here_is_unknown_without_a_station_call()
    {
        var harness = new Harness();

        var polled = await harness.As(Alice).RequestStatus(RequestId, CancellationToken.None);
        Assert.Null(polled.Result);
        Assert.Equal("unknown", polled.Value!.Status);
        Assert.Equal(RequestId, polled.Value.RequestId);
        Assert.Empty(harness.Station.Seen);
    }

    [Fact]
    public async Task Submitting_records_the_receipt_for_the_caller()
    {
        var harness = new Harness();
        await harness.SubmitAsync(Alice);
        Assert.Equal(ReceiptOwnership.Owner, harness.Receipts.Check(RequestId, Alice));
        Assert.Equal(ReceiptOwnership.OtherUser, harness.Receipts.Check(RequestId, Bob));
    }
}
