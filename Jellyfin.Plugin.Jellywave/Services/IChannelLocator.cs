using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Configuration;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Finds the plugin's channel entity id.
/// </summary>
public interface IChannelLocator
{
    /// <summary>
    /// Gets the channel id for the configured station, creating the entity when the station is configured
    /// but the entity does not exist yet. Null when the station is not configured.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The channel id, or null.</returns>
    Task<Guid?> FindChannelIdAsync(PluginConfiguration config, CancellationToken cancellationToken);
}
