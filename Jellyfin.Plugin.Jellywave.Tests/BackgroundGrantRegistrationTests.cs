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
    public void The_only_hosted_service_is_the_now_playing_poller()
    {
        var hosted = Registered()
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToArray();

        Assert.Equal(new Type?[] { typeof(NowPlayingService) }, hosted);
    }

    [Fact]
    public void The_granter_is_still_registered_for_the_admin_button()
    {
        Assert.Contains(Registered(), d => d.ServiceType == typeof(AccessGranter) && d.Lifetime == ServiceLifetime.Singleton);
    }
}
