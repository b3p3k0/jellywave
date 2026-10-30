using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Card R2a: the one way the station routes call a station on a user's behalf. Each call is checked, in this order,
/// against the caller's budgets (<see cref="UserBudgets"/>: 429 when over) and the shared limiter
/// (<see cref="StationCallLimiter"/>: 503 when the station or the server is busy), then made through
/// <see cref="StationClient"/>; the limiter's permit is held until the answer is read in full and disposed, and given back
/// on every exit. What is given back (card R2a fix round): a call that never reached the station (no connection, no name)
/// and a call this server's limiter refused get the whole reservation back; a station's refusal (its 4xx, its 429
/// included, or its 503) gets back only the kind's own windows (request, like) and the general window stays spent, so
/// a station that refuses every call (SUB/WAVE answers 409 to any song id not on air) still costs the caller a slot; a
/// timeout, a connection lost after sending and any other answer keep everything spent. Transport failures and timeouts come back as
/// "no answer" (<see cref="StationCall{T}.Answered"/> false), as the routes reported them before; the caller's own
/// cancellation propagates.
/// <para>
/// Answers are cached (<see cref="StationCache{T}"/>), keyed by the station key (id and origin) and the item, and only
/// a miss is admitted and costs the caller a budget slot and a permit; concurrent misses share one fetch. Lyrics per
/// (station, song): <see cref="LyricsLifetime"/> for lines or "none", <see cref="ErrorLifetime"/> for "pending", any other
/// status and no answer, except a 409 ("not on air", which the station answers for any song id the caller makes up),
/// which is not kept at all; at most <see cref="LyricsPerStation"/> per station, and the cache evicts the entry that
/// expires soonest, so short-lived error entries go before kept lines. Portraits per (station, persona):
/// <see cref="AvatarLifetime"/>, errors <see cref="ErrorLifetime"/>; at most <see cref="MaxAvatars"/> and
/// <see cref="MaxAvatarBytes"/> in all. Like status per (station, the song the poller last heard):
/// <see cref="LikeLifetime"/>; the station counts this whole server as one caller, so the status is the same for every
/// user; dropped after a like is taken and when the song changes. Request status per (station, receipt token):
/// <see cref="RequestStatusLifetime"/>, so the page's poll every 3 s asks the station about every 9 s.
/// </para>
/// </summary>
public sealed class StationCalls
{
    /// <summary>The most lyrics kept for one station.</summary>
    public const int LyricsPerStation = 64;

    /// <summary>The most lyrics kept in all.</summary>
    public const int MaxLyrics = 512;

    /// <summary>The most lyrics bytes kept in all (estimated).</summary>
    public const long MaxLyricsBytes = 4 * 1024 * 1024;

    /// <summary>The most portraits kept in all (and for one station).</summary>
    public const int MaxAvatars = 32;

    /// <summary>The most portrait bytes kept in all.</summary>
    public const long MaxAvatarBytes = 8 * 1024 * 1024;

    /// <summary>The most like statuses kept for one station (one song on air, and the one before it in flight).</summary>
    public const int LikesPerStation = 4;

    /// <summary>The most like statuses kept in all.</summary>
    public const int MaxLikes = 256;

    /// <summary>The most like status bytes kept in all (estimated).</summary>
    public const long MaxLikeBytes = 256 * 1024;

    /// <summary>The most request statuses kept (one per live receipt at most).</summary>
    public const int MaxRequestStatuses = RequestReceipts.MaxEntries;

    /// <summary>The most request status bytes kept in all (estimated).</summary>
    public const long MaxRequestStatusBytes = 1024 * 1024;

    /// <summary>The estimated fixed cost of one kept answer, in bytes.</summary>
    private const long EntryOverhead = 256;

    /// <summary>The estimated cost of one lyrics line besides its text, in bytes.</summary>
    private const long LineOverhead = 32;

