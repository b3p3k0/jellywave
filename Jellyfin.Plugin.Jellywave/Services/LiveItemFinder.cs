using System;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Finds the channel entity and its live item with plain item queries. Deliberately not through
/// <c>IChannelManager</c>, which creates channel entities on demand and must not be raced at startup.
/// </summary>
public sealed class LiveItemFinder
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveItemFinder"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    public LiveItemFinder(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Finds the channel entity by the configured name, without creating it.
    /// </summary>
    /// <param name="config">The configuration, or null for defaults.</param>
    /// <returns>The channel, or null when Jellyfin has not created it yet.</returns>
    public Channel? FindChannel(PluginConfiguration? config)
    {
        var name = JellywaveChannel.StationName(config ?? new PluginConfiguration());
        return _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Channel] })
            .OfType<Channel>()
            .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds the live item of a channel.
    /// </summary>
    /// <param name="channel">The channel entity.</param>
    /// <returns>The live Audio item, or null when it has not been created yet.</returns>
    public BaseItem? FindLiveItem(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ChannelIds = [channel.Id],
            ExternalId = JellywaveChannel.LiveItemId,
            IncludeItemTypes = [BaseItemKind.Audio]
        });
        return items.Count > 0 ? items[0] : null;
    }
}
