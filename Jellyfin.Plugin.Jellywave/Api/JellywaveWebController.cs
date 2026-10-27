using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using Jellyfin.Plugin.Jellywave.Web;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// Endpoints behind the station page: the injected assets (anonymous, like the web client itself), the feed
/// (any signed-in user) and the injection status (administrators, for the configuration page).
/// </summary>
[ApiController]
[Route("Jellywave")]
public class JellywaveWebController : ControllerBase
{
    private const string ScriptResource = "Jellyfin.Plugin.Jellywave.Web.ui.js";
    private const string StyleResource = "Jellyfin.Plugin.Jellywave.Web.ui.css";
    private const int MaxRequestText = 280;
    private const int MaxRequestName = 40;
    private static readonly TimeSpan AssetCacheLifetime = TimeSpan.FromDays(1);

    private readonly NowPlayingState _state;
    private readonly LiveItemFinder _finder;
    private readonly IImageProcessor _imageProcessor;
    private readonly StationClient _stationClient;
    private readonly ILogger<JellywaveWebController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellywaveWebController"/> class.
    /// </summary>
    /// <param name="state">The shared now-playing state.</param>
    /// <param name="finder">The live item finder.</param>
    /// <param name="imageProcessor">The image processor, for image tags.</param>
    /// <param name="stationClient">The station client, for requests.</param>
    /// <param name="logger">The logger.</param>
    public JellywaveWebController(NowPlayingState state, LiveItemFinder finder, IImageProcessor imageProcessor, StationClient stationClient, ILogger<JellywaveWebController> logger)
    {
        _state = state;
        _finder = finder;
        _imageProcessor = imageProcessor;
        _stationClient = stationClient;
        _logger = logger;
    }

    /// <summary>
    /// The injected script. Anonymous: it is part of the web client's page load, before sign-in.
    /// </summary>
    /// <returns>The script.</returns>
    [HttpGet("ui.js")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    public IActionResult Script() => Asset(ScriptResource, "text/javascript");

    /// <summary>
    /// The injected stylesheet.
    /// </summary>
    /// <returns>The stylesheet.</returns>
    [HttpGet("ui.css")]
    [AllowAnonymous]
    [Produces("text/css")]
    public IActionResult Style() => Asset(StyleResource, "text/css");

    /// <summary>
    /// What is on air, for the station page. Any signed-in user.
    /// </summary>
    /// <returns>The feed.</returns>
    [HttpGet("Feed")]
    [Authorize]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<FeedResponse> Feed()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var feed = _state.Feed();
        var next = feed?.Context?.ShowHandover?.NextShow;
        var response = new FeedResponse
        {
            StationName = JellywaveChannel.StationName(config),
            Online = feed?.StreamOnline,
            Title = feed?.NowPlaying?.Title,
            Artist = feed?.NowPlaying?.Artist,
            Album = feed?.NowPlaying?.Album,
            TrackLine = _state.TrackLine(),
            Description = _state.Description() ?? JellywaveChannel.DefaultOverview,
            Dj = feed?.Dj?.Name,
            DjTagline = feed?.Dj?.Tagline,
            Show = feed?.ActiveShow?.Name,
            NextShow = next?.Name,
            NextShowPresenter = next?.Presenter,
            NextShowStartsAt = next?.StartsAt,
            Listeners = feed?.Listeners?.Current,
            FetchedUtc = feed is null ? null : _state.FetchedUtc
        };

        var channel = _finder.FindChannel(config);
        response.ChannelId = channel?.Id;
        var item = channel is null ? null : _finder.FindLiveItem(channel);
        if (item is not null)
        {
            response.ItemId = item.Id;
            var image = item.GetImageInfo(ImageType.Primary, 0);
            response.ImageTag = image is null ? null : _imageProcessor.GetImageCacheTag(item, image);
        }

        return response;
    }

