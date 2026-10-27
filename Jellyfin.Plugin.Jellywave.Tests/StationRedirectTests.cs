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
/// Card 14: a 3xx from the station is its answer, never a hop. Every call answers 302 to another host with a body
/// that would look like success if it were read; the stub records every request, so a followed redirect would show
/// as a second one. That the real handler does not follow is proven in <see cref="StationHttpClientRegistrationTests"/>.
/// </summary>
public class StationRedirectTests
{
    private const string Elsewhere = "http://elsewhere.example:7700/";
    private static readonly Uri Base = new("http://station.example/");
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private sealed class RedirectingStation : HttpMessageHandler
    {
        public List<string> Seen { get; } = new();

        public string? Target { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request.Method + " " + request.RequestUri);
            var path = request.RequestUri!.AbsolutePath;
            var body = path switch
            {
                "/api/like" => "{\"enabled\":true,\"liked\":true,\"count\":99,\"songId\":\"song-1\"}",
                "/api/request" => "{\"success\":true,\"requestId\":\"req_x\",\"status\":\"pending\"}",
                "/api/request/req_x" => "{\"status\":\"done\",\"outcome\":\"queued\",\"ack\":\"Coming up\"}",
                "/api/lyrics/current" => "{\"lines\":[{\"text\":\"la la\"}],\"synced\":false}",
                _ => "{\"station\":{\"name\":\"Elsewhere\"}}"
            };
            var response = new HttpResponseMessage(HttpStatusCode.Found)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            response.Headers.Location = Target is null ? new Uri(new Uri(Elsewhere), path.TrimStart('/')) : new Uri(Target);
            return Task.FromResult(response);
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubFactory(HttpMessageHandler handler) => _handler = handler;

        public List<string> Names { get; } = new();

        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return new HttpClient(_handler, disposeHandler: false);
        }
    }

    private sealed class Harness
    {
        public Harness()
        {
            Factory = new StubFactory(Station);
            Client = new StationClient(Factory);
        }

        public RedirectingStation Station { get; } = new();

        public string? Target
        {
            get => Station.Target;
            init => Station.Target = value;
        }

        public StubFactory Factory { get; }

        public StationClient Client { get; }

        public RequestReceipts Receipts { get; } = new();

        public JellywaveWebController Web()
        {
            var http = new DefaultHttpContext();
            http.Items[StationAccess.UserIdItem] = Alice;
            return new JellywaveWebController(
                new NowPlayingState(),
                new LiveItemFinder(null!),
                null!,
                Client,
                new ScheduleCache(Client),
                Receipts,
                NullLogger<JellywaveWebController>.Instance,
                () => new PluginConfiguration { StationBaseUrl = "http://station.example" })
            {
                ControllerContext = new ControllerContext { HttpContext = http }
            };
        }

        // The probe touches only the station client and the logger.
        public JellywaveController Admin()
            => new(Client, null!, new NowPlayingState(), new PollerHealth(), null!, new LiveItemFinder(null!), NullLogger<JellywaveController>.Instance);
    }

    [Fact]
    public async Task Json_get_throws_like_any_other_non_success_and_keeps_the_location()
    {
        var h = new Harness();
        var ex = await Assert.ThrowsAsync<StationRedirectException>(() => h.Client.GetStateAsync(Base, CancellationToken.None));
        Assert.IsAssignableFrom<HttpRequestException>(ex);
        Assert.Equal(HttpStatusCode.Found, ex.StatusCode);
        Assert.Equal(new Uri(Elsewhere + "api/state"), ex.Location);
        Assert.DoesNotContain("elsewhere", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "GET http://station.example/api/state" }, h.Station.Seen);
    }

    [Fact]
    public async Task Image_get_is_no_art()
    {
        var h = new Harness();
        Assert.Null(await h.Client.GetCoverAsync(Base, "abc", CancellationToken.None));
        Assert.Null(await h.Client.GetAvatarAsync(Base, "dj_one", CancellationToken.None));
        Assert.Equal(2, h.Station.Seen.Count);
    }

    [Fact]
    public async Task Every_call_uses_the_plugin_client()
    {
        var h = new Harness();
        await Assert.ThrowsAsync<StationRedirectException>(() => h.Client.GetNowPlayingAsync(Base, CancellationToken.None));
        await Assert.ThrowsAsync<StationRedirectException>(() => h.Client.GetScheduleAsync(Base, CancellationToken.None));
        await h.Client.GetCoverAsync(Base, "abc", CancellationToken.None);
        await h.Client.GetLikeAsync(Base, CancellationToken.None);
        await h.Client.PostLikeAsync(Base, "song-1", CancellationToken.None);
        await h.Client.GetLyricsAsync(Base, "song-1", CancellationToken.None);
        await h.Client.SubmitRequestAsync(Base, "text", "name", CancellationToken.None);
        await h.Client.GetRequestStatusAsync(Base, "req_x", CancellationToken.None);
        Assert.Equal(8, h.Factory.Names.Count);
        Assert.All(h.Factory.Names, name => Assert.Equal(StationClient.HttpClientName, name));
    }

    [Fact]
    public async Task A_redirect_reply_carries_no_body()
    {
        var h = new Harness();
        var reply = await h.Client.PostLikeAsync(Base, "song-1", CancellationToken.None);
        Assert.Equal(StatusCodes.Status302Found, reply.StatusCode);
        Assert.Null(reply.Body);
    }

    [Fact]
    public async Task Request_post_is_not_reported_as_accepted_and_records_no_receipt()
    {
        var h = new Harness();
        var outcome = (await h.Web().SubmitRequest(new RequestSubmission { Text = "something with horns", Name = "A" }, CancellationToken.None)).Value!;
        Assert.False(outcome.Accepted);
        Assert.Null(outcome.RequestId);
        Assert.NotEqual("pending", outcome.Status);
        Assert.Equal(ReceiptOwnership.Unknown, h.Receipts.Check("req_x", Alice));
        Assert.Equal(new[] { "POST http://station.example/api/request" }, h.Station.Seen);
    }

    [Fact]
    public async Task Like_post_is_a_refusal_not_a_like()
    {
        var h = new Harness();
        var outcome = (await h.Web().Like(new LikeSubmission { SongId = "song-1" }, CancellationToken.None)).Value!;
        Assert.False(outcome.Liked);
        Assert.Equal(0, outcome.Count);
        Assert.Null(outcome.SongId);
        Assert.False(string.IsNullOrEmpty(outcome.Message));
    }

    [Fact]
    public async Task Like_status_shows_no_heart()
    {
        var h = new Harness();
        var outcome = (await h.Web().LikeStatus(CancellationToken.None)).Value!;
        Assert.False(outcome.Liked);
        Assert.Null(outcome.SongId);
    }

    [Fact]
    public async Task Lyrics_are_unavailable_not_ok()
    {
        var h = new Harness();
        var outcome = (await h.Web().Lyrics("song-1", CancellationToken.None)).Value!;
        Assert.Equal("unavailable", outcome.Status);
        Assert.True(outcome.Lines is null || outcome.Lines.Count == 0);
    }

    [Fact]
    public async Task Request_status_is_unavailable_and_the_body_is_not_shown()
    {
        var h = new Harness();
        h.Receipts.Record("req_x", Alice);
        var outcome = (await h.Web().RequestStatus("req_x", CancellationToken.None)).Value!;
        Assert.Equal("unavailable", outcome.Status);
        Assert.Null(outcome.Ack);
        Assert.Equal(new[] { "GET http://station.example/api/request/req_x" }, h.Station.Seen);
    }

    [Fact]
    public async Task Admin_probe_names_the_address_to_use_instead()
    {
        var h = new Harness();
        var result = (await h.Admin().ProbeStation("http://station.example", CancellationToken.None)).Value!;
        Assert.False(result.Ok);
        Assert.Null(result.Name);
        Assert.Equal("the station redirected to " + Elsewhere + "; use that address instead", result.Error);
        Assert.Single(h.Station.Seen);
    }

    [Theory]
    [InlineData("http://new.example:7700/api/state", "http://new.example:7700/")]
    [InlineData("https://new.example/radio/api/now-playing?x=1", "https://new.example/radio/")]
    [InlineData("https://new.example/", "https://new.example/")]
    [InlineData("http://api/somewhere", "http://api/somewhere/")]
    public void Suggested_base_drops_the_api_path(string location, string expected)
        => Assert.Equal(expected, JellywaveController.SuggestedBase(new Uri(location)));

    [Theory]
    [InlineData("ftp://x/y")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://u:p@evil.example/")]
    [InlineData("http://u:p@evil.example/api/state")]
    [InlineData("http://@evil.example/api/state")]
    public void Suggested_base_refuses_what_the_base_url_rules_refuse(string location)
        => Assert.Null(JellywaveController.SuggestedBase(new Uri(location)));

    [Fact]
    public async Task Admin_probe_does_not_echo_an_unusable_redirect_target()
    {
        var h = new Harness { Target = "http://user:secret@evil.example/api/state" };
        var result = (await h.Admin().ProbeStation("http://station.example", CancellationToken.None)).Value!;
        Assert.False(result.Ok);
        Assert.Equal("the station answered HTTP 302 (a redirect) to an address the plugin does not accept", result.Error);
        Assert.DoesNotContain("secret", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://user:pw@station.example")]
    [InlineData("http://station.example/?x=1")]
    [InlineData("http://station.example/#f")]
    public async Task Admin_probe_refuses_a_base_url_with_credentials_query_or_fragment(string baseUrl)
    {
        var h = new Harness();
        var result = await h.Admin().ProbeStation(baseUrl, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(h.Station.Seen);
    }
}
