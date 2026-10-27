using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Makes the station visible to users by adding the plugin's channel id to their
/// <see cref="PreferenceKind.EnabledChannels"/> preference. That is the only policy field ever written:
/// <see cref="PermissionKind.EnableAllChannels"/> is never set, other channels are never added or removed,
/// and <see cref="PreferenceKind.BlockedChannels"/> is respected (an explicit block wins).
/// </summary>
public sealed class AccessGranter
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IChannelManager _channelManager;
    private readonly Func<PluginConfiguration?> _configuration;
    private readonly ILogger<AccessGranter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessGranter"/> class (DI constructor).
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="channelManager">The channel manager, used once to make sure the channel entity exists.</param>
    /// <param name="logger">The logger.</param>
    public AccessGranter(IUserManager userManager, ILibraryManager libraryManager, IChannelManager channelManager, ILogger<AccessGranter> logger)
        : this(userManager, libraryManager, channelManager, logger, () => Plugin.Instance?.Configuration)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessGranter"/> class with an explicit configuration source (tests).
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="channelManager">The channel manager.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">Returns the current configuration, or null when the plugin is not ready.</param>
    public AccessGranter(IUserManager userManager, ILibraryManager libraryManager, IChannelManager channelManager, ILogger<AccessGranter> logger, Func<PluginConfiguration?> configuration)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _channelManager = channelManager;
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
    /// Grants access to every user who needs it, when the setting is on.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Counts for the run; <see cref="GrantResult.Enabled"/> false when the setting is off or the channel is unknown.</returns>
    public async Task<GrantResult> GrantAllAsync(CancellationToken cancellationToken)
    {
        var config = _configuration();
        if (config is null || !config.GrantAccessToAllUsers)
        {
            return GrantResult.Disabled;
        }

        var channelId = await FindChannelIdAsync(config, cancellationToken).ConfigureAwait(false);
        if (channelId is null)
        {
            _logger.LogWarning("Cannot grant channel access: the channel entity does not exist yet (is the station URL configured?)");
            return GrantResult.Disabled;
        }

        var result = new GrantResult { Enabled = true };
        foreach (var user in _userManager.GetUsers())
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                    await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
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
    /// Grants access to one user (new-user hook), when the setting is on.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when a grant was written.</returns>
    public async Task<bool> GrantAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var config = _configuration();
        if (config is null || !config.GrantAccessToAllUsers)
        {
            return false;
        }

        var channelId = await FindChannelIdAsync(config, cancellationToken).ConfigureAwait(false);
        if (channelId is null || Classify(user, channelId.Value) != Standing.NeedsGrant)
        {
            return false;
        }

        AddToEnabledChannels(user, channelId.Value);
        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
        _logger.LogInformation("Channel access granted to a newly created user");
        return true;
    }

    /// <summary>
    /// Finds the channel entity id by name, creating the entity through the channel manager if it has not
    /// been created yet (that happens normally on the first My Media load). Call only after core startup.
    /// </summary>
    private async Task<Guid?> FindChannelIdAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var name = JellywaveChannel.StationName(config);
        var existing = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Channel] })
            .OfType<Channel>()
            .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing.Id;
        }

        if (!StationUrls.TryGetBaseUrl(config, out _))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var created = await _channelManager.GetChannelsInternalAsync(new ChannelQuery()).ConfigureAwait(false);
        return created.Items.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;
    }
}