    /// <summary>Bytes per character of kept text (UTF-16).</summary>
    private const long CharBytes = 2;

    private const int StatusOk = 200;
    private const int FirstRedirect = 300;
    private const int NotOnAir = 409;
    private const int TooManyRequests = 429;
    private const int ServiceUnavailable = 503;
    private const int FirstClientError = 400;
    private const int FirstServerError = 500;

    /// <summary>The <c>Retry-After</c> of a call refused because the station or the server has the most calls in flight.</summary>
    public static readonly TimeSpan BusyRetryAfter = TimeSpan.FromSeconds(2);

    /// <summary>How long lyrics lines (or "no lyrics") are kept.</summary>
    public static readonly TimeSpan LyricsLifetime = TimeSpan.FromMinutes(10);

    /// <summary>How long an error, no answer or "pending" lyrics are kept (lyrics and portraits; like and request status keep everything for their own shorter lifetime).</summary>
    public static readonly TimeSpan ErrorLifetime = TimeSpan.FromSeconds(15);

    /// <summary>How long a portrait is kept.</summary>
    public static readonly TimeSpan AvatarLifetime = TimeSpan.FromMinutes(10);

    /// <summary>How long a like status is kept.</summary>
    public static readonly TimeSpan LikeLifetime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a request status is kept. The page polls every 3 s while a request is pending; kept 8 s, the station is
    /// asked about it about every 9 s, which keeps the page's busiest minute under a third of <see cref="UserBudgets.CallsPerMinute"/>.
    /// </summary>
    public static readonly TimeSpan RequestStatusLifetime = TimeSpan.FromSeconds(8);

    private readonly StationClient _client;
    private readonly UserBudgets _budgets;
    private readonly StationCallLimiter _limiter;
    private readonly ILogger<StationCalls> _logger;
    private readonly StationCache<StationReply<LyricsPayload>> _lyrics;
    private readonly StationCache<StationImage> _avatars;
    private readonly StationCache<StationReply<LikeStatus>> _likes;
    private readonly StationCache<StationReply<RequestStatus>> _requestStatuses;

