using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Jellywave.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Makes the station visible to users by adding the plugin's channel id to their
/// <see cref="PreferenceKind.EnabledChannels"/> preference. That is the only policy field ever written:
/// <see cref="PermissionKind.EnableAllChannels"/> is never set, other channels are never added or removed,
/// and <see cref="PreferenceKind.BlockedChannels"/> is respected (an explicit block wins). Nothing is ever removed.
/// Runs only when an administrator asks (<c>POST /Jellywave/GrantAccess</c>); there is no background writer, so an
/// admin who takes the channel away from a user is never overruled (card 15b). One gate serializes the passes, and
/// each user is re-read under it immediately before it is written, so two presses never save a stale copy over each
/// other. A dashboard save between that read and the write can still be lost; accepted (DESIGN.md).
/// The rename bridge's one-shot <see cref="CarryOverAsync"/> (card L1) runs under the same gate and is add-only too: it
/// copies grants and blocks naming an earlier tile name's id onto the current id.
/// </summary>
public sealed class AccessGranter : IDisposable
{
    /// <summary>The <see cref="GrantResult.Error"/> text when the plugin configuration is not loaded.</summary>
    public const string NotReadyError = "the plugin is not ready yet";

    /// <summary>The <see cref="GrantResult.Error"/> text when the channel entity does not exist.</summary>
    public const string MissingChannelError = "the tile does not exist yet (add a station and save)";

    private readonly IUserStore _users;
    private readonly IChannelLocator _channels;
    private readonly Func<PluginConfiguration?> _configuration;
    private readonly RenameBridge? _bridge;
    private readonly Action<IReadOnlyList<string>> _recordCarried;
    private readonly ILogger<AccessGranter> _logger;
    // Lock order: this gate, then Plugin's save gate (the carry-over records its names under it); never the reverse.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessGranter"/> class (DI constructor).
    /// </summary>
    /// <param name="users">The user store.</param>
    /// <param name="channels">The channel locator.</param>
    /// <param name="bridge">The rename bridge, for the earlier tile names' ids.</param>
    /// <param name="logger">The logger.</param>
    public AccessGranter(IUserStore users, IChannelLocator channels, RenameBridge bridge, ILogger<AccessGranter> logger)
        : this(users, channels, logger, () => Plugin.Instance?.Configuration, bridge, names => Plugin.Instance?.RecordCarriedNames(names))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessGranter"/> class with an explicit configuration source and no
    /// rename bridge (tests of the grant pass; a carry-over reports "not ready").
    /// </summary>
    /// <param name="users">The user store.</param>
    /// <param name="channels">The channel locator.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">Returns the current configuration, or null when the plugin is not ready.</param>
    internal AccessGranter(IUserStore users, IChannelLocator channels, ILogger<AccessGranter> logger, Func<PluginConfiguration?> configuration)
        : this(users, channels, logger, configuration, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessGranter"/> class with explicit sources (tests).
    /// </summary>
    /// <param name="users">The user store.</param>
    /// <param name="channels">The channel locator.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">Returns the current configuration, or null when the plugin is not ready.</param>
    /// <param name="bridge">The rename bridge, or null when there is none (carry-over reports "not ready").</param>
    internal AccessGranter(IUserStore users, IChannelLocator channels, ILogger<AccessGranter> logger, Func<PluginConfiguration?> configuration, RenameBridge? bridge)
        : this(users, channels, logger, configuration, bridge, names => AddCarried(configuration(), names))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessGranter"/> class with explicit sources (tests).
    /// </summary>
    /// <param name="users">The user store.</param>
    /// <param name="channels">The channel locator.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">Returns the current configuration, or null when the plugin is not ready.</param>
    /// <param name="bridge">The rename bridge, or null when there is none (carry-over reports "not ready").</param>
    /// <param name="recordCarried">Records the earlier tile names a carry-over ran for, and saves.</param>
    internal AccessGranter(IUserStore users, IChannelLocator channels, ILogger<AccessGranter> logger, Func<PluginConfiguration?> configuration, RenameBridge? bridge, Action<IReadOnlyList<string>> recordCarried)
    {
        _users = users;
        _channels = channels;
        _logger = logger;
        _configuration = configuration;
        _bridge = bridge;
        _recordCarried = recordCarried;
    }

    /// <summary>
    /// How a user stands with respect to the channel.
    /// </summary>
    public enum Standing
    {
        /// <summary>The user already sees the channel (all channels, or the channel is in the enabled list, or an unrelated block list).</summary>
        Visible,

        /// <summary>The user has an explicit block list that names this channel, or an earlier tile name's id (rename bridge); the plugin leaves it alone.</summary>
        Blocked,

        /// <summary>The user cannot see the channel and the plugin can fix it by adding the id to the enabled list.</summary>
        NeedsGrant
    }

    /// <summary>
    /// Classifies a user exactly the way <c>Channel.IsVisible</c> decides. Pure; no I/O.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="channelId">The channel id.</param>
    /// <returns>The user's standing.</returns>
    public static Standing Classify(User user, Guid channelId) => Classify(user, channelId, []);

    /// <summary>
    /// Classifies a user the way <c>Channel.IsVisible</c> decides, with the rename bridge on top: a block list naming an
    /// earlier tile name's id counts as blocked, because the bridge hides the tile from that user. Pure; no I/O.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="channelId">The channel id.</param>
    /// <param name="previousIds">The earlier tile names' ids.</param>
    /// <returns>The user's standing.</returns>
    public static Standing Classify(User user, Guid channelId, IReadOnlyCollection<Guid> previousIds)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(previousIds);

        var blocked = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedChannels);
        if (blocked.Length != 0)
        {
            if (blocked.Any(previousIds.Contains))
            {
                return Standing.Blocked;
            }

            // With a block list present Jellyfin ignores EnableAllChannels and EnabledChannels entirely.
            return blocked.Contains(channelId) ? Standing.Blocked : Standing.Visible;
        }

