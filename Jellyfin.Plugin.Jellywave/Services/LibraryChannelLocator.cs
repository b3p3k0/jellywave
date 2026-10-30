using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Looks the channel entity up by its derived id (<see cref="LiveItemFinder"/>); creates it through the channel
/// manager when at least one station is listed but no My Media load has created the entity yet. Call only after core
/// startup.
/// </summary>
public sealed class LibraryChannelLocator : IChannelLocator
{
    private readonly LiveItemFinder _finder;
    private readonly IChannelManager _channelManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryChannelLocator"/> class.
    /// </summary>
    /// <param name="finder">The finder that derives the channel id and detects name collisions.</param>
    /// <param name="channelManager">The channel manager.</param>
    public LibraryChannelLocator(LiveItemFinder finder, IChannelManager channelManager)
    {
        _finder = finder;
        _channelManager = channelManager;
    }

    /// <inheritdoc />
    public async Task<ChannelResolution> LocateAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var existing = _finder.Resolve(config);
        // Any listed station, plain streams included: the tile shows exactly those (card L7, finding F1).
        if (existing.Channel is not null || existing.Collides || StationSources.Publish(config).Count == 0)
        {
            return existing;
        }

        // Creates every registered channel's entity (the same work a My Media load does). Runs from the admin routes
        // only, always after core startup: the grant and rename buttons (POST GrantAccess, POST CarryAccess) and, card
        // L13, the "who can see it" count (GET Viewers), so the count works right after the first station is saved.
        // None of them writes a user from here.
        cancellationToken.ThrowIfCancellationRequested();
        await _channelManager.GetChannelsInternalAsync(new ChannelQuery()).ConfigureAwait(false);
        return _finder.Resolve(config);
    }
}
