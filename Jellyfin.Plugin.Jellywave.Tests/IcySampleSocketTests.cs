using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L6 (RA R1): a title sample through the plugin's registered client against a real socket, on HTTP/1.1 with a
/// chunked or a declared-length body (under 1 MB to go), the shapes .NET drains on dispose to keep the connection. The station sends
/// what the sample needs plus a short tail, then stalls with the connection open: a client that drains would sit on the
/// stall until its drain timeout (2 s by default); the plugin's client closes at once.
/// </summary>
public sealed class IcySampleSocketTests : IDisposable
{
    private const int MetaInt = 16000;

    /// <summary>The audio a stalling station sends after the metadata block: well under .NET's default 1 MB drain.</summary>
    private const int Tail = 64 * 1024;

    /// <summary>How soon after the sample returns the station must see the connection close.</summary>
    private static readonly TimeSpan CloseWithin = TimeSpan.FromSeconds(1);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public IcySampleSocketTests() => _listener.Start();

    private Uri Stream => new("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/live.mp3");

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    private static StationClient Client()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);   // the application host is not used
        return new StationClient(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>());
    }

    /// <summary>
    /// Serves one connection: the headers, then <paramref name="body"/> (as chunks or raw), then stalls until the client
    /// closes. Completes with the time the close was seen and the body bytes sent.
    /// </summary>
    private Task<(DateTime ClosedUtc, long Sent)> ServeOnceAsync(string headers, byte[] body, bool chunked)
        => Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            var stream = client.GetStream();
            var request = new byte[8192];
            _ = await stream.ReadAsync(request, _stop.Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n" + headers + "\r\n"), _stop.Token);
            long sent = 0;
            try
            {
                for (var at = 0; at < body.Length; at += 4096)
                {
                    var length = Math.Min(4096, body.Length - at);
                    if (chunked)
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(length.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "\r\n"), _stop.Token);
                    }

                    await stream.WriteAsync(body.AsMemory(at, length), _stop.Token);
                    if (chunked)
                    {
                        await stream.WriteAsync("\r\n"u8.ToArray(), _stop.Token);
                    }

                    sent += length;
                }

                // Stall: send nothing more and wait for the client to close (a read of 0 bytes, or a reset).
                while (await stream.ReadAsync(request, _stop.Token) > 0)
                {
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or SocketException)
            {
                // The client closed while the body was still being sent: the close is seen now.
            }

            return (DateTime.UtcNow, sent);
        });

    private static byte[] Body(string title, int metaInt, int tail)
    {
        var block = IcyMetadataTests.Block("StreamTitle='" + title + "';");
        var body = new byte[metaInt + 1 + block.Length + tail];
        body[metaInt] = (byte)(block.Length / IcyMetadata.BlockUnit);
        block.CopyTo(body, metaInt + 1);
        return body;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_sample_closes_at_once_after_its_block(bool chunked)
    {
        var body = Body("Socket - FM", MetaInt, Tail);
        var headers = "Content-Type: audio/mpeg\r\nicy-metaint: 16000\r\n" + (chunked ? "Transfer-Encoding: chunked\r\n" : "Content-Length: " + (body.Length + Tail) + "\r\n");
        var client = Client();   // built before the clock starts: the service provider is not the sample's time
        var served = ServeOnceAsync(headers, body, chunked);
        var watch = Stopwatch.StartNew();

        var sample = await client.SampleIcyTitleAsync(Stream, null, CancellationToken.None);
        var returnedUtc = DateTime.UtcNow;
        var (closedUtc, _) = await served.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Socket - FM", sample.Title);
        Assert.True(closedUtc - returnedUtc < CloseWithin, "the station saw the close " + (closedUtc - returnedUtc) + " after the sample returned");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "took " + watch.Elapsed);

        // The station offers more than the client may read (at most the cap, IcySampleClientTests), so a client that kept
        // the connection would have a body to drain. How much of it the station got out before the close depends on the
        // machine's load (the client can read its block before the station's last writes): an exact count of the bytes
        // sent failed on one core (fix round 2), so the test no longer asserts one.
        Assert.True(IcyMetadata.ReadCap(MetaInt) < body.Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stream_over_the_metaint_cap_closes_at_once_without_reading(bool chunked)
    {
        var body = Body("Never read", 100000, Tail);
        var headers = "Content-Type: audio/mpeg\r\nicy-metaint: 100000\r\n" + (chunked ? "Transfer-Encoding: chunked\r\n" : "Content-Length: " + (body.Length + Tail) + "\r\n");
        var served = ServeOnceAsync(headers, body, chunked);

        var sample = await Client().SampleIcyTitleAsync(Stream, null, CancellationToken.None);
        var returnedUtc = DateTime.UtcNow;
        var (closedUtc, _) = await served.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(sample.HasMetadata);
        Assert.True(closedUtc - returnedUtc < CloseWithin, "the station saw the close " + (closedUtc - returnedUtc) + " after the sample returned");
    }

    [Fact]
    public async Task A_stream_without_metaint_closes_at_once()
    {
        var served = ServeOnceAsync("Content-Type: audio/mpeg\r\nTransfer-Encoding: chunked\r\n", new byte[Tail], chunked: true);

        var sample = await Client().SampleIcyTitleAsync(Stream, null, CancellationToken.None);
        var returnedUtc = DateTime.UtcNow;
        var (closedUtc, _) = await served.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(sample.HasMetadata);
        Assert.True(closedUtc - returnedUtc < CloseWithin, "the station saw the close " + (closedUtc - returnedUtc) + " after the sample returned");
    }
}
