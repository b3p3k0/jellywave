namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The outcome of <see cref="StationAccess.Check"/>: allowed, or the first rule that refused the caller.
/// </summary>
public enum StationAccessReason
{
    /// <summary>A signed-in user who can see the channel in Jellyfin.</summary>
    Allowed,

    /// <summary>The caller authenticated with an API key, or the API-key claim is missing or unreadable.</summary>
    ApiKey,

    /// <summary>No usable user id claim.</summary>
    NoUser,

    /// <summary>The user id does not name an existing user.</summary>
    UnknownUser,

    /// <summary>
    /// The user's block list names the id of an earlier tile name (the rename bridge, card L1): a block on the old tile
    /// keeps applying after a rename. The filter answers 403.
    /// </summary>
    PreviousChannelBlocked,

    /// <summary>The channel entity does not exist (yet), so visibility cannot be decided. The filter answers 404.</summary>
    NoChannel,

    /// <summary>
    /// Another plugin's channel derives the same channel id, so the entity may not be this station's. The filter answers
    /// 404, like a missing channel: the station has no channel of its own to serve until the admin renames the tile.
    /// </summary>
    NameCollision,

    /// <summary>Jellyfin's own <c>Channel.IsVisible</c> hides the channel from this user.</summary>
    NotVisible
}
