using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Binds each accepted listener request to the Jellyfin user who sent it and the station it went to (card L5), so only
/// that user can poll it and the poll goes to that station, never to one the caller names. The station sees this server
/// as one caller and cannot tell users apart. Card R2a: the page never sees the station's request id. Each receipt is our
/// own opaque token (<see cref="TokenLength"/> random hex digits), which maps to the user, the station key and the station's
/// request id, so two stations that hand out the same id, to two users or to one, never meet here. A slot is reserved
/// before the request is sent (<see cref="Reserve"/>) and filled when the station accepts it (<see cref="Complete"/>), so
/// a full store refuses before the station is asked. At most <see cref="MaxPerUser"/> per user (the user's own oldest
/// goes first) and <see cref="MaxEntries"/> in all; a user who owns none when the store is full is refused, and another
/// user's receipt is never evicted. In memory only: a restart forgets every receipt and the page then reports that the
/// station lost track of the request.
/// </summary>
public sealed class RequestReceipts
{
    /// <summary>The most receipts kept, reservations included.</summary>
    public const int MaxEntries = 1000;

    /// <summary>The most receipts one user holds, reservations included; the user's own oldest goes to make room.</summary>
    public const int MaxPerUser = 20;

    /// <summary>The length of a receipt token in hex digits (128 random bits). It passes <see cref="StationClient.IsValidId"/>.</summary>
    public const int TokenLength = 32;

    /// <summary>The <c>Retry-After</c> of a request refused because the store is full and the caller owns no receipt.</summary>
    public static readonly TimeSpan FullRetryAfter = TimeSpan.FromMinutes(1);

