using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Drawing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L5, through ASP.NET Core's real MVC pipeline on a loopback socket (not a direct method call): the channel gate
/// (<see cref="StationAccessAttribute"/>) decides before model binding and before the station route filter, so a refused
/// caller gets 403 whatever station id or body it sends, and its body is never read; only an allowed caller's station id
/// is resolved, from the query (GET) or the JSON body (POST), and an unlisted one is 404 with no station asked.
/// </summary>
public sealed class StationRoutePipelineTests : IAsyncLifetime, IDisposable
{
    private const string TestScheme = "Test";
    private const string UserHeader = "X-Test-User";
    private const string ApiKeyHeader = "X-Test-ApiKey";

    private readonly MultiStationHarness _h = new();
    private readonly User _alice = Listener("alice", allChannels: true);
    private readonly User _bob = Listener("bob", allChannels: true);
    private readonly User _hidden = Listener("hidden", allChannels: false);
    private readonly User _oldBlocked = Listener("oldBlocked", allChannels: true);   // blocked from an earlier tile name (L1)
    private readonly User _unknown = Listener("unknown", allChannels: true);   // not in the user store
    private WebApplication? _app;
    private HttpClient? _http;

    public async Task InitializeAsync()
    {
        var store = new FakeStore();
        foreach (var user in new[] { _alice, _bob, _hidden, _oldBlocked })
        {
            store.Users[user.Id] = user;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var services = builder.Services;
        services.AddAuthentication(TestScheme).AddScheme<AuthenticationSchemeOptions, TestAuthentication>(TestScheme, null);
        services.AddAuthorization(o => o.AddPolicy(Policies.RequiresElevation, p => p.RequireAssertion(_ => false)));
        services.AddControllers()
            .ConfigureApplicationPartManager(m =>
            {
                m.ApplicationParts.Add(new AssemblyPart(typeof(JellywaveWebController).Assembly));
                m.FeatureProviders.Add(new OnlyStationPageController());
            });
        services.AddSingleton(new StationAccess(store, () => ChannelResolution.Found(_h.Channel), user => user.Id == _oldBlocked.Id, NullLogger<StationAccess>.Instance));
        services.AddSingleton(new StationResolver(_h.Receipts, () => _h.Config, NullLogger<StationResolver>.Instance));
        services.AddSingleton(new StationDemand(new FakePlayingItems()));
        services.AddSingleton(_h.State);
        services.AddSingleton(_h.Artwork);
        services.AddSingleton(new LiveItemFinder(_h.Library.Manager, () => [], NullLogger<LiveItemFinder>.Instance));
        services.AddSingleton(DispatchProxy.Create<IImageProcessor, FeedStationSwitchTests.TagProcessor>());
        services.AddSingleton(_h.Client);
        services.AddSingleton(_h.Schedule);
        services.AddSingleton(_h.Receipts);
        services.AddSingleton<StationList>();
        services.AddSingleton(_h.Titles);
        services.AddSingleton(new StationCalls(_h.Client, NullLogger<StationCalls>.Instance));

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapControllers();
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public void Dispose() => _http?.Dispose();

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    public static IEnumerable<object[]> GetRoutes()
        => new[] { "Jellywave/Feed", "Jellywave/Like", "Jellywave/Schedule", "Jellywave/Lyrics?songId=song-x", "Jellywave/Avatar/dj_x", "Jellywave/Stations" }
            .Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(GetRoutes))]
    public async Task A_refused_caller_is_403_whatever_station_id_it_sends(string route)
    {
        // API key, unknown user, blocked from an earlier tile name, not visible: the channel gate answers first.
        foreach (var stationId in new[] { null, "two", "nope", "plain" })
        {
            var url = stationId is null ? route : route + (route.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "stationId=" + stationId;
            Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(url, _alice, apiKey: true));
            Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(url, _unknown));
            Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(url, _oldBlocked));
            Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync(url, _hidden));
        }

        Assert.Empty(_h.Seen);
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync(route, _alice));   // control
    }

    [Theory]
    [InlineData("Jellywave/Like")]
    [InlineData("Jellywave/Request")]
    public async Task A_refused_caller_s_body_is_never_read(string route)
    {
        // A body that cannot be bound would be 400 if model binding ran; the gate answers first.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(route, "{not json", _alice, apiKey: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(route, "{not json", _hidden)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(route, "{not json", _oldBlocked)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(route, "{not json", _unknown)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(route, "{not json", _alice)).StatusCode);   // control
        Assert.Empty(_h.Seen);
    }

    [Theory]
    [MemberData(nameof(MultiStationHarness.UnlistedIds), MemberType = typeof(MultiStationHarness))]
    public async Task An_allowed_caller_naming_an_unlisted_station_gets_404_and_no_station_is_asked(string stationId)
    {
        var id = Uri.EscapeDataString(stationId);
        foreach (var route in new[] { "Jellywave/Feed?stationId=", "Jellywave/Like?stationId=", "Jellywave/Schedule?stationId=", "Jellywave/Lyrics?songId=song-x&stationId=", "Jellywave/Avatar/dj_x?stationId=" })
        {
            var status = await GetStatusAsync(route + id, _alice);

            // An empty or blank query value binds as absent (ASP.NET Core's ConvertEmptyStringToNull), so it is the primary
            // station, like an old page's request without the parameter. In a JSON body it stays text and is 404.
            Assert.Equal(string.IsNullOrWhiteSpace(stationId) ? HttpStatusCode.OK : HttpStatusCode.NotFound, status);
        }

        var body = "{\"stationId\":" + JsonString(stationId) + ",\"songId\":\"song-x\",\"text\":\"hi\"}";
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("Jellywave/Like", body, _alice)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("Jellywave/Request", body, _alice)).StatusCode);
        Assert.All(_h.Seen, s => Assert.StartsWith(MultiStationHarness.HostLive + " ", s, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(stationId))
        {
            Assert.Empty(_h.Seen);
        }
    }

    [Fact]
    public async Task An_allowed_caller_reaches_the_station_it_names_from_the_query_or_the_body()
    {
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync("Jellywave/Like?stationId=two", _alice));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("Jellywave/Like", "{\"StationId\":\"two\",\"SongId\":\"song-two\"}", _alice)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("Jellywave/Like", "{\"stationId\":\"two\",\"songId\":\"song-two\"}", _alice)).StatusCode);
        Assert.Equal(3, _h.Seen.Count);
        Assert.All(_h.Seen, s => Assert.StartsWith(MultiStationHarness.HostTwo + " ", s, StringComparison.Ordinal));

        _h.Station.Seen.Clear();
        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync("Jellywave/Like", _alice));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("Jellywave/Like", "{\"SongId\":\"song-live\"}", _alice)).StatusCode);
        Assert.Equal(2, _h.Seen.Count);
        Assert.All(_h.Seen, s => Assert.StartsWith(MultiStationHarness.HostLive + " ", s, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_query_station_id_on_a_post_route_is_ignored()
    {
        // POST routes read the body only; a station id in the query is not bound at all.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("Jellywave/Like?stationId=two", "{\"SongId\":\"song-live\"}", _alice)).StatusCode);
        Assert.All(_h.Seen, s => Assert.StartsWith(MultiStationHarness.HostLive + " ", s, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_request_is_polled_at_its_own_station_and_by_its_owner_only()
    {
        var posted = await PostAsync("Jellywave/Request", "{\"StationId\":\"two\",\"Text\":\"hi\",\"Name\":\"A\"}", _alice);
        var body = await posted.Content.ReadAsStringAsync();

        // Card R2a: the answer carries our receipt token, never the station's request id.
        Assert.DoesNotContain("req-station-two", body, StringComparison.Ordinal);
        var token = System.Text.Json.JsonDocument.Parse(body).RootElement.EnumerateObject().Single(p => string.Equals(p.Name, "RequestId", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
        Assert.Equal(RequestReceipts.TokenLength, token.Length);
        _h.Station.Seen.Clear();

        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync("Jellywave/Request/req-station-two", _alice));   // the station's id is no receipt
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync("Jellywave/Request/" + token, _bob));
        Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync("Jellywave/Request/" + token, _alice, apiKey: true));
        Assert.Empty(_h.Seen);

        Assert.Equal(HttpStatusCode.OK, await GetStatusAsync("Jellywave/Request/" + token + "?stationId=live", _alice));
        Assert.Equal(new[] { MultiStationHarness.HostTwo + " GET /api/request/req-station-two" }, _h.Seen);

        _h.Station.Seen.Clear();
        _h.Config.Stations[1].Enabled = false;
        var lost = await _http!.SendAsync(Request(HttpMethod.Get, "Jellywave/Request/" + token, _alice));
        Assert.Equal(HttpStatusCode.OK, lost.StatusCode);
        Assert.Contains("unknown", await lost.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(_h.Seen);
    }

    [Fact]
    public async Task The_station_list_reaches_no_station_and_carries_no_url()
    {
        var response = await _http!.SendAsync(Request(HttpMethod.Get, "Jellywave/Stations", _alice));
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"two\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("http", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".example", json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_h.Seen);
    }

    [Fact]
    public async Task The_title_route_serves_listed_plain_streams_only_and_behind_the_gate()
    {
        // Card L6: through the real pipeline, the gate answers first (403 whatever the id), then the plain-stream filter: a
        // listed plain stream is 200, a SUB/WAVE, disabled, refused, unknown or missing id is 404. Nothing is asked of any
        // station or stream, and the answer carries no URL.
        _h.Titles.Record("plain", StationTitles.StateTitle, "Artist - Title", DateTime.UtcNow);
        foreach (var stationId in new[] { "plain", "live", "nope" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync("Jellywave/Title?stationId=" + stationId, _alice, apiKey: true));
            Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync("Jellywave/Title?stationId=" + stationId, _hidden));
            Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync("Jellywave/Title?stationId=" + stationId, _oldBlocked));
            Assert.Equal(HttpStatusCode.Forbidden, await GetStatusAsync("Jellywave/Title?stationId=" + stationId, _unknown));
        }

        var response = await _http!.SendAsync(Request(HttpMethod.Get, "Jellywave/Title?stationId=plain", _alice));
        var json = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"Artist - Title\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Title\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("http", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".example", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);

        foreach (var stationId in new[] { "live", "two", "off", "bad", "nope", "PLAIN", "plain ", string.Empty })
        {
            Assert.Equal(HttpStatusCode.NotFound, await GetStatusAsync("Jellywave/Title?stationId=" + Uri.EscapeDataString(stationId), _alice));
        }

        Assert.Equal(HttpStatusCode.NotFound, await GetStatusAsync("Jellywave/Title", _alice));
        Assert.Empty(_h.Seen);
    }

    private static string JsonString(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static User Listener(string name, bool allChannels)
    {
        var u = new User(name, "Default", "Default");
        u.SetPermission(PermissionKind.EnableAllChannels, allChannels);
        return u;
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, User user, bool apiKey = false)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(UserHeader, user.Id.ToString("N", CultureInfo.InvariantCulture));
        request.Headers.Add(ApiKeyHeader, apiKey ? "True" : "False");
        return request;
    }

    private async Task<HttpStatusCode> GetStatusAsync(string url, User user, bool apiKey = false)
        => (await _http!.SendAsync(Request(HttpMethod.Get, url, user, apiKey))).StatusCode;

    private Task<HttpResponseMessage> PostAsync(string url, string body, User user, bool apiKey = false)
    {
        var request = Request(HttpMethod.Post, url, user, apiKey);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return _http!.SendAsync(request);
    }

    private sealed class FakeStore : IUserStore
    {
        public Dictionary<Guid, User> Users { get; } = new();

        public IEnumerable<Guid> UserIds() => Users.Keys.ToArray();

        public User? GetUser(Guid id) => Users.TryGetValue(id, out var u) ? u : null;

        public Task SaveAsync(User user) => throw new InvalidOperationException("station routes never write users");
    }

    /// <summary>Signs the caller in from two test headers, with the claims Jellyfin's own handler sets.</summary>
    private sealed class TestAuthentication : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [new Claim(StationAccess.UserIdClaim, user.ToString()), new Claim(StationAccess.ApiKeyClaim, Request.Headers[ApiKeyHeader].ToString())],
                TestScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }

    /// <summary>Only the station page's controller (the admin controller's services are not part of this host).</summary>
    private sealed class OnlyStationPageController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            foreach (var other in feature.Controllers.Where(c => c.AsType() != typeof(JellywaveWebController)).ToList())
            {
                feature.Controllers.Remove(other);
            }
        }
    }
}
