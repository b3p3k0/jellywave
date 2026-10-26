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
    private readonly StationClient _stationClient;
    private readonly ILogger<JellywaveController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellywaveController"/> class.
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="logger">The logger.</param>
    public JellywaveController(StationClient stationClient, ILogger<JellywaveController> logger)
    {
        _stationClient = stationClient;
        _logger = logger;
    }

    /// <summary>
    /// Probes a SUB/WAVE station from the server and reports what the plugin can learn from it.
    /// </summary>
    /// <param name="baseUrl">The station base URL as seen from the Jellyfin server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The probe result; HTTP 200 whether or not the station answered, with <c>ok</c> telling which.</returns>
    [HttpGet("Station")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StationProbeResult>> ProbeStation([FromQuery, Required] string baseUrl, CancellationToken cancellationToken)
    {
        if (!StationUrls.TryParseBaseUrl(baseUrl, out var uri) || uri is null)
        {
            return BadRequest("baseUrl must be an absolute http(s) URL");
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
            HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
            HttpRequestException => "could not connect",
            JsonException or NotSupportedException or InvalidOperationException => "did not answer with the expected JSON (is this a SUB/WAVE base URL?)",
            _ => "unexpected error"
        };
}
