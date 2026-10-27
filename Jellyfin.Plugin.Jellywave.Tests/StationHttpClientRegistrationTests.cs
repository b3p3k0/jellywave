using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 14: the named client the registrator really registers does not follow a redirect. A loopback listener
/// answers every path under <c>/start</c> with 302 to <c>/landed</c> and counts the hits on <c>/landed</c>.
/// </summary>
public sealed class StationHttpClientRegistrationTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Uri _root;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;
    private int _landed;
    private string? _userAgent;

    public StationHttpClientRegistrationTests()
    {
        var port = FreePort();
        _root = new Uri("http://127.0.0.1:" + port + "/");
        _listener.Prefixes.Add(_root.ToString());
        _listener.Start();
        _serving = ServeAsync();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        try
        {
            _serving.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The listener throws out of GetContextAsync when closed.
        }

        _stop.Dispose();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            Volatile.Write(ref _userAgent, context.Request.UserAgent);
            if (context.Request.Url!.AbsolutePath.StartsWith("/start", StringComparison.Ordinal))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Found;
                context.Response.RedirectLocation = new Uri(_root, "landed").ToString();
            }
            else
            {
                Interlocked.Increment(ref _landed);
                context.Response.StatusCode = (int)HttpStatusCode.OK;
            }

            context.Response.Close();
        }
    }

    private static IHttpClientFactory RegisteredFactory()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);   // the application host is not used
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    [Fact]
    public async Task The_registered_client_does_not_follow_a_redirect()
    {
        using var client = RegisteredFactory().CreateClient(StationClient.HttpClientName);
        using var response = await client.GetAsync(new Uri(_root, "start"));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(new Uri(_root, "landed"), response.Headers.Location);
        Assert.Equal(0, Volatile.Read(ref _landed));
        Assert.StartsWith("Jellywave/", Volatile.Read(ref _userAgent), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_that_follows_redirects_would_have_landed()
    {
        // Control: the listener itself works, so the test above fails if the registration ever follows.
        using var client = new HttpClient();
        using var response = await client.GetAsync(new Uri(_root, "start"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref _landed));
    }

    [Fact]
    public async Task The_station_client_reports_the_redirect_through_the_registered_client()
    {
        var station = new StationClient(RegisteredFactory());
        var ex = await Assert.ThrowsAsync<StationRedirectException>(() => station.GetStateAsync(new Uri(_root, "start/"), CancellationToken.None));
        Assert.Equal(HttpStatusCode.Found, ex.StatusCode);
        Assert.Equal(new Uri(_root, "landed"), ex.Location);
        Assert.Equal(0, Volatile.Read(ref _landed));
    }
}
