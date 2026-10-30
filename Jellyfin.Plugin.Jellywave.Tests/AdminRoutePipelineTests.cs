using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Channels;
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
/// Card L13 E, through ASP.NET Core's real MVC pipeline on a loopback socket: the admin controller's class-level
/// <c>RequiresElevation</c> policy covers the new <c>GET /Jellywave/Viewers</c> (user names) like every other admin
/// route. A signed-in non-admin gets 403 and the user store is never read; an admin gets the count (control). The
/// policy here stands in for Jellyfin's: it admits a caller with the administrator role claim.
/// </summary>
public sealed class AdminRoutePipelineTests : IAsyncLifetime, IDisposable
{
    private const string TestScheme = "Test";
    private const string RoleHeader = "X-Test-Role";
    private const string AdminRole = "Administrator";

    private readonly CountingStore _store = new();
    private WebApplication? _app;
    private HttpClient? _http;

    public async Task InitializeAsync()
    {
        var hidden = new User("testB", "Default", "Default");
        hidden.SetPermission(PermissionKind.EnableAllChannels, true);
        hidden.SetPermission(PermissionKind.EnableAudioPlaybackTranscoding, false);
        _store.Users[hidden.Id] = hidden;
        var config = new PluginConfiguration();
        var station = new StationClient(null!);   // the routes reached here never call a station

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var services = builder.Services;
        services.AddAuthentication(TestScheme).AddScheme<AuthenticationSchemeOptions, TestAuthentication>(TestScheme, null);
        services.AddAuthorization(o => o.AddPolicy(Policies.RequiresElevation, p => p.RequireRole(AdminRole)));
        services.AddControllers()
            .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = null)   // Jellyfin answers in PascalCase
            .ConfigureApplicationPartManager(m =>
            {
                m.ApplicationParts.Add(new AssemblyPart(typeof(JellywaveController).Assembly));
                m.FeatureProviders.Add(new OnlyAdminController());
            });
        services.AddSingleton(station);
        services.AddSingleton(new AccessGranter(_store, new FoundLocator(), NullLogger<AccessGranter>.Instance, () => config));
        services.AddSingleton(new NowPlayingState());
        var health = new PollerHealth();
        services.AddSingleton(health);
        services.AddSingleton(new StationCheck(station, health, NullLogger<StationCheck>.Instance));
        services.AddSingleton(new LiveItemArtwork(station, (_, _, _, _) => Task.CompletedTask, NullLogger<LiveItemArtwork>.Instance));
        services.AddSingleton(new StationLogos(station, (_, _, _, _) => Task.CompletedTask, NullLogger<StationLogos>.Instance, TimeProvider.System));
        services.AddSingleton(new LiveItemFinder(FakeLibrary.Create().Manager, () => [], NullLogger<LiveItemFinder>.Instance));
        services.AddSingleton(new StationTitles());

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

    [Fact]
    public async Task A_non_admin_gets_403_from_viewers_and_no_user_is_read()
    {
        var response = await SendAsync(HttpMethod.Get, "Jellywave/Viewers", "User");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("testB", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, _store.Reads);
        Assert.NotEqual(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "Jellywave/Viewers", null)).StatusCode);   // not signed in
        Assert.Equal(0, _store.Reads);
    }

    [Fact]
    public async Task An_admin_gets_the_count_and_the_names()
    {
        var response = await SendAsync(HttpMethod.Get, "Jellywave/Viewers", AdminRole);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"Visible\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"testB\"", json, StringComparison.Ordinal);
        Assert.True(_store.Reads > 0);
        Assert.Empty(_store.Saved);
    }

    [Theory]
    [InlineData("GET", "Jellywave/Health")]
    [InlineData("GET", "Jellywave/CarryAccess")]
    [InlineData("POST", "Jellywave/CarryAccess")]
    [InlineData("POST", "Jellywave/GrantAccess")]
    [InlineData("POST", "Jellywave/Probe")]
    [InlineData("POST", "Jellywave/Health/Check")]
    public async Task Every_other_admin_route_refuses_a_non_admin_too(string method, string url)
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(new HttpMethod(method), url, "User")).StatusCode);
        Assert.Equal(0, _store.Reads);
        Assert.Empty(_store.Saved);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? role)
    {
        var request = new HttpRequestMessage(method, url);
        if (role is not null)
        {
            request.Headers.Add(RoleHeader, role);
        }

        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return _http!.SendAsync(request);
    }

    private sealed class CountingStore : IUserStore
    {
        public Dictionary<Guid, User> Users { get; } = new();

        public List<Guid> Saved { get; } = new();

        public int Reads { get; private set; }

        public IEnumerable<Guid> UserIds()
        {
            Reads++;
            return Users.Keys.ToArray();
        }

        public User? GetUser(Guid id)
        {
            Reads++;
            return Users.TryGetValue(id, out var u) ? u : null;
        }

        public Task SaveAsync(User user)
        {
            Saved.Add(user.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class FoundLocator : IChannelLocator
    {
        public Task<ChannelResolution> LocateAsync(PluginConfiguration config, CancellationToken cancellationToken)
            => Task.FromResult(ChannelResolution.Found(new Channel { Id = Guid.Parse("11111111-1111-1111-1111-111111111111") }));
    }

    /// <summary>Signs the caller in with the role in a test header; no header, no user.</summary>
    private sealed class TestAuthentication : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "caller"), new Claim(ClaimTypes.Role, role.ToString())], TestScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }

    /// <summary>Only the admin controller (the station page controller's services are not part of this host).</summary>
    private sealed class OnlyAdminController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            foreach (var other in feature.Controllers.Where(c => c.AsType() != typeof(JellywaveController)).ToList())
            {
                feature.Controllers.Remove(other);
            }
        }
    }
}
