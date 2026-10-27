using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Jellywave;

/// <summary>
/// Registers the plugin's services. Jellyfin 12 resolves <see cref="IChannel"/> implementations
/// from the service collection only, so the channel must be added here to exist at all.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<StationClient>();
        serviceCollection.AddSingleton<NowPlayingState>();
        serviceCollection.AddSingleton<IChannel, JellywaveChannel>();
        serviceCollection.AddHostedService<NowPlayingService>();
        serviceCollection.AddSingleton<IUserStore, UserManagerStore>();
        serviceCollection.AddSingleton<IChannelLocator, LibraryChannelLocator>();
        serviceCollection.AddSingleton<AccessGranter>();
        serviceCollection.AddHostedService<AccessGrantService>();
        serviceCollection.AddScoped<IEventConsumer<UserCreatedEventArgs>, UserCreatedConsumer>();
        serviceCollection.AddScoped<IEventConsumer<UserUpdatedEventArgs>, UserUpdatedConsumer>();
    }
}
