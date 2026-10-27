using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Looks the channel entity up by its derived id (<see cref="LiveItemFinder"/>); creates it through the channel
/// manager when the station is configured but no My Media load has created it yet. Call only after core startup.
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
        if (existing.Channel is not null || existing.Collides || !StationUrls.TryGetBaseUrl(config, out _))
        {
            return existing;
        }

        // Creates every registered channel's entity (the same work a My Media load does). Runs only from the
        // admin's grant button, which is always after core startup.
        cancellationToken.ThrowIfCancellationRequested();
        await _channelManager.GetChannelsInternalAsync(new ChannelQuery()).ConfigureAwait(false);
        return _finder.Resolve(config);
    }
}