    /// <summary>How long a receipt can be polled. The page gives up after about a minute and a half.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>How long a reserved slot waits for the station's answer before it is pruned (a station call ends within 16 s).</summary>
    public static readonly TimeSpan ReservationLifetime = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Receipt> _receipts = new(StringComparer.Ordinal);
    private long _sequence;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestReceipts"/> class (DI constructor, system clock).
    /// </summary>
    public RequestReceipts()
        : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestReceipts"/> class with an explicit clock (tests).
    /// </summary>
    /// <param name="clock">The clock.</param>
    internal RequestReceipts(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>Gets the number of receipts held, reservations and expired ones included until the next reserve prunes them.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _receipts.Count;
            }
        }
    }

    /// <summary>
    /// Reserves a receipt slot for a user before a request is sent. Expired receipts are pruned first. A user at
    /// <see cref="MaxPerUser"/>, or any user while the store holds <see cref="MaxEntries"/>, gives up their own oldest
    /// accepted receipt; with none to give up the reservation is refused, and the request must not be sent.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <returns>The new receipt token, or null when refused.</returns>
    public string? Reserve(Guid userId)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            foreach (var expired in _receipts.Where(r => r.Value.Expires <= now).Select(r => r.Key).ToList())
            {
                _receipts.Remove(expired);
            }

            var own = _receipts.Where(r => r.Value.UserId == userId).ToList();
            if (own.Count >= MaxPerUser || _receipts.Count >= MaxEntries)
            {
                var accepted = own.Where(r => r.Value.Station is not null).ToList();
                if (accepted.Count == 0)
                {
                    return null;
                }

                _receipts.Remove(accepted.MinBy(r => r.Value.Sequence).Key);
            }

            string token;
            do
            {
                token = RandomNumberGenerator.GetHexString(TokenLength, lowercase: true);
            }
            while (_receipts.ContainsKey(token));

            _receipts[token] = new Receipt(userId, null, null, now + ReservationLifetime, ++_sequence);
            return token;
        }
    }

    /// <summary>
    /// Fills a reserved slot once the station accepted the request: the token now names the station's request id at that
    /// station, for <see cref="Lifetime"/>.
    /// </summary>
    /// <param name="token">The token from <see cref="Reserve"/>.</param>
    /// <param name="userId">The Jellyfin user id the slot was reserved for.</param>
    /// <param name="station">The key of the station the request went to (its id and origin, so a base URL changed under the
    /// same id does not receive another station's request id).</param>
    /// <param name="stationRequestId">The station's request id.</param>
    /// <returns>True when the receipt is recorded; false when the slot is gone or belongs to someone else.</returns>
    public bool Complete(string token, Guid userId, StationKey station, string stationRequestId)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentNullException.ThrowIfNull(station);
        ArgumentException.ThrowIfNullOrEmpty(stationRequestId);
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (!_receipts.TryGetValue(token, out var slot) || slot.UserId != userId || slot.Station is not null || slot.Expires <= now)
            {
                return false;
            }

            _receipts[token] = slot with { Station = station, StationRequestId = stationRequestId, Expires = now + Lifetime };
            return true;
        }
    }

    /// <summary>
    /// Gives a reserved slot back when the station did not accept the request (a refusal, an answer without a receipt, a
    /// failure). An accepted receipt is not touched.
    /// </summary>
    /// <param name="token">The token from <see cref="Reserve"/>.</param>
    public void Cancel(string token)
    {
        lock (_gate)
        {
            if (_receipts.TryGetValue(token, out var slot) && slot.Station is null)
            {
                _receipts.Remove(token);
            }
        }
    }

    /// <summary>
    /// Reserves and fills a slot in one step (tests and harnesses).
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="station">The key of the station the request went to.</param>
    /// <param name="stationRequestId">The station's request id.</param>
    /// <returns>The receipt token, or null when the store refused it.</returns>
    internal string? Record(Guid userId, StationKey station, string stationRequestId)
    {
        var token = Reserve(userId);
        return token is not null && Complete(token, userId, station, stationRequestId) ? token : null;
    }

    /// <summary>
    /// Says whose request a receipt token is, and for its owner which station it went to. A slot still waiting for the
    /// station's answer is unknown.
    /// </summary>
    /// <param name="token">The receipt token from the route.</param>
    /// <param name="userId">The caller's user id.</param>
    /// <param name="station">The key of the station the request went to; set only for <see cref="ReceiptOwnership.Owner"/>.</param>
    /// <returns>The caller's standing with that token.</returns>
    public ReceiptOwnership Check(string token, Guid userId, out StationKey? station)
    {
        station = null;
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (string.IsNullOrEmpty(token) || !_receipts.TryGetValue(token, out var receipt))
            {
                return ReceiptOwnership.Unknown;
            }

            if (receipt.Expires <= now)
            {
                _receipts.Remove(token);
                return ReceiptOwnership.Unknown;
            }

            if (receipt.UserId != userId)
            {
                return ReceiptOwnership.OtherUser;
            }

            if (receipt.Station is null)
            {
                return ReceiptOwnership.Unknown;
            }

            station = receipt.Station;
            return ReceiptOwnership.Owner;
        }
    }

    /// <summary>
    /// The station's request id behind a receipt token, for the status route's call to that station: only for the token's
    /// owner, only for the station key the receipt names, and only while it lives.
    /// </summary>
    /// <param name="token">The receipt token from the route.</param>
    /// <param name="userId">The caller's user id.</param>
    /// <param name="station">The key of the station the route resolved from the receipt.</param>
    /// <returns>The station's request id, or null.</returns>
    public string? StationRequestIdOf(string token, Guid userId, StationKey station)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            return !string.IsNullOrEmpty(token)
                && _receipts.TryGetValue(token, out var receipt)
                && receipt.Expires > now
                && receipt.UserId == userId
                && receipt.Station is not null
                && receipt.Station == station
                    ? receipt.StationRequestId
                    : null;
        }
    }

    /// <summary>One receipt; <see cref="Station"/> and <see cref="StationRequestId"/> are null while the slot is reserved.</summary>
    private readonly record struct Receipt(Guid UserId, StationKey? Station, string? StationRequestId, DateTimeOffset Expires, long Sequence);
}