    /// <summary>
    /// Initializes a new instance of the <see cref="StationCalls"/> class (DI constructor).
    /// </summary>
    /// <param name="client">The station client.</param>
    /// <param name="logger">The logger.</param>
    public StationCalls(StationClient client, ILogger<StationCalls> logger)
        : this(client, new UserBudgets(), new StationCallLimiter(), logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StationCalls"/> class with explicit budgets, limiter and clock (tests).
    /// </summary>
    /// <param name="client">The station client.</param>
    /// <param name="budgets">The per-user budgets.</param>
    /// <param name="limiter">The shared limiter.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="clock">The caches' clock.</param>
    internal StationCalls(StationClient client, UserBudgets budgets, StationCallLimiter limiter, ILogger<StationCalls> logger, TimeProvider clock)
    {
        _client = client;
        _budgets = budgets;
        _limiter = limiter;
        _logger = logger;
        _lyrics = new StationCache<StationReply<LyricsPayload>>(clock, MaxLyrics, LyricsPerStation, MaxLyricsBytes, LyricsSize);
        _avatars = new StationCache<StationImage>(clock, MaxAvatars, MaxAvatars, MaxAvatarBytes, call => EntryOverhead + (call.Value?.Length ?? 0));
        _likes = new StationCache<StationReply<LikeStatus>>(clock, MaxLikes, LikesPerStation, MaxLikeBytes, call => EntryOverhead + (TextBytes(call.Value?.Body?.SongId) + TextBytes(call.Value?.Body?.Error)));
        _requestStatuses = new StationCache<StationReply<RequestStatus>>(clock, MaxRequestStatuses, MaxRequestStatuses, MaxRequestStatusBytes, RequestStatusSize);
    }

    /// <summary>Gets the lyrics cache (tests).</summary>
    internal StationCache<StationReply<LyricsPayload>> Lyrics => _lyrics;

    /// <summary>Gets the portrait cache (tests).</summary>
    internal StationCache<StationImage> Avatars => _avatars;

    /// <summary>Gets the like status cache (tests).</summary>
    internal StationCache<StationReply<LikeStatus>> Likes => _likes;

    /// <summary>
    /// The like status of the track on air at a station, cached per (station, song on air) for <see cref="LikeLifetime"/>.
    /// Without a song the poller has heard, it is asked every time.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <param name="station">The station's key (the route filter's).</param>
    /// <param name="baseUrl">The station's base URL (the route filter's).</param>
    /// <param name="songOnAir">The song id the poller last heard at that station, or null.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The outcome.</returns>
    public Task<StationCall<StationReply<LikeStatus>>> GetLikeAsync(Guid userId, StationKey station, Uri baseUrl, string? songOnAir, CancellationToken cancellationToken)
    {
        Func<CancellationToken, Task<StationReply<LikeStatus>?>> call = ct => Maybe(_client.GetLikeAsync(baseUrl, ct));
        if (!StationClient.IsValidId(songOnAir))
        {
            return CallAsync(userId, station, StationCallKind.LikeStatus, call, RefusedReply, cancellationToken);
        }

        // The song changed: the statuses of the songs before it go.
        var key = new StationCacheKey(station, songOnAir!);
        _likes.DropOthers(key);
        return _likes.GetAsync(
            key,
            () => Admit(userId, station, StationCallKind.LikeStatus, call, RefusedReply),
            // An answer for another song (the station moved on meanwhile) is not this song's status: not kept. Anything else,
            // no answer included, is kept for the like lifetime, which is already shorter than the error lifetime.
            answer => answer.Value is { StatusCode: StatusOk, Body.SongId: { } id } && !string.Equals(id, songOnAir, StringComparison.Ordinal)
                ? TimeSpan.Zero
                : LikeLifetime,
            cancellationToken);
    }

    /// <summary>
    /// Likes the track on air at a station.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <param name="station">The station's key.</param>
    /// <param name="baseUrl">The station's base URL.</param>
    /// <param name="songId">The song id the listener saw (already checked).</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<StationCall<StationReply<LikeStatus>>> PostLikeAsync(Guid userId, StationKey station, Uri baseUrl, string songId, CancellationToken cancellationToken)
    {
        var answer = await CallAsync(userId, station, StationCallKind.Like, ct => Maybe(_client.PostLikeAsync(baseUrl, songId, ct)), RefusedReply, cancellationToken).ConfigureAwait(false);
        if (answer.Value is { StatusCode: >= StatusOk and < FirstRedirect })
        {
            // Taken: the kept status (liked, count) is out of date.
            _likes.Invalidate(new StationCacheKey(station, songId));
        }

        return answer;
    }

    /// <summary>
    /// The lyrics of the track on air at a station.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <param name="station">The station's key.</param>
    /// <param name="baseUrl">The station's base URL.</param>
    /// <param name="songId">The song id (already checked).</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The outcome.</returns>
    public Task<StationCall<StationReply<LyricsPayload>>> GetLyricsAsync(Guid userId, StationKey station, Uri baseUrl, string songId, CancellationToken cancellationToken)
        => _lyrics.GetAsync(
            new StationCacheKey(station, songId),
            () => Admit(userId, station, StationCallKind.Lyrics, ct => Maybe(_client.GetLyricsAsync(baseUrl, songId, ct)), RefusedReply),
            answer => answer.Value switch
            {
                // "Not on air": answered for any song id a caller makes up, so it is never kept (it would push out lines).
                { StatusCode: NotOnAir } => TimeSpan.Zero,
                { StatusCode: StatusOk, Body: { } body } when body.Pending != true || body.Lines is { Count: > 0 } => LyricsLifetime,
                _ => ErrorLifetime
            },
            cancellationToken);

    /// <summary>
    /// A DJ portrait from a station. A station's "no art" answer cannot be told from a refusal, so it is never refunded.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <param name="station">The station's key.</param>
    /// <param name="baseUrl">The station's base URL.</param>
    /// <param name="personaId">The persona id (already checked).</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The outcome; its value is null when the station had no usable image.</returns>
    public Task<StationCall<StationImage>> GetAvatarAsync(Guid userId, StationKey station, Uri baseUrl, string personaId, CancellationToken cancellationToken)
        => _avatars.GetAsync(
            new StationCacheKey(station, personaId),
            () => Admit(userId, station, StationCallKind.Avatar, ct => _client.GetAvatarAsync(baseUrl, personaId, ct), _ => false),
            answer => answer.Value is not null ? AvatarLifetime : ErrorLifetime,
            cancellationToken);

    /// <summary>
    /// Sends a listener request to a station.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <param name="station">The station's key.</param>
    /// <param name="baseUrl">The station's base URL.</param>
    /// <param name="text">The request text (already checked).</param>
    /// <param name="name">The listener's name (already cut).</param>
    /// <param name="reserveReceipt">Reserves the caller's receipt slot (card R2a fix round: only once the budget has room,
    /// so an over-budget send never evicts the caller's own oldest receipt); false when the store refuses, which gives the
    /// budget back and refuses with 503 and <see cref="RequestReceipts.FullRetryAfter"/> before the station is asked.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The outcome.</returns>
    public Task<StationCall<StationReply<RequestReceipt>>> SubmitRequestAsync(Guid userId, StationKey station, Uri baseUrl, string text, string name, Func<bool> reserveReceipt, CancellationToken cancellationToken)
        => CallAsync(userId, station, StationCallKind.Request, ct => Maybe(_client.SubmitRequestAsync(baseUrl, text, name, ct)), RefusedReply, cancellationToken, reserveReceipt);

    /// <summary>
    /// A listener request's status at the station it went to.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <param name="station">The receipt's station key.</param>
    /// <param name="baseUrl">The receipt's station's base URL.</param>
    /// <param name="receipt">The caller's receipt token.</param>
    /// <param name="stationRequestId">The station's request id behind the caller's receipt.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The outcome.</returns>
    public Task<StationCall<StationReply<RequestStatus>>> GetRequestStatusAsync(Guid userId, StationKey station, Uri baseUrl, string receipt, string stationRequestId, CancellationToken cancellationToken)
        => _requestStatuses.GetAsync(
            new StationCacheKey(station, receipt),
            () => Admit(userId, station, StationCallKind.RequestStatus, ct => Maybe(_client.GetRequestStatusAsync(baseUrl, stationRequestId, ct)), RefusedReply),
            _ => RequestStatusLifetime,
            cancellationToken);

    /// <summary>A station answer that is never null, as one that may be (the shape <see cref="CallAsync"/> takes).</summary>
    private static async Task<T?> Maybe<T>(Task<T> answer)
        where T : class
        => await answer.ConfigureAwait(false);

    /// <summary>Whether a station answer is the station refusing: a 4xx (its 429 included) or a 503.</summary>
    private static bool RefusedReply<T>(StationReply<T>? reply)
        where T : class
        => reply is { StatusCode: (>= FirstClientError and < FirstServerError) or ServiceUnavailable };

    private static long TextBytes(string? text) => CharBytes * (text?.Length ?? 0);

    private static long LyricsSize(StationCall<StationReply<LyricsPayload>> call)
        => EntryOverhead + TextBytes(call.Value?.Body?.Error) + (call.Value?.Body?.Lines ?? []).Sum(l => LineOverhead + TextBytes(l?.Text));

    private static long RequestStatusSize(StationCall<StationReply<RequestStatus>> call)
    {
        var body = call.Value?.Body;
        return EntryOverhead + TextBytes(body?.Status) + TextBytes(body?.Ack) + TextBytes(body?.Message) + TextBytes(body?.Outcome)
            + TextBytes(body?.Track?.Artist) + TextBytes(body?.Track?.Title);
    }

    /// <summary>A call made every time (no cache): admitted, then run.</summary>
    private Task<StationCall<T>> CallAsync<T>(Guid userId, StationKey station, StationCallKind kind, Func<CancellationToken, Task<T?>> call, Func<T?, bool> refusedByStation, CancellationToken cancellationToken, Func<bool>? slot = null)
        where T : class
    {
        var (refusal, fetch) = Admit(userId, station, kind, call, refusedByStation, slot);
        return refusal is not null ? Task.FromResult(refusal) : fetch!(cancellationToken);
    }

    /// <summary>
    /// Admits one station call: the caller's budget, then a permit, both without waiting. Returns the refusal, or the call
    /// to run, which holds the reservation and the permit until it ends.
    /// </summary>
    private (StationCall<T>? Refusal, Func<CancellationToken, Task<StationCall<T>>>? Fetch) Admit<T>(Guid userId, StationKey station, StationCallKind kind, Func<CancellationToken, Task<T?>> call, Func<T?, bool> refusedByStation, Func<bool>? slot = null)
        where T : class
    {
        var reservation = _budgets.TryReserve(userId, kind, out var retryAfter);
        if (reservation is null)
        {
            _logger.LogDebug("{Kind} refused for user {UserId}: over the user's budget", kind, userId);
            return (StationCall<T>.Refuse(TooManyRequests, retryAfter), null);
        }

        if (slot is not null && !slot())
        {
            // Nothing was sent: the whole reservation goes back.
            reservation.Refund();
            _logger.LogWarning("{Kind} refused for user {UserId}: the receipt store is full", kind, userId);
            return (StationCall<T>.Refuse(ServiceUnavailable, RequestReceipts.FullRetryAfter), null);
        }

        var permit = _limiter.TryEnter(station.Origin, portrait: kind == StationCallKind.Avatar);
        if (permit is null)
        {
            reservation.Refund();
            _logger.LogDebug("{Kind} refused at station {StationId}: too many station calls in flight", kind, station.StationId);
            return (StationCall<T>.Refuse(ServiceUnavailable, BusyRetryAfter), null);
        }

        return (null, ct => RunAsync(kind, reservation, permit, call, refusedByStation, ct));
    }

    /// <summary>
    /// Runs an admitted call; the permit is disposed after the client has read and disposed the answer, on every exit.
    /// </summary>
    private async Task<StationCall<T>> RunAsync<T>(StationCallKind kind, BudgetReservation reservation, StationCallPermit permit, Func<CancellationToken, Task<T?>> call, Func<T?, bool> refusedByStation, CancellationToken cancellationToken)
        where T : class
    {
        using (permit)
        {
            try
            {
                var value = await call(cancellationToken).ConfigureAwait(false);
                if (refusedByStation(value))
                {
                    // The station refused: its own request or like window is given back, the general one stays spent, so
                    // refused calls (a 409 for any song id not on air) cannot be looped without limit.
                    reservation.RefundOwnWindows();
                }

                return StationCall<T>.Of(value);
            }
            catch (HttpRequestException ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
                {
                    // Nothing reached the station.
                    reservation.Refund();
                }

                Log(kind, ex);
                return StationCall<T>.NoAnswer();
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The client's timeout: the station may have acted on it, so the budget stays spent.
                Log(kind, ex);
                return StationCall<T>.NoAnswer();
            }
        }
    }

    private void Log(StationCallKind kind, Exception ex)
    {
        if (kind is StationCallKind.Request or StationCallKind.RequestStatus)
        {
            _logger.LogWarning("{Kind} to the station failed: {Message}", kind, ex.Message);
        }
        else
        {
            _logger.LogDebug("{Kind} from the station failed: {Message}", kind, ex.Message);
        }
    }
}
