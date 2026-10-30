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
    private static readonly byte[] Png = ImageFixtures.Png(ImageFixtures.CoverSide, ImageFixtures.CoverSide);

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
            return Response(HttpStatusCode.OK, Png, "image/png");
        });
        var image = await client.GetCoverAsync(Base, "abc-123", CancellationToken.None);
        Assert.NotNull(image);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(Png.Length, image.Length);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Missing_content_type_uses_the_sniffed_type()
    {
        var (client, _) = Client(_ => Response(HttpStatusCode.OK, Png, mime: null));
        var image = await client.GetCoverAsync(Base, "x", CancellationToken.None);
        Assert.Equal("image/png", image!.MimeType);
    }

    [Fact]
    public async Task Missing_content_type_is_no_longer_assumed_to_be_jpeg()
    {
        var (client, _) = Client(_ => Response(HttpStatusCode.OK, [1, 2, 3], mime: null));
        Assert.Null(await client.GetCoverAsync(Base, "x", CancellationToken.None));
    }

    [Fact]
    public async Task A_declared_type_that_disagrees_with_the_bytes_loses_to_the_bytes()
    {
        var (client, _) = Client(_ => Response(HttpStatusCode.OK, ImageFixtures.Gif(64, 64), "image/jpeg"));
        var image = await client.GetCoverAsync(Base, "x", CancellationToken.None);
        Assert.Equal("image/gif", image!.MimeType);
    }

    [Fact]
    public async Task Oversize_dimensions_count_as_no_art()
    {
        var (client, _) = Client(_ => Response(HttpStatusCode.OK, ImageFixtures.Png(ImageSniffer.MaxImageDimension + 1, 16), "image/png"));
        Assert.Null(await client.GetCoverAsync(Base, "x", CancellationToken.None));
    }

    [Fact]
    public async Task An_animated_image_counts_as_no_art_for_covers_avatars_and_logos()
    {
        // Card R2a (D9): all three come through the same image check, which now refuses a second frame.
        var animated = ImageFixtures.Gif(64, 64, "89a", frames: 3);
        var (client, _) = Client(_ => Response(HttpStatusCode.OK, animated, "image/gif"));
        Assert.Null(await client.GetCoverAsync(Base, "x", CancellationToken.None));
        Assert.Null(await client.GetAvatarAsync(Base, "dj_one", CancellationToken.None));
        Assert.Null(await client.GetLogoAsync(new Uri("https://img.example/logo.gif"), CancellationToken.None));

        (client, _) = Client(_ => Response(HttpStatusCode.OK, ImageFixtures.Gif(64, 64), "image/gif"));
        Assert.Equal("image/gif", (await client.GetCoverAsync(Base, "x", CancellationToken.None))!.MimeType);
    }

    [Fact]
    public async Task Avatars_are_sniffed_too()
    {
        var (client, _) = Client(_ => Response(HttpStatusCode.OK, [0x3C, 0x73, 0x76, 0x67], "image/png"));
        Assert.Null(await client.GetAvatarAsync(Base, "dj_one", CancellationToken.None));

        (client, _) = Client(_ => Response(HttpStatusCode.OK, Png, "image/jpeg"));
        Assert.Equal("image/png", (await client.GetAvatarAsync(Base, "dj_one", CancellationToken.None))!.MimeType);
    }

    [Fact]
    public async Task No_art_is_a_502_with_an_empty_body()
    {
        var (client, _) = Client(_ => Response(HttpStatusCode.BadGateway));
        Assert.Null(await client.GetCoverAsync(Base, "x", CancellationToken.None));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    public async Task A_declared_type_outside_image_counts_as_no_art_even_with_image_bytes(string mime)
    {
        var (client, _) = Client(_ => Response(HttpStatusCode.OK, Png, mime));
        Assert.Null(await client.GetCoverAsync(Base, "x", CancellationToken.None));
    }

    [Fact]
    public async Task An_image_type_the_sniffer_does_not_know_counts_as_no_art()
    {
        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        var (client, _) = Client(_ => Response(HttpStatusCode.OK, svg, "image/svg+xml"));
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
        var (client, _) = Client(_ => Response(HttpStatusCode.OK, Png, declaredLength: 5L * 1024 * 1024));
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
