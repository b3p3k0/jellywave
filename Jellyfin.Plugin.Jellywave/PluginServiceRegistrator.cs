using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
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

    /// <summary>
    /// The User-Agent comment: the project URL, so a station or image host can see who calls (card L9; Wikimedia's
    /// User-Agent policy asks clients for a contact URL). Sent as <c>Jellywave/&lt;version&gt; (+&lt;url&gt;)</c>.
    /// </summary>
    private const string UserAgentComment = "(+https://github.com/b3p3k0/jellywave)";

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // The station client's own named client: Jellyfin's NamedClient.Default follows redirects, which would let a
        // station answer send the server anywhere (card 14). Jellyfin's dual-stack connect callback lives in a server
        // assembly the plugin does not reference; the station is a LAN host, so the default connect is used.
        serviceCollection.AddHttpClient(StationClient.HttpClientName, client =>
            {
                client.DefaultRequestHeaders.UserAgent.Add(UserAgent());
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(UserAgentComment));
            })
            .ConfigurePrimaryHttpMessageHandler(CreateStationHandler);
        serviceCollection.AddSingleton<StationClient>();
        serviceCollection.AddSingleton<NowPlayingState>();
        serviceCollection.AddSingleton<LiveItemArtwork>();
        serviceCollection.AddSingleton<StationLogos>();
        serviceCollection.AddSingleton<PollerHealth>();
        serviceCollection.AddSingleton<LiveItemFinder>();
        serviceCollection.AddSingleton<RenameBridge>();
        serviceCollection.AddSingleton<ScheduleCache>();
        serviceCollection.AddSingleton<IUserStore, UserManagerStore>();
        // The channel's IsEnabledFor reads users through a class that cannot write them (not an IUserStore).
        serviceCollection.AddSingleton<IUserReader, UserManagerReader>();
        serviceCollection.AddSingleton<IChannel, JellywaveChannel>();
        serviceCollection.AddHostedService<NowPlayingService>();
        serviceCollection.AddSingleton<StationTitles>();
        serviceCollection.AddSingleton<IPlayingItems, SessionPlayingItems>();
        serviceCollection.AddSingleton<StationDemand>();
        serviceCollection.AddSingleton<StationCheck>();
        serviceCollection.AddHostedService<TitleSampler>();
        serviceCollection.AddSingleton<IChannelLocator, LibraryChannelLocator>();
        serviceCollection.AddSingleton<AccessGranter>();
        serviceCollection.AddSingleton<StationAccess>();
        serviceCollection.AddSingleton<RequestReceipts>();
        serviceCollection.AddSingleton<StationCalls>();
        serviceCollection.AddSingleton<StationResolver>();
        serviceCollection.AddSingleton<Api.StationList>();
    }

    /// <summary>
    /// Creates the station client's primary handler: redirects are never followed, bodies are decompressed like
    /// Jellyfin's own clients do, and a body left unread is never drained (card L6, RA R1): .NET otherwise reads up to
    /// 1 MB for up to 2 s of an HTTP/1.1 chunked or declared-length body on dispose to reuse the connection, which for an
    /// audio stream (a title sample, the add-station probe) is a megabyte of audio and a late close. Every other call reads
    /// its body in full or does not want it, so the connection is simply closed. StationHttpClientRegistrationTests and
    /// IcySampleSocketTests check this through the real registration.
    /// </summary>
    /// <returns>The handler.</returns>
    private static HttpMessageHandler CreateStationHandler()
        => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            MaxResponseDrainSize = 0
        };

    private static ProductInfoHeaderValue UserAgent()
        => new(UserAgentProduct, typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0");
}
