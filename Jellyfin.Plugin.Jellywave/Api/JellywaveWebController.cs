using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Services;
using Jellyfin.Plugin.Jellywave.Web;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
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
/// and the injection status (administrators, for the configuration page). Each station route serves one listed SUB/WAVE
/// station, found by <see cref="StationRouteAttribute"/> (card L5) and read here only through <see cref="CallerStation"/>.
/// The title route (card L6) serves one listed plain stream instead, found by the same filter's plain-stream variant and
/// read only through <see cref="CallerPlainStation"/>, which holds its id alone.
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
    private readonly ScheduleCache _schedule;
    private readonly RequestReceipts _receipts;
    private readonly StationList _stations;
    private readonly StationTitles _titles;
    private readonly StationCalls _calls;
    private readonly ILogger<JellywaveWebController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellywaveWebController"/> class.
    /// </summary>
    /// <param name="state">The shared now-playing state.</param>
    /// <param name="finder">The live item finder.</param>
    /// <param name="schedule">The schedule cache.</param>
    /// <param name="receipts">Which user sent which listener request to which station.</param>
    /// <param name="stations">The station list and image tags: ids, names, tags and header fields, never a station or a
    /// stream URL (the routes' own station comes from <see cref="StationRouteAttribute"/>).</param>
    /// <param name="titles">The plain streams' sampled titles, read only (card L6).</param>
    /// <param name="calls">Card R2a: every user-driven station call, under the per-user budgets and the shared limiter.</param>
    /// <param name="logger">The logger.</param>
    public JellywaveWebController(NowPlayingState state, LiveItemFinder finder, ScheduleCache schedule, RequestReceipts receipts, StationList stations, StationTitles titles, StationCalls calls, ILogger<JellywaveWebController> logger)
    {
        _state = state;
        _finder = finder;
        _schedule = schedule;
        _receipts = receipts;
        _stations = stations;
        _titles = titles;
        _calls = calls;
        _logger = logger;
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
    /// Every listed station for the station page, in grid order with its kind (users who can see the channel; card L8):
    /// ids, names, item ids, image tags and the header fields. Never a stream or station URL; a plain stream's website is
    /// the one link. The SUB/WAVE station routes still refuse a plain stream's id (404).
    /// </summary>
    /// <returns>The channel and its stations.</returns>
    [HttpGet("Stations")]
    [Authorize]
    [StationAccess]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<StationsResponse> Stations()
    {
        var channel = CallerChannel();
        return new StationsResponse { ChannelId = channel.Id, ChannelName = channel.Name ?? string.Empty, Stations = _stations.Entries(channel) };
    }

    /// <summary>
    /// What is on air on one station, for the station page (users who can see the channel).
    /// </summary>
    /// <param name="stationId">The station, from the station list; absent means the primary station. Read by <see cref="StationRouteAttribute"/>.</param>
    /// <returns>The feed.</returns>
    [HttpGet("Feed")]
    [Authorize]
    [StationAccess]
    [StationRoute]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<FeedResponse> Feed([FromQuery] string? stationId)
    {
        // The station's own state (card L4). Until the poller has heard its current origin (its base URL changed), the
        // snapshot says nothing; OtherOrigin says the item may still carry the previous origin's cover.
        var station = CallerStation();
        var heard = _state.Snapshot(KeyOf(station));
        var feed = heard.Feed;
        var stationState = heard.StationState;
        var next = feed?.Context?.ShowHandover?.NextShow;
        var upcoming = stationState?.Upcoming;
        var upNext = upcoming is { Count: > 0 } ? upcoming[0] : null;
        var response = new FeedResponse
        {
            SongId = heard.CoverId,
            DjAvatarId = AvatarIdOf(feed?.Dj?.Avatar),
            UpNext = TrackLineOf(upNext),
            Recent = (stationState?.History ?? []).Take(MaxRecent).Select(t => TrackLineOf(t) + (string.IsNullOrWhiteSpace(t?.RequestedBy) ? string.Empty : " · requested by " + t.RequestedBy.Trim())).Where(l => l.Length > 0).ToList(),
            LyricsEnabled = stationState?.Lyrics?.Enabled == true,
            StationId = KeyOf(station).StationId,
            StationName = NameOf(station),
            Online = feed?.StreamOnline,
            Title = feed?.NowPlaying?.Title,
            Artist = feed?.NowPlaying?.Artist,
            Album = feed?.NowPlaying?.Album,
            TrackLine = heard.TrackLine,
            Description = heard.Description ?? JellywaveChannel.DefaultOverview,
            Dj = feed?.Dj?.Name,
            DjTagline = feed?.Dj?.Tagline,
            Show = feed?.ActiveShow?.Name,
            NextShow = next?.Name,
            NextShowPresenter = next?.Presenter,
            NextShowStartsAt = next?.StartsAt,
            Listeners = feed?.Listeners?.Current,
            FetchedUtc = feed is null ? null : heard.FetchedUtc
        };

        var channel = CallerChannel();
        response.ChannelId = channel.Id;
        var item = _finder.FindLiveItem(channel, KeyOf(station).StationId);
        if (item is not null)
        {
            response.ItemId = item.Id;
            response.ImageTag = _stations.ImageTag(item, KeyOf(station), heard);
        }

        return response;
    }

    /// <summary>
    /// The current title of one plain stream, for its header's "Now:" line (users who can see the channel; card L6). Reads
    /// what the title sampler last recorded and never samples itself. Never a URL.
    /// </summary>
    /// <param name="stationId">A listed plain stream, from the station list; required. Read by <see cref="StationRouteAttribute"/>
    /// (SUB/WAVE, disabled and unknown ids are 404).</param>
    /// <returns>The title state.</returns>
    [HttpGet("Title")]
    [Authorize]
    [StationAccess]
    [StationRoute(PlainStream = true)]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<TitleResponse> Title([FromQuery] string? stationId)
    {
        var station = CallerPlainStation();
        var heard = _titles.Snapshot(station.StationId);
        return new TitleResponse { StationId = station.StationId, State = heard.State, Title = heard.Title, UpdatedUtc = heard.LastGoodUtc };
    }

    /// <summary>
    /// The DJ's portrait, proxied from the station (users who can see the channel). A transparent PNG when the persona has none.
    /// </summary>
    /// <param name="personaId">The persona id from the feed.</param>
    /// <param name="stationId">The station, from the station list; absent means the primary station. Read by <see cref="StationRouteAttribute"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The image.</returns>
    [HttpGet("Avatar/{personaId}")]
    [Authorize]
    [StationAccess]
    [StationRoute]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Avatar([FromRoute] string personaId, [FromQuery] string? stationId, CancellationToken cancellationToken)
    {
        var station = CallerStation();
        if (!StationClient.IsValidId(personaId))
        {
            return NotFound();
        }

        var call = await _calls.GetAvatarAsync(CallerId(), KeyOf(station), BaseUrlOf(station), personaId, cancellationToken).ConfigureAwait(false);
        if (call.Refusal != 0)
        {
            return Refused(call.Refusal, call.RetryAfter);
        }

        var image = call.Value;
        if (image is null)
        {
            return NotFound();
        }

        Response.Headers[HeaderNames.CacheControl] = "private, max-age=" + (int)AvatarCacheLifetime.TotalSeconds;
        return File(image.OpenStream(), image.MimeType);
    }

    /// <summary>
    /// Like status of the track on air on one station (users who can see the channel).
    /// </summary>
    /// <param name="stationId">The station, from the station list; absent means the primary station. Read by <see cref="StationRouteAttribute"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status.</returns>
    [HttpGet("Like")]
    [Authorize]
    [StationAccess]
    [StationRoute]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<LikeOutcome>> LikeStatus([FromQuery] string? stationId, CancellationToken cancellationToken)
    {
        var station = CallerStation();
        // Card R2a: kept per song on air, so the poller's song id names it.
        var call = await _calls.GetLikeAsync(CallerId(), KeyOf(station), BaseUrlOf(station), _state.Snapshot(KeyOf(station)).CoverId, cancellationToken).ConfigureAwait(false);
        if (call.Refusal != 0)
        {
            return Refused(call.Refusal, call.RetryAfter);
        }

        return call.Value is { } reply ? MapLike(reply) : new LikeOutcome { Enabled = false };
    }

    /// <summary>
    /// Likes the track on air (users who can see the channel). The station counts this server as one caller.
    /// </summary>
    /// <param name="submission">The song id the listener saw and the station (absent: the primary station; read by <see cref="StationRouteAttribute"/>).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    [HttpPost("Like")]
    [Authorize]
    [StationAccess]
    [StationRoute]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<LikeOutcome>> Like([FromBody] LikeSubmission submission, CancellationToken cancellationToken)
    {
        var station = CallerStation();
        var songId = submission?.SongId?.Trim() ?? string.Empty;
        if (!StationClient.IsValidId(songId))
        {
            return new LikeOutcome { Enabled = false, Message = "Nothing to like right now." };
        }

        var call = await _calls.PostLikeAsync(CallerId(), KeyOf(station), BaseUrlOf(station), songId, cancellationToken).ConfigureAwait(false);
        if (call.Refusal != 0)
        {
            return Refused(call.Refusal, call.RetryAfter);
        }

        return call.Value is { } reply ? MapLike(reply) : new LikeOutcome { Enabled = true, Message = "The station did not answer." };
    }

    /// <summary>
    /// Today's programme on one station in its time zone (users who can see the channel; cached a few minutes server-side).
    /// </summary>
    /// <param name="stationId">The station, from the station list; absent means the primary station. Read by <see cref="StationRouteAttribute"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The blocks.</returns>
    [HttpGet("Schedule")]
    [Authorize]
    [StationAccess]
    [StationRoute]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ScheduleResponse>> Schedule([FromQuery] string? stationId, CancellationToken cancellationToken)
    {
        var station = CallerStation();
        var key = KeyOf(station);
        ScheduleInfo? info;
        try
        {
            info = await _schedule.GetAsync(key.StationId, BaseUrlOf(station), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            _logger.LogDebug("Schedule fetch failed: {Message}", ex.Message);
            return new ScheduleResponse();
        }

        var feed = _state.Snapshot(key).Feed;
        return info is null ? new ScheduleResponse() : BuildToday(info, DateTime.UtcNow, feed?.Context?.ActiveShow?.Id, feed?.Context?.ActiveShow?.Name ?? feed?.ActiveShow?.Name);
    }

    /// <summary>
    /// Lyrics of the track on air on one station (users who can see the channel).
    /// </summary>
    /// <param name="songId">The song id from that station's feed.</param>
    /// <param name="stationId">The station, from the station list; absent means the primary station. Read by <see cref="StationRouteAttribute"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The lines, or why there are none.</returns>
    [HttpGet("Lyrics")]
    [Authorize]
    [StationAccess]
    [StationRoute]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<LyricsResponse>> Lyrics([FromQuery] string songId, [FromQuery] string? stationId, CancellationToken cancellationToken)
    {
        var station = CallerStation();
        if (!StationClient.IsValidId(songId))
        {
            return new LyricsResponse { SongId = songId, Status = "unavailable", Message = "No track to look up." };
        }

        var call = await _calls.GetLyricsAsync(CallerId(), KeyOf(station), BaseUrlOf(station), songId, cancellationToken).ConfigureAwait(false);
        if (call.Refusal != 0)
        {
            return Refused(call.Refusal, call.RetryAfter);
        }

        if (call.Value is not { } reply)
        {
            return new LyricsResponse { SongId = songId, Status = "unavailable", Message = "The station did not answer." };
        }

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
                var lines = (body.Lines ?? []).Select(l => Collapse(l?.Text)).Where(t => t.Length > 0).ToList();
                if (lines.Count == 0)
                {
                    return new LyricsResponse { SongId = songId, Status = body.Pending == true ? "pending" : "none", Message = body.Pending == true ? "Still looking…" : "No lyrics for this one." };
                }

                return new LyricsResponse { SongId = songId, Status = "ok", Synced = body.Synced == true, Lines = lines };
            default:
                return new LyricsResponse { SongId = songId, Status = "unavailable", Message = body?.Error ?? "The station could not get lyrics right now." };
        }
    }

    /// <summary>
    /// Submits a listener request to the station on behalf of the signed-in user. The station's own limits apply
    /// (a wait between requests and a few per hour, shared by everyone on this server, since the station sees one caller).
    /// </summary>
    /// <param name="submission">The text, the name and the station (absent: the primary station; read by <see cref="StationRouteAttribute"/>).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The receipt or the station's refusal, 200 so the page can show the station's words; card R2a: 429 (over the
    /// caller's request or call budget) or 503 (the receipt store is full and the caller owns no receipt, or too many station
    /// calls in flight) with <c>Retry-After</c> when this server refuses before asking the station.</returns>
    [HttpPost("Request")]
    [Authorize]
    [StationAccess]
    [StationRoute]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<RequestOutcome>> SubmitRequest([FromBody] RequestSubmission submission, CancellationToken cancellationToken)
    {
        var caller = CallerId();
        var station = CallerStation();
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

        // Card R2a: the caller's budget first, then a receipt slot (reserved through the callback, so an over-budget send
        // never evicts the caller's own oldest receipt), so a full store refuses before the station is asked.
        string? receipt = null;
        var recorded = false;
        try
        {
            var call = await _calls.SubmitRequestAsync(caller, KeyOf(station), BaseUrlOf(station), text, name, () => (receipt = _receipts.Reserve(caller)) is not null, cancellationToken).ConfigureAwait(false);
            if (call.Refusal != 0)
            {
                return Refused(call.Refusal, call.RetryAfter);
            }

            if (call.Value is not { } reply)
            {
                return new RequestOutcome { Status = "unavailable", Message = "The station did not answer." };
            }

            var body = reply.Body;
            if (reply.StatusCode == StatusCodes.Status202Accepted && StationClient.IsValidId(body?.RequestId))
            {
                // One line per accepted request, never the text or the name: ties the station's id (and the receipt token
                // the page holds instead) to the Jellyfin user, which the station cannot do (it sees this server as one
                // caller). The receipt lets only that user poll the outcome, and only at this station.
                recorded = _receipts.Complete(receipt!, caller, KeyOf(station), body!.RequestId!);
                if (recorded)
                {
                    _logger.LogInformation("Listener request {RequestId} (receipt {Receipt}) accepted for user {UserId} at station {StationId}", body.RequestId, receipt, caller, KeyOf(station).StationId);
                    return new RequestOutcome { Accepted = true, RequestId = receipt, Status = "pending", Message = "Sent. Waiting for the DJ…" };
                }

                _logger.LogWarning("Listener request {RequestId} accepted for user {UserId} at station {StationId}, but its receipt slot was gone", body.RequestId, caller, KeyOf(station).StationId);
                return new RequestOutcome { Status = "unavailable", Message = "The station took the request, but its progress cannot be followed." };
            }

            if (reply.StatusCode is >= StatusCodes.Status200OK and < StatusCodes.Status300MultipleChoices)
            {
                // A success without a usable receipt is not something today's station sends; treat it as a failure, not a refusal.
                return new RequestOutcome { Status = "unavailable", Message = "The station answered without a receipt." };
            }

            return new RequestOutcome
            {
                Status = reply.StatusCode == StatusCodes.Status503ServiceUnavailable ? "unavailable" : "refused",
                Message = body?.Message ?? (reply.StatusCode == StatusCodes.Status429TooManyRequests ? "Too many requests right now; try again in a minute." : "The station did not take the request."),
                RetryAfterSeconds = body?.RetryAfter
            };
        }
        finally
        {
            if (!recorded && receipt is not null)
            {
                _receipts.Cancel(receipt);
            }
        }
    }

    /// <summary>
    /// Polls the outcome of a submitted request at the station it went to (the receipt's, never one the caller names).
    /// <see cref="StationRouteAttribute"/> answers first: 403 for another user's request, "unknown" without asking any
    /// station when there is no live receipt here or its station is no longer listed.
    /// </summary>
    /// <param name="requestId">The receipt token the request route answered with (card R2a: never the station's own id).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome so far.</returns>
    [HttpGet("Request/{requestId}")]
    [Authorize]
    [StationAccess]
    [StationRoute(FromReceipt = true)]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<RequestOutcome>> RequestStatus([FromRoute] string requestId, CancellationToken cancellationToken)
    {
        var station = CallerStation();
        var stationRequestId = _receipts.StationRequestIdOf(requestId, CallerId(), KeyOf(station));
        if (stationRequestId is null)
        {
            // The filter found the receipt a moment ago; it expired or was evicted since.
            return RequestOutcome.LostTrack(requestId);
        }

        var call = await _calls.GetRequestStatusAsync(CallerId(), KeyOf(station), BaseUrlOf(station), requestId, stationRequestId, cancellationToken).ConfigureAwait(false);
        if (call.Refusal != 0)
        {
            return Refused(call.Refusal, call.RetryAfter);
        }

        if (call.Value is not { } reply || reply.StatusCode is >= StatusCodes.Status300MultipleChoices and < StatusCodes.Status400BadRequest)
        {
            // No answer, or a redirect (not followed): the receipt stays, so a later poll can still reach the station.
            return new RequestOutcome { RequestId = requestId, Status = "unavailable", Message = "The station did not answer." };
        }

        var body = reply.Body;
        if (reply.StatusCode == StatusCodes.Status404NotFound || body is null)
        {
            return RequestOutcome.LostTrack(requestId);
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

        // The station's ids are not trusted to be unique: the first show or persona with an id wins, a repeat is ignored
        // (ToDictionary would throw and the route would answer 500).
        var shows = new Dictionary<string, ScheduleShow>(StringComparer.Ordinal);
        foreach (var show in info.Shows ?? [])
        {
            if (show?.Id is not null)
            {
                shows.TryAdd(show.Id, show);
            }
        }

        var personas = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var persona in info.Personas ?? [])
        {
            if (persona?.Id is not null)
            {
                personas.TryAdd(persona.Id, persona.Name);
            }
        }

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

    private static string Seconds(TimeSpan delay)
        => ((int)Math.Ceiling(Math.Max(1, delay.TotalSeconds))).ToString(System.Globalization.CultureInfo.InvariantCulture);

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
            // A redirect (3xx) is not followed and its body is not read: it shows as a refusal, never as a like.
            Message = reply.StatusCode >= StatusCodes.Status300MultipleChoices ? (body?.Error ?? "The station did not take the like.") : null,
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

    // The only members that open a StationSource (StationRouteIlTests): its key, its base URL and its name. Every other
    // line handles the resolved station as an opaque value from CallerStation.
    private static string NameOf(StationSource station) => JellywaveChannel.StationName(station.Station);

    private static StationKey KeyOf(StationSource station)
        => station.Key ?? throw new InvalidOperationException("A station route resolved a station without a key.");

    private static Uri BaseUrlOf(StationSource station)
        => station.BaseUrl ?? throw new InvalidOperationException("A station route resolved a station without a base URL.");

    /// <summary>
    /// The station <see cref="StationRouteAttribute"/> resolved for this request: a listed SUB/WAVE station. Throws when
    /// the filter did not run, so an action that forgot the attribute fails instead of serving some other station.
    /// </summary>
    private StationSource CallerStation()
        => HttpContext.Items.TryGetValue(StationRouteAttribute.StationItem, out var value) && value is StationSource station
            ? station
            : throw new InvalidOperationException("The station route filter did not run for this action.");

    /// <summary>
    /// The plain stream the title route's filter resolved (card L6): its id only. Throws when the filter did not run, so an
    /// action that forgot the attribute fails instead of serving some other station.
    /// </summary>
    private PlainStation CallerPlainStation()
        => HttpContext.Items.TryGetValue(StationRouteAttribute.PlainStationItem, out var value) && value is PlainStation station
            ? station
            : throw new InvalidOperationException("The plain station route filter did not run for this action.");

    /// <summary>
    /// The channel entity <see cref="StationAccessAttribute"/> resolved for this request. Throws when the filter did not run.
    /// </summary>
    private Channel CallerChannel()
        => HttpContext.Items.TryGetValue(StationAccess.ChannelItem, out var value) && value is Channel channel
            ? channel
            : throw new InvalidOperationException("The station access filter did not run for this action.");

    /// <summary>
    /// A refusal this server makes before it asks the station (card R2a): the status (429 over the caller's budget, 503 when
    /// the server is busy) with a <c>Retry-After</c> in whole seconds, at least 1. <c>[ApiController]</c>'s client-error
    /// mapping gives it a ProblemDetails body (<c>status</c>, <c>title</c>, <c>traceId</c>); the page reads the status and
    /// the header, not the body.
    /// </summary>
    private StatusCodeResult Refused(int status, TimeSpan retryAfter)
    {
        Response.Headers[HeaderNames.RetryAfter] = Seconds(retryAfter);
        return StatusCode(status);
    }

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
