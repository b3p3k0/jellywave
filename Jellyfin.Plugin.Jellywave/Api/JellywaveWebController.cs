using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using Jellyfin.Plugin.Jellywave.Web;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// Endpoints behind the station page: the injected assets (anonymous, like the web client itself), the station
/// routes (signed-in users who can see the channel, see <see cref="StationAccessAttribute"/>; API keys are refused)
/// and the injection status (administrators, for the configuration page).
/// </summary>
[ApiController]
[Route("Jellywave")]
public class JellywaveWebController : ControllerBase
{
    private const int MaxRequestText = 280;
    private const int MaxRequestName = 40;
    private const int MaxRecent = 8;
    private static readonly TimeSpan AssetCacheLifetime = TimeSpan.FromDays(1);
    private static readonly TimeSpan AvatarCacheLifetime = TimeSpan.FromHours(1);

    private readonly NowPlayingState _state;
    private readonly LiveItemFinder _finder;
    private readonly IImageProcessor _imageProcessor;
    private readonly StationClient _stationClient;
    private readonly ScheduleCache _schedule;
    private readonly RequestReceipts _receipts;
    private readonly ILogger<JellywaveWebController> _logger;
    private readonly Func<PluginConfiguration?> _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellywaveWebController"/> class (DI constructor).
    /// </summary>
    /// <param name="state">The shared now-playing state.</param>
    /// <param name="finder">The live item finder.</param>
    /// <param name="imageProcessor">The image processor, for image tags.</param>
    /// <param name="stationClient">The station client, for requests.</param>
    /// <param name="schedule">The schedule cache.</param>
    /// <param name="receipts">Which user owns which listener request.</param>
    /// <param name="logger">The logger.</param>
    public JellywaveWebController(NowPlayingState state, LiveItemFinder finder, IImageProcessor imageProcessor, StationClient stationClient, ScheduleCache schedule, RequestReceipts receipts, ILogger<JellywaveWebController> logger)
        : this(state, finder, imageProcessor, stationClient, schedule, receipts, logger, () => Plugin.Instance?.Configuration)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JellywaveWebController"/> class with an explicit configuration source (tests).
    /// </summary>
    /// <param name="state">The shared now-playing state.</param>
    /// <param name="finder">The live item finder.</param>
    /// <param name="imageProcessor">The image processor, for image tags.</param>
    /// <param name="stationClient">The station client, for requests.</param>
    /// <param name="schedule">The schedule cache.</param>
    /// <param name="receipts">Which user owns which listener request.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">Returns the current configuration, or null when the plugin is not ready.</param>
    internal JellywaveWebController(NowPlayingState state, LiveItemFinder finder, IImageProcessor imageProcessor, StationClient stationClient, ScheduleCache schedule, RequestReceipts receipts, ILogger<JellywaveWebController> logger, Func<PluginConfiguration?> configuration)
    {
        _state = state;
        _finder = finder;
        _imageProcessor = imageProcessor;
        _stationClient = stationClient;
        _schedule = schedule;
        _receipts = receipts;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// The injected script. Anonymous: it is part of the web client's page load, before sign-in.
    /// </summary>
    /// <returns>The script.</returns>
    [HttpGet("ui.js")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    public IActionResult Script() => Asset(WebUiTransforms.ScriptResource, "text/javascript");

    /// <summary>
    /// The injected stylesheet.
    /// </summary>
    /// <returns>The stylesheet.</returns>
    [HttpGet("ui.css")]
    [AllowAnonymous]
    [Produces("text/css")]
    public IActionResult Style() => Asset(WebUiTransforms.StyleResource, "text/css");

    /// <summary>
    /// What is on air, for the station page (users who can see the channel).
    /// </summary>
    /// <returns>The feed.</returns>
    [HttpGet("Feed")]
    [Authorize]
    [StationAccess]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<FeedResponse> Feed()
    {
        var config = _configuration() ?? new PluginConfiguration();
        var feed = _state.Feed();
        var stationState = _state.StationState();
        var next = feed?.Context?.ShowHandover?.NextShow;
        var upcoming = stationState?.Upcoming;
        var upNext = upcoming is { Count: > 0 } ? upcoming[0] : null;
        var response = new FeedResponse
        {
            SongId = _state.CoverId(),
            DjAvatarId = AvatarIdOf(feed?.Dj?.Avatar),
            UpNext = TrackLineOf(upNext),
            Recent = (stationState?.History ?? []).Take(MaxRecent).Select(t => TrackLineOf(t) + (string.IsNullOrWhiteSpace(t.RequestedBy) ? string.Empty : " · requested by " + t.RequestedBy.Trim())).Where(l => l.Length > 0).ToList(),
            LyricsEnabled = stationState?.Lyrics?.Enabled == true,
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

        var channel = CallerChannel();
        response.ChannelId = channel.Id;
        var item = _finder.FindLiveItem(channel);
        if (item is not null)
        {
            response.ItemId = item.Id;
            var image = item.GetImageInfo(ImageType.Primary, 0);
            response.ImageTag = image is null ? null : _imageProcessor.GetImageCacheTag(item, image);
        }

        return response;
    }

    /// <summary>
    /// The DJ's portrait, proxied from the station (users who can see the channel). A transparent PNG when the persona has none.
    /// </summary>
    /// <param name="personaId">The persona id from the feed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The image.</returns>
    [HttpGet("Avatar/{personaId}")]
    [Authorize]
    [StationAccess]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Avatar([FromRoute] string personaId, CancellationToken cancellationToken)
    {
        var config = _configuration();
        if (config is null || !StationUrls.TryGetBaseUrl(config, out var baseUrl) || baseUrl is null || !StationClient.IsValidId(personaId))
        {
            return NotFound();
        }

        try
        {
            var image = await _stationClient.GetAvatarAsync(baseUrl, personaId, cancellationToken).ConfigureAwait(false);
            if (image is null)
            {
                return NotFound();
            }

            Response.Headers[HeaderNames.CacheControl] = "private, max-age=" + (int)AvatarCacheLifetime.TotalSeconds;
            return File(image.OpenStream(), image.MimeType);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug("Avatar fetch failed: {Message}", ex.Message);
            return NotFound();
        }
    }

    /// <summary>
    /// Like status of the track on air (users who can see the channel).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status.</returns>
    [HttpGet("Like")]
    [Authorize]
    [StationAccess]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LikeOutcome>> LikeStatus(CancellationToken cancellationToken)
    {
        var config = _configuration();
        if (config is null || !StationUrls.TryGetBaseUrl(config, out var baseUrl) || baseUrl is null)
        {
            return new LikeOutcome { Enabled = false };
        }

        try
        {
            return MapLike(await _stationClient.GetLikeAsync(baseUrl, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug("Like status failed: {Message}", ex.Message);
            return new LikeOutcome { Enabled = false };
        }
    }

    /// <summary>
    /// Likes the track on air (users who can see the channel). The station counts this server as one caller.
    /// </summary>
    /// <param name="submission">The song id the listener saw.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    [HttpPost("Like")]
    [Authorize]
    [StationAccess]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LikeOutcome>> Like([FromBody] LikeSubmission submission, CancellationToken cancellationToken)
    {
        var songId = submission?.SongId?.Trim() ?? string.Empty;
        var config = _configuration();
        if (config is null || !StationUrls.TryGetBaseUrl(config, out var baseUrl) || baseUrl is null || !StationClient.IsValidId(songId))
        {
            return new LikeOutcome { Enabled = false, Message = "Nothing to like right now." };
        }

        try
        {
            return MapLike(await _stationClient.PostLikeAsync(baseUrl, songId, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug("Like failed: {Message}", ex.Message);
            return new LikeOutcome { Enabled = true, Message = "The station did not answer." };
        }
    }

    /// <summary>
    /// Today's programme in the station's time zone (users who can see the channel; cached a few minutes server-side).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The blocks.</returns>
    [HttpGet("Schedule")]
    [Authorize]
    [StationAccess]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ScheduleResponse>> Schedule(CancellationToken cancellationToken)
    {
        var config = _configuration();
        if (config is null)
        {
            return new ScheduleResponse();
        }

        ScheduleInfo? info;
        try
        {
            info = await _schedule.GetAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            _logger.LogDebug("Schedule fetch failed: {Message}", ex.Message);
            return new ScheduleResponse();
        }

        var feed = _state.Feed();
        return info is null ? new ScheduleResponse() : BuildToday(info, DateTime.UtcNow, feed?.Context?.ActiveShow?.Id, feed?.Context?.ActiveShow?.Name ?? feed?.ActiveShow?.Name);
    }

    /// <summary>
    /// Lyrics of the track on air (users who can see the channel).
    /// </summary>
    /// <param name="songId">The song id from the feed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The lines, or why there are none.</returns>
    [HttpGet("Lyrics")]
    [Authorize]
    [StationAccess]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LyricsResponse>> Lyrics([FromQuery] string songId, CancellationToken cancellationToken)
    {
        var config = _configuration();
        if (config is null || !StationUrls.TryGetBaseUrl(config, out var baseUrl) || baseUrl is null || !StationClient.IsValidId(songId))
        {
            return new LyricsResponse { SongId = songId, Status = "unavailable", Message = "No track to look up." };
        }

        try
        {
            var reply = await _stationClient.GetLyricsAsync(baseUrl, songId, cancellationToken).ConfigureAwait(false);
            var body = reply.Body;
            switch (reply.StatusCode)
            {
                case StatusCodes.Status403Forbidden:
                    return new LyricsResponse { SongId = songId, Status = "off", Message = "The station has lyrics switched off." };
                case StatusCodes.Status401Unauthorized:
                    return new LyricsResponse { SongId = songId, Status = "unavailable", Message = "The station is private; lyrics need its password." };
                case StatusCodes.Status409Conflict:
                    return new LyricsResponse { SongId = songId, Status = "ended", Message = "That track is no longer on air." };
                case StatusCodes.Status200OK when body is not null:
                    var lines = (body.Lines ?? []).Select(l => Collapse(l.Text)).Where(t => t.Length > 0).ToList();
                    if (lines.Count == 0)
                    {
                        return new LyricsResponse { SongId = songId, Status = body.Pending == true ? "pending" : "none", Message = body.Pending == true ? "Still looking…" : "No lyrics for this one." };
                    }

                    return new LyricsResponse { SongId = songId, Status = "ok", Synced = body.Synced == true, Lines = lines };
                default:
                    return new LyricsResponse { SongId = songId, Status = "unavailable", Message = body?.Error ?? "The station could not get lyrics right now." };
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug("Lyrics fetch failed: {Message}", ex.Message);
            return new LyricsResponse { SongId = songId, Status = "unavailable", Message = "The station did not answer." };
        }
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
    [StationAccess]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RequestOutcome>> SubmitRequest([FromBody] RequestSubmission submission, CancellationToken cancellationToken)
    {
        var caller = CallerId();
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

        var config = _configuration();
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
                // One line per accepted request, never the text or the name: ties the station's id to the Jellyfin
                // user, which the station cannot do (it sees this server as one caller). The receipt lets only that
                // user poll the outcome.
                _receipts.Record(body.RequestId, caller);
                _logger.LogInformation("Listener request {RequestId} accepted for user {UserId}", body.RequestId, caller);
                return new RequestOutcome { Accepted = true, RequestId = body.RequestId, Status = "pending", Message = "Sent. Waiting for the DJ…" };
            }

            if (reply.StatusCode is >= StatusCodes.Status200OK and < StatusCodes.Status300MultipleChoices)
            {
                // A success without a receipt is not something today's station sends; treat it as a failure, not a refusal.
                return new RequestOutcome { Status = "unavailable", Message = "The station answered without a receipt." };
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
    /// Polls the outcome of a submitted request. Only the user who submitted it may poll it (403 for anyone else);
    /// an id with no live receipt here is "unknown" without asking the station.
    /// </summary>
    /// <param name="requestId">The id from the receipt.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome so far.</returns>
    [HttpGet("Request/{requestId}")]
    [Authorize]
    [StationAccess]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RequestOutcome>> RequestStatus([FromRoute] string requestId, CancellationToken cancellationToken)
    {
        switch (_receipts.Check(requestId, CallerId()))
        {
            case ReceiptOwnership.Owner:
                break;
            case ReceiptOwnership.OtherUser:
                _logger.LogDebug("Request status refused: request {RequestId} belongs to another user", requestId);
                return StatusCode(StatusCodes.Status403Forbidden);
            case ReceiptOwnership.Unknown:
                return LostTrack(requestId);
            default:
                throw new InvalidOperationException("Unknown receipt ownership");
        }

        var config = _configuration();
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
                return LostTrack(requestId);
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

    /// <summary>
    /// Turns the weekly grid into today's blocks in the station's time zone. Internal for tests.
    /// </summary>
    /// <param name="info">The schedule.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <param name="activeShowId">The id of the show the station says is on air (honours its timed takeovers), or null when unknown.</param>
    /// <param name="activeShowName">That show's name, for the takeover line.</param>
    /// <returns>The response.</returns>
    internal static ScheduleResponse BuildToday(ScheduleInfo info, DateTime nowUtc, string? activeShowId = null, string? activeShowName = null)
    {
        TimeZoneInfo zone;
        try
        {
            zone = string.IsNullOrWhiteSpace(info.Timezone) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(info.Timezone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        var dayKey = ((int)local.DayOfWeek).ToString(System.Globalization.CultureInfo.InvariantCulture);   // Sunday = 0, as the station's grid
        var response = new ScheduleResponse { Timezone = info.Timezone, Day = local.DayOfWeek.ToString(), Hour = local.Hour };
        if (info.Schedule is null || !info.Schedule.TryGetValue(dayKey, out var hours) || hours is null)
        {
            return response;
        }

        var shows = (info.Shows ?? []).Where(s => s.Id is not null).ToDictionary(s => s.Id!, s => s);
        var personas = (info.Personas ?? []).Where(p => p.Id is not null).ToDictionary(p => p.Id!, p => p.Name);
        var blocks = new List<ScheduleBlock>();
        ScheduleBlock? open = null;
        string? openId = null;
        for (var hour = 0; hour < Math.Min(hours.Count, 24); hour++)
        {
            var id = hours[hour];
            if (open is not null && string.Equals(id, openId, StringComparison.Ordinal))
            {
                open.EndHour = hour + 1;
                continue;
            }

            open = null;
            openId = null;
            if (id is null || !shows.TryGetValue(id, out var show))
            {
                continue;
            }

            open = new ScheduleBlock
            {
                ShowId = id,
                StartHour = hour,
                EndHour = hour + 1,
                Show = Collapse(show.Name),
                Host = show.PersonaId is not null && personas.TryGetValue(show.PersonaId, out var host) ? Collapse(host) : null
            };
            openId = id;
            blocks.Add(open);
        }

        // "Now" is the block around the station's clock, unless the station says a show with another id is on
        // air (a timed takeover outranks the grid); then no grid block is current and the takeover is named.
        var activeId = activeShowId?.Trim() ?? string.Empty;
        foreach (var block in blocks)
        {
            block.Now = local.Hour >= block.StartHour && local.Hour < block.EndHour
                && (activeId.Length == 0 || string.Equals(activeId, block.ShowId, StringComparison.Ordinal));
        }

        if (activeId.Length > 0 && !blocks.Exists(b => b.Now))
        {
            var name = Collapse(activeShowName);
            response.Takeover = name.Length > 0 ? name : activeId;
        }

        response.Available = true;
        response.Today = blocks;
        return response;
    }

    private static RequestOutcome LostTrack(string requestId)
        => new() { RequestId = requestId, Status = "unknown", Message = "The station lost track of that request." };

    private static LikeOutcome MapLike(StationReply<LikeStatus> reply)
    {
        var body = reply.Body;
        if (reply.StatusCode == StatusCodes.Status403Forbidden || body?.Enabled == false)
        {
            return new LikeOutcome { Enabled = false };
        }

        return new LikeOutcome
        {
            Enabled = true,
            SongId = StationClient.IsValidId(body?.SongId) ? body!.SongId : null,
            Liked = body?.Liked == true,
            Count = body?.Count ?? 0,
            Message = reply.StatusCode >= StatusCodes.Status400BadRequest ? (body?.Error ?? "The station did not take the like.") : null,
            RetryAfterSeconds = body?.RetryAfter
        };
    }

    /// <summary>
    /// The persona id at the end of the feed's avatar path (<c>/persona-avatar/p_8754e4</c>), when it is a safe id.
    /// </summary>
    private static string? AvatarIdOf(string? avatarPath)
    {
        if (string.IsNullOrWhiteSpace(avatarPath))
        {
            return null;
        }

        var id = avatarPath.Trim().TrimEnd('/');
        id = id[(id.LastIndexOf('/') + 1)..];
        return StationClient.IsValidId(id) ? id : null;
    }

    private static string TrackLineOf(QueueTrack? track)
    {
        if (track is null)
        {
            return string.Empty;
        }

        return string.Join(" – ", new[] { track.Artist, track.Title }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Collapse(p)));
    }

    private static string Collapse(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The caller's user id, left by <see cref="StationAccessAttribute"/>. Throws when the filter did not run,
    /// so an action that forgot the attribute fails instead of serving an unchecked caller.
    /// </summary>
    private Guid CallerId()
        => HttpContext.Items.TryGetValue(StationAccess.UserIdItem, out var value) && value is Guid id
            ? id
            : throw new InvalidOperationException("The station access filter did not run for this action.");

    /// <summary>
    /// The channel entity <see cref="StationAccessAttribute"/> resolved for this request. Throws when the filter did not run.
    /// </summary>
    private Channel CallerChannel()
        => HttpContext.Items.TryGetValue(StationAccess.ChannelItem, out var value) && value is Channel channel
            ? channel
            : throw new InvalidOperationException("The station access filter did not run for this action.");

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