        if (user.HasPermission(PermissionKind.EnableAllChannels)
            || user.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels).Contains(channelId))
        {
            return Standing.Visible;
        }

        return Standing.NeedsGrant;
    }

    /// <summary>
    /// Adds the channel id to the user's enabled list. Pure mutation of the entity; the caller persists it.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="channelId">The channel id.</param>
    public static void AddToEnabledChannels(User user, Guid channelId)
    {
        ArgumentNullException.ThrowIfNull(user);
        var current = user.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels);
        if (current.Contains(channelId))
        {
            return;
        }

        user.SetPreference(PreferenceKind.EnabledChannels, [.. current, channelId]);
    }

    /// <summary>
    /// Adds the channel id to the user's block list. Pure mutation of the entity; the caller persists it. Adding a block
    /// only narrows access.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="channelId">The channel id.</param>
    public static void AddToBlockedChannels(User user, Guid channelId)
    {
        ArgumentNullException.ThrowIfNull(user);
        var current = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedChannels);
        if (current.Contains(channelId))
        {
            return;
        }

        user.SetPreference(PreferenceKind.BlockedChannels, [.. current, channelId]);
    }

    /// <summary>
    /// What a carry-over would do for one user. Pure; no I/O.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="previousIds">The earlier tile names' ids (the current id excluded).</param>
    /// <param name="channelId">The current channel id.</param>
    /// <returns>
    /// Whether the user's lists name an earlier tile at all, whether the new id must be added to the enabled list (an
    /// earlier id is there and the new one is not), and whether it must be added to the block list (same rule).
    /// </returns>
    public static (bool NamesPrevious, bool AddGrant, bool AddBlock) PlanCarryOver(User user, IReadOnlyCollection<Guid> previousIds, Guid channelId)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(previousIds);
        var enabled = user.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels);
        var blocked = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedChannels);
        var enabledPrevious = enabled.Any(previousIds.Contains);
        var blockedPrevious = blocked.Any(previousIds.Contains);
        return (
            enabledPrevious || blockedPrevious,
            enabledPrevious && !enabled.Contains(channelId),
            blockedPrevious && !blocked.Contains(channelId));
    }

    /// <summary>
    /// The rename bridge's one-shot carry-over: for every user whose enabled list names an earlier tile name's id, adds
    /// the current channel id to that list; same for the block list. Adds only, never removes, never sets
    /// <see cref="PermissionKind.EnableAllChannels"/>. Runs under the grant gate and re-reads each user right before
    /// writing it. Nothing is written when the plugin is not ready, the channel does not exist yet, or its name collides
    /// with another plugin's channel.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The counts, or <see cref="CarryOverResult.Error"/> set when nothing was done.</returns>
    public async Task<CarryOverResult> CarryOverAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CarryOverUnderGateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Counts the users a carry-over would change now, for the configuration page's rename notice. Reads only, outside
    /// the gate; the count may be one press behind while a run is in progress.
    /// </summary>
    /// <returns>The status, or <see cref="CarryOverStatus.Error"/> set when the plugin is not ready.</returns>
    public CarryOverStatus CarryOverStatus()
    {
        var config = _configuration();
        if (config is null || _bridge is null)
        {
            return new CarryOverStatus { Error = NotReadyError };
        }

        var (names, previous, skipped) = _bridge.CarryCandidates(config);
        var status = new CarryOverStatus { TileName = ConfigMigration.TileName(config), PreviousNames = names, SkippedOtherChannel = skipped };
        if (previous.Count == 0)
        {
            return status;
        }

        var current = _bridge.CurrentId(config);
        foreach (var id in _users.UserIds().ToArray())
        {
            if (_users.GetUser(id) is not { } user)
            {
                continue;
            }

            var (_, addGrant, addBlock) = PlanCarryOver(user, previous, current);
            if (addGrant || addBlock)
            {
                status.Pending++;
            }
        }

        return status;
    }

    /// <summary>
    /// Card L13: who can see the tile now, by the same rule as the grant pass (<see cref="Classify(User, Guid, IReadOnlyCollection{Guid})"/>
    /// with the rename bridge's earlier ids), and which of those users have "Allow audio playback that requires
    /// transcoding" off, so they cannot play a station. Disabled users are left out entirely (fix round, MI). Reads
    /// users only, outside the gate, and writes none; logs nothing either (the page asks on every visit). Like the grant,
    /// the channel lookup may create the tile's channel entity when it does not exist yet (as a My Media load does). Nothing is counted when the plugin is not ready, the tile does not exist
    /// yet, or its name collides with another plugin's channel (the id could be the other plugin's channel).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The counts and names, or <see cref="TileViewers.Error"/> set.</returns>
    public async Task<TileViewers> ViewersAsync(CancellationToken cancellationToken)
    {
        var (channelId, error) = await ChannelIdAsync(cancellationToken, log: false).ConfigureAwait(false);
        if (channelId is null)
        {
            return new TileViewers { Error = error };
        }

        var previous = _bridge?.PreviousIds(_configuration()) ?? [];
        var result = new TileViewers();
        var noTranscoding = new List<string>();
        foreach (var id in _users.UserIds().ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_users.GetUser(id) is not { } user)
            {
                continue; // deleted since the snapshot
            }

            if (user.HasPermission(PermissionKind.IsDisabled))
            {
                continue; // MI 2026-10-29: a disabled user cannot sign in; neither counted nor named
            }

            result.Users++;
            if (Classify(user, channelId.Value, previous) != Standing.Visible)
            {
                continue;
            }

            result.Visible++;
            if (!user.HasPermission(PermissionKind.EnableAudioPlaybackTranscoding))
            {
                noTranscoding.Add(user.Username);
            }
        }

        noTranscoding.Sort(StringComparer.OrdinalIgnoreCase);
        result.NoAudioTranscoding = noTranscoding;
        return result;
    }

    /// <summary>
    /// Grants access once to every user who cannot see the channel yet. Serialized: a second call waits for the first.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// Counts for the run, or <see cref="GrantResult.Error"/> set (and nothing written) when the plugin is not ready,
    /// the channel does not exist yet, or its name collides with another plugin's channel.
    /// </returns>
    public async Task<GrantResult> GrantAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GrantAllUnderGateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task<GrantResult> GrantAllUnderGateAsync(CancellationToken cancellationToken)
    {
        var (channelId, error) = await ChannelIdAsync(cancellationToken).ConfigureAwait(false);
        if (channelId is null)
        {
            return new GrantResult { Error = error };
        }

        var previous = _bridge?.PreviousIds(_configuration()) ?? [];
        var result = new GrantResult();
        foreach (var id in _users.UserIds().ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var user = _users.GetUser(id);
            if (user is null)
            {
                continue; // deleted since the snapshot
            }

            switch (Classify(user, channelId.Value, previous))
            {
                case Standing.Visible:
                    result.AlreadyVisible++;
                    break;
                case Standing.Blocked:
                    result.Blocked++;
                    break;
                case Standing.NeedsGrant:
                    AddToEnabledChannels(user, channelId.Value);
                    await _users.SaveAsync(user).ConfigureAwait(false);
                    result.Granted++;
                    break;
                default:
                    throw new InvalidOperationException("Unknown standing");
            }
        }

        _logger.LogInformation(
            "Channel access run: {Granted} granted, {Visible} already visible, {Blocked} blocked by an explicit block list",
            result.Granted,
            result.AlreadyVisible,
            result.Blocked);
        return result;
    }

    private async Task<CarryOverResult> CarryOverUnderGateAsync(CancellationToken cancellationToken)
    {
        var config = _configuration();
        if (config is null || _bridge is null)
        {
            return new CarryOverResult { Error = NotReadyError };
        }

        var (channelId, error) = await ChannelIdAsync(cancellationToken).ConfigureAwait(false);
        if (channelId is null)
        {
            return new CarryOverResult { Error = error };
        }

        var current = channelId.Value;
        var (names, ids, _) = _bridge.CarryCandidates(config);
        var previous = ids.Where(id => id != current).ToArray();
        var result = new CarryOverResult();
        if (previous.Length == 0)
        {
            _logger.LogInformation("Channel access carry-over: nothing to carry (no earlier tile name it has not run for)");
            return result;
        }

        foreach (var id in _users.UserIds().ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var user = _users.GetUser(id);   // re-read now: the snapshot's entities would be stale by the time of the save
            if (user is null)
            {
                continue; // deleted since the snapshot
            }

            var (namesPrevious, addGrant, addBlock) = PlanCarryOver(user, previous, current);
            if (!namesPrevious)
            {
                result.Untouched++;
                continue;
            }

            if (!addGrant && !addBlock)
            {
                result.AlreadyCurrent++;
                continue;
            }

            if (addGrant)
            {
                AddToEnabledChannels(user, current);
                result.CarriedGrants++;
            }

            if (addBlock)
            {
                AddToBlockedChannels(user, current);
                result.CarriedBlocks++;
            }

            await _users.SaveAsync(user).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Channel access carry-over: {Grants} grants carried, {Blocks} blocks carried, {Current} already current, {Untouched} naming no earlier tile",
            result.CarriedGrants,
            result.CarriedBlocks,
            result.AlreadyCurrent,
            result.Untouched);

        // One-shot: the names are recorded only after every user was handled, so a failed run is offered again.
        _recordCarried(names);
        return result;
    }

    private static void AddCarried(PluginConfiguration? config, IReadOnlyList<string> names)
    {
        if (config is null)
        {
            return;
        }

        config.CarriedChannelNames ??= new System.Collections.ObjectModel.Collection<string>();
        foreach (var name in names)
        {
            config.CarriedChannelNames.Add(name);
        }
    }

    /// <summary>
    /// The channel id to grant, or null with the reason: the configuration is not loaded, the entity is missing, or
    /// the name collides with another plugin's channel (then nothing is granted, because the id may be the other
    /// plugin's channel; the finder logs that once per streak at Warning). <paramref name="log"/> false: a read
    /// (<see cref="ViewersAsync"/>) that says nothing in the log.
    /// </summary>
    private async Task<(Guid? ChannelId, string? Error)> ChannelIdAsync(CancellationToken cancellationToken, bool log = true)
    {
        var config = _configuration();
        if (config is null)
        {
            return (null, NotReadyError);
        }

        var resolution = await _channels.LocateAsync(config, cancellationToken).ConfigureAwait(false);
        if (resolution.Collides)
        {
            if (log)
            {
                _logger.LogInformation("Channel access run skipped: the channel name collides with another plugin's channel");
            }

            return (null, ChannelResolution.CollisionStatus);
        }

        if (resolution.Channel is null)
        {
            if (log)
            {
                _logger.LogInformation("Channel access run skipped: the channel entity does not exist yet (is a station listed?)");
            }

            return (null, MissingChannelError);
        }

        return (resolution.Channel.Id, null);
    }
}
