using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L6: <see cref="StationClient.SampleIcyTitleAsync"/> against a fake handler whose stream counts every byte it hands
/// out: it asks for metadata, reads no audio without an accepted <c>icy-metaint</c>, never reads past the cap, follows no
/// redirect, and stops at its budget.
/// </summary>
public class IcySampleClientTests
{
    private static readonly Uri Stream = new("http://cdn.example/live.mp3");

    /// <summary>An endless stream: <c>metaInt</c> audio bytes, a metadata block, again; counts what was read.</summary>
    private sealed class IcyStream : System.IO.Stream
    {
        private readonly int _metaInt;
        private readonly byte[] _block;
        private readonly long _endAfter;
        private long _position;

        public IcyStream(int metaInt, byte[] block, long endAfter = long.MaxValue)
        {
            _metaInt = metaInt;
            _block = block;
            _endAfter = endAfter;
        }

        public long BytesRead => _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = 0;
            while (n < count && _position < _endAfter)
            {
                var cycle = _metaInt + 1 + _block.Length;
                var at = (int)(_position % cycle);
                buffer[offset + n] = at < _metaInt ? (byte)0xAA : at == _metaInt ? (byte)(_block.Length / IcyMetadata.BlockUnit) : _block[at - _metaInt - 1];
                _position++;
                n++;
            }

            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A stream that sends nothing until it is cancelled.</summary>
    private sealed class SilentStream : System.IO.Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public int Requests { get; private set; }

        public string? IcyMetaData { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            IcyMetaData = request.Headers.TryGetValues("Icy-MetaData", out var values) ? string.Join(",", values) : null;
            return Task.FromResult(_respond(request));
        }
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public Factory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static HttpResponseMessage Audio(System.IO.Stream body, string? metaInt)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        if (metaInt is not null)
        {
            response.Headers.TryAddWithoutValidation("icy-metaint", metaInt);
        }

        return response;
    }

    private static (StationClient Client, Handler Handler) Client(Func<HttpRequestMessage, HttpResponseMessage> respond, TimeSpan? timeout = null)
    {
        var handler = new Handler(respond);
        return (timeout is null ? new StationClient(new Factory(handler)) : new StationClient(new Factory(handler), timeout.Value), handler);
    }

    [Fact]
    public async Task Reads_the_title_from_the_first_block_and_asks_for_metadata()
    {
        var body = new IcyStream(16000, IcyMetadataTests.Block("StreamTitle='Artist - Title';StreamUrl='';"));
        var (client, handler) = Client(_ => Audio(body, "16000"));

        var sample = await client.SampleIcyTitleAsync(Stream, null, CancellationToken.None);

        Assert.True(sample.HasMetadata);
        Assert.Equal("Artist - Title", sample.Title);
        Assert.Equal("1", handler.IcyMetaData);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(16000 + 1 + 48, body.BytesRead);   // the audio, the length byte and this block: nothing more
    }

    [Fact]
    public async Task Never_reads_past_the_cap_with_the_largest_block()
    {
        var block = IcyMetadataTests.Block("StreamTitle='" + new string('x', IcyMetadata.MaxBlockBytes - 15) + "';");
        Assert.Equal(IcyMetadata.MaxBlockBytes, block.Length);
        var body = new IcyStream(IcyMetadata.MaxMetaInt, block);
        var (client, _) = Client(_ => Audio(body, IcyMetadata.MaxMetaInt.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var sample = await client.SampleIcyTitleAsync(Stream, null, CancellationToken.None);

        Assert.Equal(IcyMetadata.MaxBlockBytes - 15, sample.Title!.Length);
        Assert.Equal(IcyMetadata.ReadCap(IcyMetadata.MaxMetaInt), body.BytesRead);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("65537")]
    [InlineData("10000000")]
    [InlineData("0")]
    [InlineData("abc")]
    public async Task Without_an_accepted_icy_metaint_no_audio_is_read(string? metaInt)
    {
        var body = new IcyStream(16000, IcyMetadataTests.Block("StreamTitle='x';"));
        var (client, handler) = Client(_ => Audio(body, metaInt));

        var sample = await client.SampleIcyTitleAsync(Stream, null, CancellationToken.None);

        Assert.False(sample.HasMetadata);
        Assert.Null(sample.Title);
        Assert.Equal(0, body.BytesRead);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task An_empty_block_reads_as_no_title()
    {
        var body = new IcyStream(8192, []);
        var (client, _) = Client(_ => Audio(body, "8192"));

        var sample = await client.SampleIcyTitleAsync(Stream, null, CancellationToken.None);

        Assert.True(sample.HasMetadata);
        Assert.Null(sample.Title);
        Assert.Equal(8192 + 1, body.BytesRead);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public async Task A_redirect_is_not_followed(HttpStatusCode status)
    {
        var (client, handler) = Client(_ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri("http://elsewhere.example/other.mp3");
            response.Headers.TryAddWithoutValidation("icy-metaint", "16000");
            return response;
        });

        var ex = await Assert.ThrowsAsync<StationRedirectException>(() => client.SampleIcyTitleAsync(Stream, null, CancellationToken.None));

        Assert.Null(ex.Location);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(StationFailures.Redirected, StationFailures.Category(ex));
    }

    [Fact]
    public async Task An_error_status_throws_with_its_code()
    {
        var (client, _) = Client(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SampleIcyTitleAsync(Stream, null, CancellationToken.None));

        Assert.Equal("HTTP 503", StationFailures.Category(ex));
    }

    [Fact]
    public async Task A_stream_that_ends_before_the_block_throws()
    {
        var body = new IcyStream(16000, IcyMetadataTests.Block("StreamTitle='x';"), endAfter: 15000);
        var (client, _) = Client(_ => Audio(body, "16000"));

        await Assert.ThrowsAsync<EndOfStreamException>(() => client.SampleIcyTitleAsync(Stream, null, CancellationToken.None));
        Assert.Equal(15000, body.BytesRead);
    }

    [Fact]
    public async Task A_stream_that_ends_inside_the_block_throws()
    {
        var body = new IcyStream(100, IcyMetadataTests.Block("StreamTitle='a longer title';"), endAfter: 110);
        var (client, _) = Client(_ => Audio(body, "100"));

        await Assert.ThrowsAsync<EndOfStreamException>(() => client.SampleIcyTitleAsync(Stream, null, CancellationToken.None));
    }

    [Fact(Timeout = 20000)]   // a sample without its budget would hang here; fail instead
    public async Task A_silent_stream_stops_at_the_budget()
    {
        var (client, _) = Client(_ => Audio(new SilentStream(), "16000"), TimeSpan.FromMilliseconds(200));
        var started = DateTime.UtcNow;

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SampleIcyTitleAsync(Stream, null, CancellationToken.None));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "the budget ends the read");
        Assert.Equal(StationFailures.TimedOut, StationFailures.Category(ex));
    }

    [Fact]
    public void The_budget_is_the_station_client_s_eight_seconds()
    {
        var field = typeof(StationClient).GetField("RequestTimeout", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.Equal(TimeSpan.FromSeconds(8), (TimeSpan)field.GetValue(null)!);
        Assert.Equal(64 * 1024, IcyMetadata.MaxMetaInt);
        Assert.Equal(255 * 16, IcyMetadata.MaxBlockBytes);
        Assert.Single(typeof(StationClient).GetMethods(), m => m.Name == nameof(StationClient.SampleIcyTitleAsync));
    }
}
