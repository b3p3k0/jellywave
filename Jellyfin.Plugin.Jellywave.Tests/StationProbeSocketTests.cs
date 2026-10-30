using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L3: the probe through the plugin's registered client against a real socket. A station that streams forever must
/// not hold the probe (headers only), and a Shoutcast v1 "ICY 200 OK" status line, which .NET's client refuses, is named.
/// </summary>
public sealed class StationProbeSocketTests : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public StationProbeSocketTests() => _listener.Start();

    private Uri Root => new("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/");

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    private static StationProbe Probe()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);   // the application host is not used
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        return new StationProbe(new StationClient(factory), NullLogger.Instance);
    }

    /// <summary>Answers every connection with the status line and headers, then streams zeros until the client leaves.</summary>
    private Task ServeAsync(string statusLine, string headers)
        => Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            var stream = client.GetStream();
                            var request = new byte[8192];
                            _ = await stream.ReadAsync(request, _stop.Token);
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(statusLine + "\r\n" + headers + "\r\n"), _stop.Token);
                            var chunk = new byte[16 * 1024];
                            while (!_stop.IsCancellationRequested)
                            {
                                await stream.WriteAsync(chunk, _stop.Token);
                            }
                        }
                        catch (Exception ex) when (ex is OperationCanceledException or System.IO.IOException or SocketException or ObjectDisposedException)
                        {
                            // The client closed the connection: what a headers-only probe does.
                        }
                    }
                });
            }
        });

    [Fact]
    public async Task An_endless_mp3_stream_answers_at_its_headers()
    {
        _ = ServeAsync("HTTP/1.0 200 OK", "Content-Type: audio/mpeg\r\nicy-br: 128\r\nicy-name: Loopback FM\r\n");
        var watch = Stopwatch.StartNew();

        var result = await Probe().ProbeAsync(new Uri(Root, "stream.mp3").AbsoluteUri, CancellationToken.None);

        // The SUB/WAVE check asks /api/state, which also answers with the endless stream: bounded by the body cap.
        Assert.True(result.Ok);
        Assert.Equal(("mp3", 128, "Loopback FM"), (result.Codec, result.BitrateKbps, result.Name));
        Assert.Equal("Stream", result.Kind);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), "took " + watch.Elapsed);
    }

    [Fact]
    public async Task A_shoutcast_v1_status_line_is_named()
    {
        _ = ServeAsync("ICY 200 OK", "Content-Type: audio/mpeg\r\nicy-name: Old Shoutcast\r\n");

        var result = await Probe().ProbeAsync(Root.AbsoluteUri, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(StationProbe.ShoutcastV1, result.Lines.Single().Text);
    }
}
