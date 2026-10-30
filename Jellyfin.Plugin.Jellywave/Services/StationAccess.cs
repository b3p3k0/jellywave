using System;
using System.Security.Claims;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Channels;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Decides who may use the station routes: a signed-in user (never an API key, which Jellyfin's default
/// policy lets through unrestricted) who is not blocked from an earlier tile name (the rename bridge) and can see the
/// channel by Jellyfin's own <c>Channel.IsVisible</c>.
/// Fails closed: a missing claim, user or channel entity refuses the caller, and so does a channel name that collides
/// with another plugin's channel.
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
    private readonly Func<ChannelResolution> _resolveChannel;
    private readonly Func<User, bool> _blockedFromPrevious;
    private readonly ILogger<StationAccess> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StationAccess"/> class (DI constructor).
    /// </summary>
    /// <param name="users">The user store.</param>
    /// <param name="finder">The live item finder, for the channel entity.</param>
    /// <param name="bridge">The rename bridge, for blocks naming an earlier tile name.</param>
    /// <param name="logger">The logger.</param>
    public StationAccess(IUserStore users, LiveItemFinder finder, RenameBridge bridge, ILogger<StationAccess> logger)
        : this(users, () => finder.Resolve(Plugin.Instance?.Configuration), user => bridge.BlocksPrevious(user, Plugin.Instance?.Configuration), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StationAccess"/> class with an explicit channel source and no
    /// earlier tile names (tests).
    /// </summary>
    /// <param name="users">The user store.</param>
    /// <param name="resolveChannel">Resolves the channel entity: found, missing, or a name collision.</param>
    /// <param name="logger">The logger.</param>
    internal StationAccess(IUserStore users, Func<ChannelResolution> resolveChannel, ILogger<StationAccess> logger)
        : this(users, resolveChannel, _ => false, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StationAccess"/> class with explicit sources (tests).
    /// </summary>
    /// <param name="users">The user store.</param>
    /// <param name="resolveChannel">Resolves the channel entity: found, missing, or a name collision.</param>
    /// <param name="blockedFromPrevious">Whether a user's block list names an earlier tile name's id.</param>
    /// <param name="logger">The logger.</param>
    internal StationAccess(IUserStore users, Func<ChannelResolution> resolveChannel, Func<User, bool> blockedFromPrevious, ILogger<StationAccess> logger)
    {
        _users = users;
        _resolveChannel = resolveChannel;
        _blockedFromPrevious = blockedFromPrevious;
        _logger = logger;
    }

    /// <summary>
    /// Applies the rules in order: API key, user id, user, block on an earlier tile name, channel name collision, channel
    /// entity, channel visibility.
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

    /// <summary>
    /// The tile gate for the users of Jellyfin's sessions (card L14 fix round): a playback report carries a user, not a
    /// request, so the claim rules do not apply; the rest do (the user exists, is not blocked from an earlier tile name, and
    /// sees the channel by <c>Channel.IsVisible</c>; a name collision or a missing channel refuses everyone). The channel is
    /// resolved once, here, and the check returned reads one user per call. Throws when the channel lookup fails.
    /// </summary>
    /// <returns>Whether a user may count as a listener.</returns>
    public Func<Guid, bool> SessionUserGate()
    {
        var resolution = _resolveChannel();
        return SessionUserGate(resolution.Collides ? null : resolution.Channel);
    }

    /// <summary>
    /// <see cref="SessionUserGate()"/> for a channel the caller resolved already (the poller and the title sampler resolve
    /// it once per round, a collision as null), so the round looks the channel up once.
    /// </summary>
    /// <param name="channel">The channel entity; null (missing, or a name collision) refuses everyone.</param>
    /// <returns>Whether a user may count as a listener.</returns>
    public Func<Guid, bool> SessionUserGate(Channel? channel)
        => userId => channel is not null && userId != Guid.Empty && _users.GetUser(userId) is { } user && !_blockedFromPrevious(user) && channel.IsVisible(user);

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

        // The rename bridge: a block on an earlier tile keeps applying, although the new channel id passes IsVisible.
        if (_blockedFromPrevious(user))
        {
            return Refused(StationAccessReason.PreviousChannelBlocked);
        }

        var resolution = _resolveChannel();
        if (resolution.Collides)
        {
            // The derived id is shared with another plugin's channel, so its visibility says nothing about this station.
            return Refused(StationAccessReason.NameCollision);
        }

        var channel = resolution.Channel;
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
