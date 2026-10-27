using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Reads SUB/WAVE's public JSON endpoints (no credentials involved). Every call goes through the plugin's own named
/// client (<see cref="HttpClientName"/>), which never follows a redirect: a 3xx is the station's answer, not a hop.
/// </summary>
public class StationClient
{
    /// <summary>
    /// The named <see cref="HttpClient"/> registered by <see cref="PluginServiceRegistrator"/>, with redirects off.
    /// </summary>
    public const string HttpClientName = "Jellywave";

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
    /// Prefix a declared image content type must have; the type itself comes from <see cref="ImageSniffer"/>.
    /// </summary>
    private const string ImageTypePrefix = "image/";

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
    /// and an unknown id, a non-success status (a redirect included), a declared type that is set but not
    /// <c>image/*</c>, a declared length over the cap, or bytes that <see cref="ImageSniffer"/> does not accept (unknown
    /// type, oversize dimensions, empty) count the same way. The media type is the sniffed one, never the declared one.
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
    /// <returns>The image, or null when the id is invalid or the answer is unusable (the same rules as covers).</returns>
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
        using var client = CreateClient(MaxResponseBytes);
        using var response = await client.GetAsync(new Uri(baseUrl, "api/like"), HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        return await ReadReplyAsync<LikeStatus>(response, cancellationToken).ConfigureAwait(false);
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
        using var client = CreateClient(MaxResponseBytes);
        using var content = JsonContent.Create(new Dictionary<string, string> { ["songId"] = songId }, options: JsonOptions);
        using var response = await client.PostAsync(new Uri(baseUrl, "api/like"), content, cancellationToken).ConfigureAwait(false);
        return await ReadReplyAsync<LikeStatus>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the weekly schedule: <c>GET /api/schedule</c>.
    /// </summary>
    /// <param name="baseUrl">The station base URL with a trailing slash.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The schedule, or null when the answer was not the expected JSON.</returns>
    public Task<ScheduleInfo?> GetScheduleAsync(Uri baseUrl, CancellationToken cancellationToken)
        => GetJsonAsync<ScheduleInfo>(new Uri(baseUrl, "api/schedule"), cancellationToken);

    /// <summary>
    /// Gets the lyrics of the track on air: <c>GET /api/lyrics/current?songId=</c>. 403 when the station has lyrics
    /// off, 401 when the station is private (the page then says lyrics are unavailable), 409 when the track ended,
    /// 503 when the music server did not answer.
    /// </summary>
    /// <param name="baseUrl">The station base URL with a trailing slash.</param>
    /// <param name="songId">The song id from the feed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status code and body.</returns>
    public async Task<StationReply<LyricsPayload>> GetLyricsAsync(Uri baseUrl, string songId, CancellationToken cancellationToken)
    {
        if (!IsValidId(songId))
        {
            return new StationReply<LyricsPayload>(400, null);
        }

        using var client = CreateClient(MaxResponseBytes);
        using var response = await client.GetAsync(new Uri(baseUrl, "api/lyrics/current?songId=" + Uri.EscapeDataString(songId)), HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        return await ReadReplyAsync<LyricsPayload>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StationImage?> GetImageAsync(Uri baseUrl, string relativePath, CancellationToken cancellationToken)
    {
        using var client = CreateClient(MaxImageBytes);

        // Headers first: a declared oversize or a wrong type is "no art", decided without reading the body.
        using var response = await client.GetAsync(new Uri(baseUrl, relativePath), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        // The declared type only has to look like an image (or be missing); the bytes decide which one it is.
        var declaredType = response.Content.Headers.ContentType?.MediaType;
        if (declaredType is not null && !declaredType.StartsWith(ImageTypePrefix, StringComparison.OrdinalIgnoreCase))
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
        return ImageSniffer.TryAccept(bytes, out var mimeType) ? new StationImage(mimeType, bytes) : null;
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
        using var client = CreateClient(MaxResponseBytes);
        using var content = JsonContent.Create(new Dictionary<string, string> { ["text"] = text, ["name"] = name }, options: JsonOptions);
        using var response = await client.PostAsync(new Uri(baseUrl, "api/request"), content, cancellationToken).ConfigureAwait(false);
        return await ReadReplyAsync<RequestReceipt>(response, cancellationToken).ConfigureAwait(false);
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

        using var client = CreateClient(MaxResponseBytes);
        using var response = await client.GetAsync(new Uri(baseUrl, "api/request/" + requestId), HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        return await ReadReplyAsync<RequestStatus>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The status and body of a station answer. A redirect's body is not the station's answer and is not read, so
    /// the callers see only the 3xx status and map it to unavailable or refused.
    /// </summary>
    private static async Task<StationReply<T>> ReadReplyAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : class
    {
        var status = (int)response.StatusCode;
        var body = IsRedirect(response.StatusCode) ? null : await ReadBodyAsync<T>(response, cancellationToken).ConfigureAwait(false);
        return new StationReply<T>(status, body);
    }

    private static bool IsRedirect(HttpStatusCode status)
        => (int)status is >= 300 and < 400;

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
        using var client = CreateClient(MaxResponseBytes);

        // ResponseContentRead (the default) makes Timeout cover the body as well as the headers,
        // and MaxResponseContentBufferSize rejects oversized bodies before they are buffered.
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (IsRedirect(response.StatusCode))
        {
            var location = response.Headers.Location;
            throw new StationRedirectException(response.StatusCode, location is null ? null : new Uri(url, location));
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the plugin's named client (redirects off) with the per-call budget and body cap.
    /// </summary>
    /// <param name="maxResponseBytes">Largest body buffered.</param>
    /// <returns>The client; dispose it when done.</returns>
    private HttpClient CreateClient(long maxResponseBytes)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = RequestTimeout;
        client.MaxResponseContentBufferSize = maxResponseBytes;
        return client;
    }
}
