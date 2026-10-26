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

    private async Task<T?> GetJsonAsync<T>(Uri url, CancellationToken cancellationToken)
        where T : class
    {
        using var client = _httpClientFactory.CreateClient(NamedClient.Default);
        client.Timeout = RequestTimeout;
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}
