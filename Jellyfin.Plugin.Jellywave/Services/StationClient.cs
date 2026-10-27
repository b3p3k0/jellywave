using System;
using System.Collections.Generic;
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
    public Task<StationImage?> GetCoverAsync(Uri baseUrl, string coverId, CancellationToken cancellationToken)
        => IsValidId(coverId) ? GetImageAsync(baseUrl, "api/cover/" + coverId, cancellationToken) : Task.FromResult<StationImage?>(null);

    /// <summary>
    /// Gets a DJ portrait: <c>GET /api/persona-avatar/{id}</c>. The station answers a transparent PNG when none is set.
    /// </summary>
    /// <param name="baseUrl">The station base URL with a trailing slash.</param>
    /// <param name="personaId">The persona id (3 to 32 characters of lowercase letters, digits and underscore).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The image, or null when the id is invalid or the answer is unusable.</returns>
    public Task<StationImage?> GetAvatarAsync(Uri baseUrl, string personaId, CancellationToken cancellationToken)
        => IsValidId(personaId) ? GetImageAsync(baseUrl, "api/persona-avatar/" + personaId, cancellationToken) : Task.FromResult<StationImage?>(null);

    /// <summary>
    /// Reads the like status of the track on air: <c>GET /api/like</c>.
    /// </summary>
    /// <param name="baseUrl">The station base URL with a trailing slash.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status code and body.</returns>
    public async Task<StationReply<LikeStatus>> GetLikeAsync(Uri baseUrl, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(NamedClient.Default);
        client.Timeout = RequestTimeout;
        client.MaxResponseContentBufferSize = MaxResponseBytes;
        using var response = await client.GetAsync(new Uri(baseUrl, "api/like"), HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        return new StationReply<LikeStatus>((int)response.StatusCode, await ReadBodyAsync<LikeStatus>(response, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Likes the track on air: <c>POST /api/like</c>. 403 disabled, 409 the track ended, 429 too fast.
    /// </summary>
    /// <param name="baseUrl">The station base URL with a trailing slash.</param>
    /// <param name="songId">The song id the listener saw, so a stale tap cannot like the next track.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status code and body.</returns>
    public async Task<StationReply<LikeStatus>> PostLikeAsync(Uri baseUrl, string songId, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(NamedClient.Default);
        client.Timeout = RequestTimeout;
        client.MaxResponseContentBufferSize = MaxResponseBytes;
        using var content = JsonContent.Create(new Dictionary<string, string> { ["songId"] = songId }, options: JsonOptions);
        using var response = await client.PostAsync(new Uri(baseUrl, "api/like"), content, cancellationToken).ConfigureAwait(false);
        return new StationReply<LikeStatus>((int)response.StatusCode, await ReadBodyAsync<LikeStatus>(response, cancellationToken).ConfigureAwait(false));
    }

    private async Task<StationImage?> GetImageAsync(Uri baseUrl, string relativePath, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(NamedClient.Default);
        client.Timeout = RequestTimeout;
        client.MaxResponseContentBufferSize = MaxImageBytes;

        // Headers first: a declared oversize or a wrong type is "no art", decided without reading the body.
        using var response = await client.GetAsync(new Uri(baseUrl, relativePath), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Submits a listener request: <c>POST /api/request</c>. The station answers 202 with a receipt, or refuses with
    /// 400 (empty text), 429 (its per-caller and global rate limits; the body says how long to wait) or 503 (requests
    /// closed or the DJ on autopilot). All Jellyfin users share one caller as seen by the station.
    /// </summary>
    /// <param name="baseUrl">The station base URL with a trailing slash.</param>
    /// <param name="text">The request text (1 to 280 characters).</param>
    /// <param name="name">The listener's name (up to 40 characters), or empty.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status code and the parsed body.</returns>
    public async Task<StationReply<RequestReceipt>> SubmitRequestAsync(Uri baseUrl, string text, string name, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(NamedClient.Default);
        client.Timeout = RequestTimeout;
        client.MaxResponseContentBufferSize = MaxResponseBytes;
        using var content = JsonContent.Create(new Dictionary<string, string> { ["text"] = text, ["name"] = name }, options: JsonOptions);
        using var response = await client.PostAsync(new Uri(baseUrl, "api/request"), content, cancellationToken).ConfigureAwait(false);
        return new StationReply<RequestReceipt>((int)response.StatusCode, await ReadBodyAsync<RequestReceipt>(response, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Polls a submitted request: <c>GET /api/request/{id}</c>. 404 means the station no longer knows the id.
    /// </summary>
    /// <param name="baseUrl">The station base URL with a trailing slash.</param>
    /// <param name="requestId">The id from the receipt.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status code and the parsed body.</returns>
    public async Task<StationReply<RequestStatus>> GetRequestStatusAsync(Uri baseUrl, string requestId, CancellationToken cancellationToken)
    {
        if (!IsValidId(requestId))
        {
            return new StationReply<RequestStatus>(404, null);
        }

        using var client = _httpClientFactory.CreateClient(NamedClient.Default);
        client.Timeout = RequestTimeout;
        client.MaxResponseContentBufferSize = MaxResponseBytes;
        using var response = await client.GetAsync(new Uri(baseUrl, "api/request/" + requestId), HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        return new StationReply<RequestStatus>((int)response.StatusCode, await ReadBodyAsync<RequestStatus>(response, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
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
