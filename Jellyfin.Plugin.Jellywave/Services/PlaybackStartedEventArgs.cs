using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// A session started playing an item (card L14): what <see cref="StationDemand"/> needs to wake the poller for an idle
/// SUB/WAVE station: the item's external id (a channel item's is its station id, card L2) and the session's user ids, so
/// only a user who can see the tile wakes it (fix round). Nothing else of the session.
/// </summary>
public sealed class PlaybackStartedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStartedEventArgs"/> class.
    /// </summary>
    /// <param name="externalId">The started item's external id, or null when it has none.</param>
    /// <param name="userIds">The ids of the session's users (its user and any added ones).</param>
    public PlaybackStartedEventArgs(string? externalId, IReadOnlyList<Guid> userIds)
    {
        ExternalId = externalId;
        UserIds = userIds;
    }

    /// <summary>Gets the started item's external id; null when it has none (not a channel item).</summary>
    public string? ExternalId { get; }

    /// <summary>Gets the ids of the session's users; empty when Jellyfin named none.</summary>
    public IReadOnlyList<Guid> UserIds { get; }
}
