using System;
using System.Linq;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 15b: the plugin writes user policies only when an admin presses the grant button. A user event consumer or a
/// startup pass would re-add the station after an admin removed it, so the real registrator must register neither.
/// </summary>
public class BackgroundGrantRegistrationTests
{
    private static ServiceCollection Registered()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);   // the application host is not used
        return services;
    }

    [Fact]
    public void No_user_event_consumer_is_registered()
    {
        var services = Registered();

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IEventConsumer<UserCreatedEventArgs>));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IEventConsumer<UserUpdatedEventArgs>));
        Assert.DoesNotContain(
            services,
            d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(IEventConsumer<>));
    }

    [Fact]
    public void The_hosted_services_are_the_now_playing_poller_and_the_title_sampler()
    {
        var hosted = Registered()
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToArray();

        Assert.Equal(new Type?[] { typeof(NowPlayingService), typeof(TitleSampler) }, hosted);
    }

    [Fact]
    public void The_title_sampler_s_services_are_registered()
    {
        // Card L6: the sampler reads sessions through the narrow IPlayingItems, and its state is one singleton shared with
        // the title route.
        var services = Registered();
        Assert.Equal(typeof(SessionPlayingItems), Assert.Single(services, d => d.ServiceType == typeof(IPlayingItems)).ImplementationType);
        Assert.Contains(services, d => d.ServiceType == typeof(StationTitles) && d.Lifetime == ServiceLifetime.Singleton);
        var pluginTypes = typeof(TitleSampler).GetConstructors().Single(c => c.IsPublic).GetParameters()
            .Select(p => p.ParameterType)
            .Where(t => t.Assembly == typeof(Plugin).Assembly)
            .ToArray();
        Assert.All(pluginTypes, t => Assert.Contains(services, d => d.ServiceType == t));

        // Card L14 fix round 2: DI builds the sampler with the station routes' tile gate, so a session of a user who cannot
        // see the tile never makes the server sample a stream.
        Assert.Contains(typeof(StationAccess), pluginTypes);
    }

    [Fact]
    public void The_listener_demand_and_the_status_check_are_singletons_the_poller_and_the_filter_share()
    {
        // Card L14: one demand holds the open pages the filter records and the poller reads; a second instance would never
        // see a page. The check guards one check at a time, so it is one instance too.
        var services = Registered();
        Assert.Contains(services, d => d.ServiceType == typeof(StationDemand) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(StationCheck) && d.Lifetime == ServiceLifetime.Singleton);
        var pollerTypes = typeof(NowPlayingService).GetConstructors().Single(c => c.IsPublic).GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Contains(typeof(StationDemand), pollerTypes);
        Assert.All(pollerTypes.Where(t => t.Assembly == typeof(Plugin).Assembly), t => Assert.Contains(services, d => d.ServiceType == t));
        Assert.Contains(typeof(StationCheck), typeof(Api.JellywaveController).GetConstructors().Single(c => c.IsPublic).GetParameters().Select(p => p.ParameterType));

        // Fix round S2: DI builds the demand with the station routes' tile gate (its only public constructor), so a session
        // of a user who cannot see the tile never counts.
        var demandTypes = typeof(StationDemand).GetConstructors().Single(c => c.IsPublic).GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Equal(new[] { typeof(IPlayingItems), typeof(StationAccess) }, demandTypes);
        Assert.All(demandTypes, t => Assert.Contains(services, d => d.ServiceType == t && d.Lifetime == ServiceLifetime.Singleton));
    }

    [Fact]
    public void The_channel_reads_users_through_the_read_only_class()
    {
        // Card L1 (S3): the channel's IsEnabledFor gets an IUserReader that is not an IUserStore.
        var reader = Assert.Single(Registered(), d => d.ServiceType == typeof(IUserReader));
        Assert.Equal(typeof(UserManagerReader), reader.ImplementationType);
        Assert.Equal(typeof(UserManagerStore), Assert.Single(Registered(), d => d.ServiceType == typeof(IUserStore)).ImplementationType);
    }

    [Fact]
    public void The_station_page_controller_s_plugin_services_are_registered()
    {
        // Card L5: StationRouteAttribute resolves StationResolver from the request services, and the controller takes it;
        // a missing registration would only show as a 500 on the host.
        var services = Registered();
        Assert.Contains(services, d => d.ServiceType == typeof(StationResolver) && d.Lifetime == ServiceLifetime.Singleton);
        var pluginTypes = typeof(Api.JellywaveWebController).GetConstructors().Single().GetParameters()
            .Select(p => p.ParameterType)
            .Where(t => t.Assembly == typeof(Plugin).Assembly);
        Assert.All(pluginTypes, t => Assert.Contains(services, d => d.ServiceType == t));
    }

    [Fact]
    public void The_granter_is_still_registered_for_the_admin_button()
    {
        Assert.Contains(Registered(), d => d.ServiceType == typeof(AccessGranter) && d.Lifetime == ServiceLifetime.Singleton);
    }
}
