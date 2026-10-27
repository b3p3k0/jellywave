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

    /// <summary>The channel entity does not exist (yet), so visibility cannot be decided.</summary>
    NoChannel,

    /// <summary>Jellyfin's own <c>Channel.IsVisible</c> hides the channel from this user.</summary>
    NotVisible
}
