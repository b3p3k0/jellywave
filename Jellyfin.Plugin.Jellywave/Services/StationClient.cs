using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Reads SUB/WAVE's public JSON endpoints (no credentials involved).
/// </summary>
public class StationClient
{
    /// <summary>
    /// Largest body accepted. The feeds are a few KB; anything bigger is not a SUB/WAVE answer.
    /// </summary>
    private const long MaxResponseBytes = 256 * 1024;

    /// <summary>
    /// Largest cover image accepted. The station serves 512 px JPEGs; a few hundred KB at most.
    /// </summary>
    private const long MaxImageBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Longest station id accepted in a URL path segment (the station enforces the same shape).
    /// </summary>
    private const int MaxIdLength = 64;

    /// <summary>
    /// Cover media types saved as the item's image; Jellyfin's image processor decodes these.
    /// </summary>
    private static readonly string[] AcceptedImageTypes = ["image/jpeg", "image/png", "image/webp", "image/gif"];

    /// <summary>
    /// Whole-request budget (connect, headers and body) per call.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="StationClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public StationClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Gets the station's now-playing feed: current track, stream format, DJ and show.
    /// </summary>
    /// <param name="baseUrl">The station base URL with a trailing slash.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The feed, or null when the station answered with something other than the expected JSON.</returns>
    public Task<NowPlayingFeed?> GetNowPlayingAsync(Uri baseUrl, CancellationToken cancellationToken)
        => GetJsonAsync<NowPlayingFeed>(new Uri(baseUrl, "api/now-playing"), cancellationToken);

    /// <summary>
    /// Gets the station state: name and privacy flags.
    /// </summary>
    /// <param name="baseUrl">The station base URL with a trailing slash.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The state, or null when the station answered with something other than the expected JSON.</returns>
    public Task<StationState?> GetStateAsync(Uri baseUrl, CancellationToken cancellationToken)
        => GetJsonAsync<StationState>(new Uri(baseUrl, "api/state"), cancellationToken);

    /// <summary>
    /// Gets the cover art of a track: <c>GET /api/cover/{id}</c>.
    /// </summary>
    /// <param name="baseUrl">The station base URL with a trailing slash.</param>
    /// <param name="coverId">The track's music-server id, as given by the now-playing feed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The image, or null when the track has no usable art: the station answers 502 with an empty body when it has none,
    /// and an unknown id, a non-success status, a media type outside <see cref="AcceptedImageTypes"/>, an empty body or a
    /// declared length over the cap count the same way. A missing content type is taken as JPEG, the station's default.
    /// Transport failures (timeouts, a chunked body that overruns the cap) throw and are retried by the caller.
    /// </returns>
    public async Task<StationImage?> GetCoverAsync(Uri baseUrl, string coverId, CancellationToken cancellationToken)
    {
        if (!IsValidId(coverId))
        {
            return null;
        }

        using var client = _httpClientFactory.CreateClient(NamedClient.Default);
        client.Timeout = RequestTimeout;
        client.MaxResponseContentBufferSize = MaxImageBytes;

        // Headers first: a declared oversize or a wrong type is "no art", decided without reading the body.
        using var response = await client.GetAsync(new Uri(baseUrl, "api/cover/" + coverId), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var mimeType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        if (Array.IndexOf(AcceptedImageTypes, mimeType.ToLowerInvariant()) < 0)
        {
            return null;
        }

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength is > MaxImageBytes or 0)
        {
            return null;
        }

        // With ResponseHeadersRead the client's Timeout and buffer cap stop at the headers (card 3 lesson), so the
        // body gets its own budget: the same timeout, and LoadIntoBufferAsync's cap for chunked bodies.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        await response.Content.LoadIntoBufferAsync(MaxImageBytes, timeout.Token).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        return bytes.Length == 0 ? null : new StationImage(mimeType, bytes);
    }

    /// <summary>
    /// Checks that a station id is safe to put in a URL path: 1 to 64 characters from letters, digits, <c>_</c> and <c>-</c>,
    /// the same rule SUB/WAVE applies before it proxies the id to the music server.
    /// </summary>
    /// <param name="id">The id to check.</param>
    /// <returns>True when the id is acceptable.</returns>
    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength)
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    private async Task<T?> GetJsonAsync<T>(Uri url, CancellationToken cancellationToken)
        where T : class
    {
        using var client = _httpClientFactory.CreateClient(NamedClient.Default);
        client.Timeout = RequestTimeout;
        client.MaxResponseContentBufferSize = MaxResponseBytes;

        // ResponseContentRead (the default) makes Timeout cover the body as well as the headers,
        // and MaxResponseContentBufferSize rejects oversized bodies before they are buffered.
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}
