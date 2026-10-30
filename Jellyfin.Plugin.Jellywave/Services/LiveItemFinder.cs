using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Finds the channel entity and its live item with plain item queries. Deliberately not through
/// <c>IChannelManager</c>, which creates channel entities on demand and must not be raced at startup.
/// The channel is loaded by the id Jellyfin derives from the name (<c>ChannelManager.GetInternalChannelId</c>),
/// never by a name scan, and withheld when another registered channel derives the same id.
/// </summary>
public sealed class LiveItemFinder
{
    private readonly ILibraryManager _libraryManager;
    private readonly Func<IEnumerable<IChannel>> _channels;
    private readonly ILogger<LiveItemFinder> _logger;
    private int _colliding;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveItemFinder"/> class (DI constructor).
    /// The registered channels are resolved on first use, not here: <see cref="JellywaveChannel"/> is one of them,
    /// and resolving every plugin's channel while this singleton is built would construct them out of order.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="services">The service provider, for the registered <see cref="IChannel"/> implementations.</param>
    /// <param name="logger">The logger.</param>
    public LiveItemFinder(ILibraryManager libraryManager, IServiceProvider services, ILogger<LiveItemFinder> logger)
        : this(libraryManager, Registered(services), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveItemFinder"/> class with an explicit channel list (tests).
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="channels">Returns every registered channel, this plugin's included.</param>
    /// <param name="logger">The logger.</param>
    internal LiveItemFinder(ILibraryManager libraryManager, Func<IEnumerable<IChannel>> channels, ILogger<LiveItemFinder> logger)
    {
        _libraryManager = libraryManager;
        _channels = channels;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the channel entity for the configured tile name (<see cref="PluginConfiguration.LibraryName"/>), without creating it. The id is computed the way Jellyfin
    /// computes it, <c>GetNewItemId("Channel " + name, typeof(Channel))</c>, so it follows the server's own case rule
    /// (<c>EnableCaseSensitiveItemIds</c>). Another registered channel whose name derives the same id is a collision:
    /// Jellyfin would serve both through one entity, so nothing is returned. One Warning per collision streak.
    /// </summary>
    /// <param name="config">The configuration, or null for defaults.</param>
    /// <returns>The resolution.</returns>
    public ChannelResolution Resolve(PluginConfiguration? config)
    {
        var name = ConfigMigration.TileName(config ?? new PluginConfiguration());
        var id = DeriveChannelId(name);
        var other = _channels().FirstOrDefault(c => c is not JellywaveChannel && DeriveChannelId(c.Name) == id);
        if (other is not null)
        {
            if (Interlocked.Exchange(ref _colliding, 1) == 0)
            {
                _logger.LogWarning(
                    "Channel name {Name} collides with the channel {Other} of another plugin (both map to channel id {Id}); Jellywave serves, writes and grants nothing until the tile name under Dashboard > Plugins > Jellywave is changed",
                    name,
                    other.Name,
                    id);
            }

            return ChannelResolution.Collision(other.Name ?? string.Empty);
        }

        if (Interlocked.Exchange(ref _colliding, 0) == 1)
        {
            _logger.LogInformation("Channel name {Name} no longer collides with another plugin's channel", name);
        }

        return _libraryManager.GetItemById(id) is Channel channel ? ChannelResolution.Found(channel) : ChannelResolution.Missing;
    }

    /// <summary>
    /// Finds the channel entity by the configured name, without creating it.
    /// </summary>
    /// <param name="config">The configuration, or null for defaults.</param>
    /// <returns>The channel, or null when Jellyfin has not created it yet or its name collides with another channel.</returns>
    public Channel? FindChannel(PluginConfiguration? config) => Resolve(config).Channel;

    /// <summary>
    /// Finds a station's live item in a channel. The channel item's external id is the station id (card L2), and
    /// Jellyfin stores it as the item's <c>ExternalId</c> (<c>ChannelManager.GetChannelItemEntityAsync</c>, 12.3).
    /// </summary>
    /// <param name="channel">The channel entity.</param>
    /// <param name="stationId">The station id; blank finds nothing.</param>
    /// <returns>The live Audio item, or null when it has not been created yet or the id is blank.</returns>
    public BaseItem? FindLiveItem(Channel channel, string? stationId)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (string.IsNullOrWhiteSpace(stationId))
        {
            return null;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ChannelIds = [channel.Id],
            ExternalId = stationId,
            IncludeItemTypes = [BaseItemKind.Audio]
        });
        return items.Count > 0 ? items[0] : null;
    }

    private static Func<IEnumerable<IChannel>> Registered(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var channels = new Lazy<IChannel[]>(() => services.GetServices<IChannel>().ToArray(), LazyThreadSafetyMode.ExecutionAndPublication);
        return () => channels.Value;
    }

    /// <summary>
    /// Gets the ids Jellyfin derives for every other registered channel (this plugin's own excluded).
    /// </summary>
    /// <returns>The ids.</returns>
    public IReadOnlyCollection<Guid> OtherChannelIds()
        => _channels().Where(c => c is not JellywaveChannel).Select(c => DeriveChannelId(c.Name)).ToHashSet();

    /// <summary>
    /// Derives the channel id Jellyfin gives a channel of this name (<see cref="ChannelIds.Derive"/>).
    /// </summary>
    /// <param name="name">The channel name.</param>
    /// <returns>The channel id.</returns>
    public Guid DeriveChannelId(string? name) => ChannelIds.Derive(_libraryManager, name);
}