    /// <summary>
    /// Submits a listener request to the station on behalf of the signed-in user. The station's own limits apply
    /// (a wait between requests and a few per hour, shared by everyone on this server, since the station sees one caller).
    /// </summary>
    /// <param name="submission">The text and name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The receipt or refusal; always 200 so the page can show the station's words.</returns>
    [HttpPost("Request")]
    [Authorize]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<RequestOutcome>> SubmitRequest([FromBody] RequestSubmission submission, CancellationToken cancellationToken)
    {
        var text = Collapse(submission?.Text);
        var name = Collapse(submission?.Name);
        if (text.Length == 0)
        {
            return new RequestOutcome { Status = "refused", Message = "Type something first." };
        }

        if (text.Length > MaxRequestText)
        {
            return new RequestOutcome { Status = "refused", Message = "Keep it under " + MaxRequestText + " characters." };
        }

        if (name.Length > MaxRequestName)
        {
            name = name[..MaxRequestName];
        }

        var config = Plugin.Instance?.Configuration;
        if (config is null || !StationUrls.TryGetBaseUrl(config, out var baseUrl) || baseUrl is null)
        {
            return new RequestOutcome { Status = "unavailable", Message = "The station is not configured." };
        }

        try
        {
            var reply = await _stationClient.SubmitRequestAsync(baseUrl, text, name, cancellationToken).ConfigureAwait(false);
            var body = reply.Body;
            if (reply.StatusCode == StatusCodes.Status202Accepted && body?.RequestId is not null)
            {
                return new RequestOutcome { Accepted = true, RequestId = body.RequestId, Status = "pending", Message = "Sent. Waiting for the DJ…" };
            }

            return new RequestOutcome
            {
                Status = reply.StatusCode == StatusCodes.Status503ServiceUnavailable ? "unavailable" : "refused",
                Message = body?.Message ?? (reply.StatusCode == StatusCodes.Status429TooManyRequests ? "Too many requests right now; try again in a minute." : "The station did not take the request."),
                RetryAfterSeconds = body?.RetryAfter
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Request to the station failed: {Message}", ex.Message);
            return new RequestOutcome { Status = "unavailable", Message = "The station did not answer." };
        }
    }

    /// <summary>
    /// Polls the outcome of a submitted request.
    /// </summary>
    /// <param name="requestId">The id from the receipt.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome so far.</returns>
    [HttpGet("Request/{requestId}")]
    [Authorize]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<RequestOutcome>> RequestStatus([FromRoute] string requestId, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !StationUrls.TryGetBaseUrl(config, out var baseUrl) || baseUrl is null)
        {
            return new RequestOutcome { Status = "unavailable", Message = "The station is not configured." };
        }

        try
        {
            var reply = await _stationClient.GetRequestStatusAsync(baseUrl, requestId, cancellationToken).ConfigureAwait(false);
            var body = reply.Body;
            if (reply.StatusCode == StatusCodes.Status404NotFound || body is null)
            {
                return new RequestOutcome { RequestId = requestId, Status = "unknown", Message = "The station lost track of that request." };
            }

            var track = body.Track is null ? null : string.Join(" – ", new[] { body.Track.Artist, body.Track.Title }.Where(p => !string.IsNullOrWhiteSpace(p)));
            return new RequestOutcome
            {
                RequestId = requestId,
                Status = body.Status ?? "pending",
                Outcome = body.Outcome,
                Message = body.Message,
                Ack = body.Ack,
                Track = string.IsNullOrEmpty(track) ? null : track,
                QueuePosition = body.QueuePosition
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Request status from the station failed: {Message}", ex.Message);
            return new RequestOutcome { RequestId = requestId, Status = "unavailable", Message = "The station did not answer." };
        }
    }

    /// <summary>
    /// Whether the station page is injected into the web client (configuration page).
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("WebUi")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Produces("application/json")]
    public ActionResult<WebUiStatus> WebUi() => WebUiStartupTask.Status;

    private static string Collapse(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private IActionResult Asset(string resource, string contentType)
    {
        var stream = typeof(JellywaveWebController).Assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return NotFound();
        }

        var etag = new EntityTagHeaderValue("\"" + WebUiTransforms.AssetVersion() + "\"");
        Response.Headers[HeaderNames.CacheControl] = "public, max-age=" + (int)AssetCacheLifetime.TotalSeconds;
        return File(stream, contentType, lastModified: null, entityTag: etag);
    }
}
