using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Looks the channel entity up by name in the library; creates it through the channel manager when the
/// station is configured but no My Media load has created it yet. Call only after core startup.
/// </summary>
public sealed class LibraryChannelLocator : IChannelLocator
{
    private readonly ILibraryManager _libraryManager;
    private readonly IChannelManager _channelManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryChannelLocator"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="channelManager">The channel manager.</param>
    public LibraryChannelLocator(ILibraryManager libraryManager, IChannelManager channelManager)
    {
        _libraryManager = libraryManager;
        _channelManager = channelManager;
    }

    /// <inheritdoc />
    public async Task<Guid?> FindChannelIdAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
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
