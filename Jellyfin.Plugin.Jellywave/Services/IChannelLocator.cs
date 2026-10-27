using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Configuration;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Finds the plugin's channel entity.
/// </summary>
public interface IChannelLocator
{
    /// <summary>
    /// Resolves the channel for the configured station, creating the entity when the station is configured but the
    /// entity does not exist yet. The entity is null when the station is not configured or its name collides with
    /// another plugin's channel (<see cref="ChannelResolution.Collides"/>).
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resolution.</returns>
    Task<ChannelResolution> LocateAsync(PluginConfiguration config, CancellationToken cancellationToken);
}
