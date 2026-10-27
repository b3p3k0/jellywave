using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Binds each accepted listener request to the Jellyfin user who sent it, so only that user can poll it.
/// The station sees this server as one caller and cannot tell users apart. In memory only: a restart forgets
/// every receipt and the page then reports that the station lost track of the request.
/// </summary>
public sealed class RequestReceipts
{
    /// <summary>The most receipts kept; the oldest is dropped to make room.</summary>
    public const int MaxEntries = 1000;

    /// <summary>How long a receipt can be polled. The page gives up after about a minute and a half.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Receipt> _receipts = new(StringComparer.Ordinal);

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

    /// <summary>Gets the number of receipts held, expired ones included until the next insert prunes them.</summary>
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
    /// Records that a user owns a request id. Expired receipts are pruned first; at the cap the oldest goes.
    /// </summary>
    /// <param name="requestId">The station's request id.</param>
    /// <param name="userId">The Jellyfin user id.</param>
    public void Record(string requestId, Guid userId)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            foreach (var expired in _receipts.Where(r => r.Value.Expires <= now).Select(r => r.Key).ToList())
            {
                _receipts.Remove(expired);
            }

            while (_receipts.Count >= MaxEntries && !_receipts.ContainsKey(requestId))
            {
                _receipts.Remove(_receipts.MinBy(r => r.Value.Expires).Key);
            }

            _receipts[requestId] = new Receipt(userId, now + Lifetime);
        }
    }

    /// <summary>
    /// Says whose request an id is.
    /// </summary>
    /// <param name="requestId">The station's request id.</param>
    /// <param name="userId">The caller's user id.</param>
    /// <returns>The caller's standing with that id.</returns>
    public ReceiptOwnership Check(string requestId, Guid userId)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (string.IsNullOrEmpty(requestId) || !_receipts.TryGetValue(requestId, out var receipt))
            {
                return ReceiptOwnership.Unknown;
            }

            if (receipt.Expires <= now)
            {
                _receipts.Remove(requestId);
                return ReceiptOwnership.Unknown;
            }

            return receipt.UserId == userId ? ReceiptOwnership.Owner : ReceiptOwnership.OtherUser;
        }
    }

    private readonly record struct Receipt(Guid UserId, DateTimeOffset Expires);
}
