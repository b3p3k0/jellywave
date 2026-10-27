using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

public class StationClientCoverTests
{
    private static readonly Uri Base = new("http://station.example/");

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(_respond(request));
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, byte[]? body = null, string? mime = "image/jpeg", long? declaredLength = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body ?? []) };
        response.Content.Headers.ContentType = mime is null ? null : new MediaTypeHeaderValue(mime);
        if (declaredLength is not null)
        {
            response.Content.Headers.ContentLength = declaredLength;
        }

        return response;
    }

    private static (StationClient Client, StubHandler Handler) Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new StationClient(new StubFactory(handler)), handler);
    }

    [Fact]
    public async Task Returns_the_image_and_its_type_on_success()
    {
        var (client, handler) = Client(r =>
        {
            Assert.Equal("http://station.example/api/cover/abc-123", r.RequestUri!.ToString());
            return Response(HttpStatusCode.OK, [1, 2, 3], "image/png");
        });
        var image = await client.GetCoverAsync(Base, "abc-123", CancellationToken.None);
        Assert.NotNull(image);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(3, image.Length);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Missing_content_type_is_taken_as_jpeg()
    {
        var (client, _) = Client(_ => Response(HttpStatusCode.OK, [1], mime: null));
        var image = await client.GetCoverAsync(Base, "x", CancellationToken.None);
        Assert.Equal("image/jpeg", image!.MimeType);
    }

    [Fact]
    public async Task No_art_is_a_502_with_an_empty_body()
    {
        var (client, _) = Client(_ => Response(HttpStatusCode.BadGateway));
        Assert.Null(await client.GetCoverAsync(Base, "x", CancellationToken.None));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/octet-stream")]
    public async Task Types_outside_the_allowlist_count_as_no_art(string mime)
    {
        var (client, _) = Client(_ => Response(HttpStatusCode.OK, [1, 2], mime));
        Assert.Null(await client.GetCoverAsync(Base, "x", CancellationToken.None));
    }

    [Fact]
    public async Task Empty_body_counts_as_no_art()
    {
        var (client, _) = Client(_ => Response(HttpStatusCode.OK, []));
        Assert.Null(await client.GetCoverAsync(Base, "x", CancellationToken.None));
    }

    [Fact]
    public async Task Declared_oversize_counts_as_no_art_without_reading_the_body()
    {
        var (client, _) = Client(_ => Response(HttpStatusCode.OK, [1], declaredLength: 5L * 1024 * 1024));
        Assert.Null(await client.GetCoverAsync(Base, "x", CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_ids_are_never_requested()
    {
        var (client, handler) = Client(_ => Response(HttpStatusCode.OK, [1]));
        Assert.Null(await client.GetCoverAsync(Base, "../etc/passwd", CancellationToken.None));
        Assert.Equal(0, handler.Requests);
    }
}
