using System;
using System.ComponentModel.DataAnnotations;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// Admin endpoints used by the plugin configuration page.
/// </summary>
[ApiController]
[Route("Jellywave")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces("application/json")]
public class JellywaveController : ControllerBase
{
    /// <summary>
    /// The path segment every station endpoint the probe calls sits under.
    /// </summary>
    private const string StationApiSegment = "/api/";

    private readonly StationClient _stationClient;
    private readonly AccessGranter _accessGranter;
    private readonly NowPlayingState _state;
    private readonly PollerHealth _health;
    private readonly LiveItemArtwork _artwork;
    private readonly LiveItemFinder _finder;
    private readonly ILogger<JellywaveController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellywaveController"/> class.
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="accessGranter">The access granter.</param>
    /// <param name="state">The shared now-playing state.</param>
    /// <param name="health">The poller health snapshot.</param>
    /// <param name="artwork">The live item artwork keeper.</param>
    /// <param name="finder">The live item finder.</param>
    /// <param name="logger">The logger.</param>
    public JellywaveController(StationClient stationClient, AccessGranter accessGranter, NowPlayingState state, PollerHealth health, LiveItemArtwork artwork, LiveItemFinder finder, ILogger<JellywaveController> logger)
    {
        _stationClient = stationClient;
        _accessGranter = accessGranter;
        _state = state;
        _health = health;
        _artwork = artwork;
        _finder = finder;
        _logger = logger;
    }

    /// <summary>
    /// The now-playing poller's health, for the configuration page: last success, last error, what the live item carries.
    /// </summary>
    /// <returns>The snapshot.</returns>
    [HttpGet("Health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<HealthResponse> Health()
    {
        var config = Plugin.Instance?.Configuration;
        var channel = _finder.FindChannel(config);
        var health = _health.Snapshot();
        return new HealthResponse
        {
            PollingEnabled = config?.NowPlayingEnabled == true,
            StationConfigured = config is not null && StationUrls.TryGetBaseUrl(config, out _),
            Running = health.Running,
            Reachable = health.Reachable,
            LastSuccessUtc = health.LastSuccessUtc,
            LastErrorUtc = health.LastErrorUtc,
            LastError = health.LastError,
            Description = _state.Description(),
            Cover = _artwork.Applied,
            LiveItemExists = channel is not null && _finder.FindLiveItem(channel) is not null
        };
    }

    /// <summary>
    /// Runs one access-grant pass now (the config page calls this after saving with the setting on).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The counts; <c>Enabled</c> false when the setting is off or the channel does not exist yet.</returns>
    [HttpPost("GrantAccess")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<GrantResult>> GrantAccess(CancellationToken cancellationToken)
    {
        try
        {
            return await _accessGranter.GrantAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Channel access pass failed");
            return new GrantResult { Enabled = false, Error = "the pass failed; see the server log" };
        }
    }

    /// <summary>
    /// Probes a SUB/WAVE station from the server and reports what the plugin can learn from it.
    /// </summary>
    /// <param name="baseUrl">The station base URL as seen from the Jellyfin server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The probe result; HTTP 200 whether or not the station answered, with <c>ok</c> telling which. A redirect is not
    /// followed: the error names where the station pointed, so the admin can enter that address instead.
    /// </returns>
    [HttpGet("Station")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StationProbeResult>> ProbeStation([FromQuery, Required] string baseUrl, CancellationToken cancellationToken)
    {
        if (!StationUrls.TryParseBaseUrl(baseUrl, out var uri) || uri is null)
        {
            return BadRequest("baseUrl must be an absolute http(s) URL without credentials, a query or a fragment");
        }

        try
        {
            var state = await _stationClient.GetStateAsync(uri, cancellationToken).ConfigureAwait(false);
            var feed = await _stationClient.GetNowPlayingAsync(uri, cancellationToken).ConfigureAwait(false);

            return new StationProbeResult
            {
                Ok = true,
                Name = state?.Station?.Name ?? feed?.Dj?.Station,
                Tagline = feed?.Dj?.Tagline,
                StreamOnline = feed?.StreamOnline,
                StreamMount = feed?.Stream?.Mount,
                StreamFormat = feed?.Stream?.Format,
                StreamBitrateKbps = feed?.Stream?.Bitrate,
                StreamSampleRate = feed?.Stream?.SampleRate,
                StreamChannels = feed?.Stream?.Channels,
                PrivatePlayer = state?.Privacy?.PrivatePlayer,
                ListenerAuth = state?.Privacy?.ListenerAuth,
                NowPlaying = feed?.NowPlaying is { } track ? $"{track.Artist} – {track.Title}" : null
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException or InvalidOperationException)
        {
            // Detail stays in the server log; the page gets a category it can act on.
            _logger.LogWarning(ex, "Station probe of {Host} failed", uri.Host);
            return new StationProbeResult { Ok = false, Error = Categorise(ex) };
        }
    }

    private static string Categorise(Exception ex)
        => ex switch
        {
            OperationCanceledException => "timed out",
            StationRedirectException { Location: { } location } => $"the station redirected to {SuggestedBase(location)}; use that address instead",
            StationRedirectException { StatusCode: { } status } => $"the station answered HTTP {(int)status} (a redirect) without saying where to",
            HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
            HttpRequestException => "could not connect",
            JsonException or NotSupportedException or InvalidOperationException => "did not answer with the expected JSON (is this a SUB/WAVE base URL?)",
            _ => "unexpected error"
        };

    /// <summary>
    /// Turns a redirect target into the base URL to enter: the probe asks for <c>api/…</c> under the base, so a
    /// station that moved answers with the new base plus that path, which is cut off along with any query.
    /// </summary>
    /// <param name="location">The absolute redirect target.</param>
    /// <returns>The suggested base URL.</returns>
    internal static string SuggestedBase(Uri location)
    {
        var path = location.GetLeftPart(UriPartial.Path);
        var api = path.LastIndexOf(StationApiSegment, StringComparison.Ordinal);
        return api > location.GetLeftPart(UriPartial.Authority).Length - 1 ? path[..(api + 1)] : path;
    }
}
