using System;
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
/// </summary>
public sealed class AccessGranter : IDisposable
{
    /// <summary>The <see cref="GrantResult.Error"/> text when the plugin configuration is not loaded.</summary>
    public const string NotReadyError = "the plugin is not ready yet";

    /// <summary>The <see cref="GrantResult.Error"/> text when the channel entity does not exist.</summary>
    public const string MissingChannelError = "the channel does not exist yet (set the station URL and save)";

    private readonly IUserStore _users;
    private readonly IChannelLocator _channels;
    private readonly Func<PluginConfiguration?> _configuration;
    private readonly ILogger<AccessGranter> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessGranter"/> class (DI constructor).
    /// </summary>
    /// <param name="users">The user store.</param>
    /// <param name="channels">The channel locator.</param>
    /// <param name="logger">The logger.</param>
    public AccessGranter(IUserStore users, IChannelLocator channels, ILogger<AccessGranter> logger)
        : this(users, channels, logger, () => Plugin.Instance?.Configuration)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessGranter"/> class with an explicit configuration source (tests).
    /// </summary>
    /// <param name="users">The user store.</param>
    /// <param name="channels">The channel locator.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">Returns the current configuration, or null when the plugin is not ready.</param>
    public AccessGranter(IUserStore users, IChannelLocator channels, ILogger<AccessGranter> logger, Func<PluginConfiguration?> configuration)
    {
        _users = users;
        _channels = channels;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// How a user stands with respect to the channel.
    /// </summary>
    public enum Standing
    {
        /// <summary>The user already sees the channel (all channels, or the channel is in the enabled list, or an unrelated block list).</summary>
        Visible,

        /// <summary>The user has an explicit block list that names this channel; the plugin leaves it alone.</summary>
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
    public static Standing Classify(User user, Guid channelId)
    {
        ArgumentNullException.ThrowIfNull(user);

        var blocked = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedChannels);
        if (blocked.Length != 0)
        {
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

        var result = new GrantResult();
        foreach (var id in _users.UserIds().ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var user = _users.GetUser(id);
            if (user is null)
            {
                continue; // deleted since the snapshot
            }

            switch (Classify(user, channelId.Value))
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

    /// <summary>
    /// The channel id to grant, or null with the reason: the configuration is not loaded, the entity is missing, or
    /// the name collides with another plugin's channel (then nothing is granted, because the id may be the other
    /// plugin's channel; the finder logs that once per streak at Warning).
    /// </summary>
    private async Task<(Guid? ChannelId, string? Error)> ChannelIdAsync(CancellationToken cancellationToken)
    {
        var config = _configuration();
        if (config is null)
        {
            return (null, NotReadyError);
        }

        var resolution = await _channels.LocateAsync(config, cancellationToken).ConfigureAwait(false);
        if (resolution.Collides)
        {
            _logger.LogInformation("Channel access run skipped: the channel name collides with another plugin's channel");
            return (null, ChannelResolution.CollisionStatus);
        }

        if (resolution.Channel is null)
        {
            _logger.LogInformation("Channel access run skipped: the channel entity does not exist yet (is the station URL configured?)");
            return (null, MissingChannelError);
        }

        return (resolution.Channel.Id, null);
    }
}
