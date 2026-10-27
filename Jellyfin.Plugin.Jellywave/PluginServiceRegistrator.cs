using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    /// <summary>
    /// The product token sent as the station client's User-Agent.
    /// </summary>
    private const string UserAgentProduct = "Jellywave";

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // The station client's own named client: Jellyfin's NamedClient.Default follows redirects, which would let a
        // station answer send the server anywhere (card 14). Jellyfin's dual-stack connect callback lives in a server
        // assembly the plugin does not reference; the station is a LAN host, so the default connect is used.
        serviceCollection.AddHttpClient(StationClient.HttpClientName, client => client.DefaultRequestHeaders.UserAgent.Add(UserAgent()))
            .ConfigurePrimaryHttpMessageHandler(CreateStationHandler);
        serviceCollection.AddSingleton<StationClient>();
        serviceCollection.AddSingleton<NowPlayingState>();
        serviceCollection.AddSingleton<LiveItemArtwork>();
        serviceCollection.AddSingleton<PollerHealth>();
        serviceCollection.AddSingleton<LiveItemFinder>();
        serviceCollection.AddSingleton<ScheduleCache>();
        serviceCollection.AddSingleton<IChannel, JellywaveChannel>();
        serviceCollection.AddHostedService<NowPlayingService>();
        serviceCollection.AddSingleton<IUserStore, UserManagerStore>();
        serviceCollection.AddSingleton<IChannelLocator, LibraryChannelLocator>();
        serviceCollection.AddSingleton<AccessGranter>();
        serviceCollection.AddSingleton<StationAccess>();
        serviceCollection.AddSingleton<RequestReceipts>();
        serviceCollection.AddHostedService<AccessGrantService>();
        serviceCollection.AddScoped<IEventConsumer<UserCreatedEventArgs>, UserCreatedConsumer>();
        serviceCollection.AddScoped<IEventConsumer<UserUpdatedEventArgs>, UserUpdatedConsumer>();
    }

    /// <summary>
    /// Creates the station client's primary handler: redirects are never followed, bodies are decompressed like
    /// Jellyfin's own clients do. StationHttpClientRegistrationTests checks this through the real registration.
    /// </summary>
    /// <returns>The handler.</returns>
    private static HttpMessageHandler CreateStationHandler()
        => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };

    private static ProductInfoHeaderValue UserAgent()
        => new(UserAgentProduct, typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0");
}
