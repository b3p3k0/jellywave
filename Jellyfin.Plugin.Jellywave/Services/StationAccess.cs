using System;
using System.Security.Claims;
using MediaBrowser.Controller.Channels;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Decides who may use the station routes: a signed-in user (never an API key, which Jellyfin's default
/// policy lets through unrestricted) who can see the channel by Jellyfin's own <c>Channel.IsVisible</c>.
/// Fails closed: a missing claim, user or channel entity refuses the caller.
/// </summary>
public sealed class StationAccess
{
    /// <summary>Jellyfin's claim carrying the signed-in user's id, "N" format (Jellyfin.Api.Constants.InternalClaimTypes.UserId).</summary>
    public const string UserIdClaim = "Jellyfin-UserId";

    /// <summary>Jellyfin's claim saying whether the caller used an API key, "True" or "False" (InternalClaimTypes.IsApiKey).</summary>
    public const string ApiKeyClaim = "Jellyfin-IsApiKey";

    /// <summary>The <c>HttpContext.Items</c> key under which an allowed caller's user id (a <see cref="Guid"/>) is left for the action.</summary>
    public const string UserIdItem = "Jellywave.StationAccess.UserId";

    /// <summary>The <c>HttpContext.Items</c> key under which the resolved <see cref="Channel"/> is left for the action.</summary>
    public const string ChannelItem = "Jellywave.StationAccess.Channel";

    private readonly IUserStore _users;
    private readonly Func<Channel?> _findChannel;
    private readonly ILogger<StationAccess> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StationAccess"/> class (DI constructor).
    /// </summary>
    /// <param name="users">The user store.</param>
    /// <param name="finder">The live item finder, for the channel entity.</param>
    /// <param name="logger">The logger.</param>
    public StationAccess(IUserStore users, LiveItemFinder finder, ILogger<StationAccess> logger)
        : this(users, () => finder.FindChannel(Plugin.Instance?.Configuration), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StationAccess"/> class with an explicit channel source (tests).
    /// </summary>
    /// <param name="users">The user store.</param>
    /// <param name="findChannel">Returns the channel entity, or null when it does not exist.</param>
    /// <param name="logger">The logger.</param>
    internal StationAccess(IUserStore users, Func<Channel?> findChannel, ILogger<StationAccess> logger)
    {
        _users = users;
        _findChannel = findChannel;
        _logger = logger;
    }

    /// <summary>
    /// Applies the rules in order: API key, user id, user, channel entity, channel visibility.
    /// </summary>
    /// <param name="principal">The authenticated caller.</param>
    /// <returns>The decision; for an allowed caller also its user id and the channel entity.</returns>
    public StationAccessResult Check(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var result = Decide(principal);
        if (result.Reason != StationAccessReason.Allowed)
        {
            _logger.LogDebug("Station route refused: {Reason}", result.Reason);
        }

        return result;
    }

    private static StationAccessResult Refused(StationAccessReason reason) => new(reason, Guid.Empty, null);

    private StationAccessResult Decide(ClaimsPrincipal principal)
    {
        // Jellyfin always sets this claim; anything but an explicit "False" is treated as an API key.
        if (!bool.TryParse(principal.FindFirst(ApiKeyClaim)?.Value, out var isApiKey) || isApiKey)
        {
            return Refused(StationAccessReason.ApiKey);
        }

        if (!Guid.TryParse(principal.FindFirst(UserIdClaim)?.Value, out var userId) || userId == Guid.Empty)
        {
            return Refused(StationAccessReason.NoUser);
        }

        var user = _users.GetUser(userId);
        if (user is null)
        {
            return Refused(StationAccessReason.UnknownUser);
        }

        var channel = _findChannel();
        if (channel is null)
        {
            return Refused(StationAccessReason.NoChannel);
        }

        // Jellyfin's own rule, the one behind the tile: block list, enabled list or EnableAllChannels,
        // then the parental check. Administrators are not exempt, same as in Jellyfin.
        return channel.IsVisible(user)
            ? new StationAccessResult(StationAccessReason.Allowed, userId, channel)
            : Refused(StationAccessReason.NotVisible);
    }
}
