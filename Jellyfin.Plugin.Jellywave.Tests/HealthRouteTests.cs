using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 14 review: <c>GET /Jellywave/Health</c> tells a refused saved URL from a missing one, so the status line
/// does not say "no station URL configured" while the field holds a URL.
/// </summary>
public class HealthRouteTests
{
    /// <summary>
    /// An <see cref="ILibraryManager"/> with no channels: every item list is empty, anything else is not expected here.
    /// </summary>
    public class EmptyLibrary : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ILibraryManager.GetItemList) && targetMethod.ReturnType.IsAssignableFrom(typeof(List<BaseItem>)))
            {
                return new List<BaseItem>();
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private static HealthResponse Health(string baseUrl)
    {
        var station = new StationClient(null!);   // never called by Health
        var controller = new JellywaveController(
            station,
            null!,
            new NowPlayingState(),
            new PollerHealth(),
            new LiveItemArtwork(station, (_, _, _, _) => Task.CompletedTask, NullLogger<LiveItemArtwork>.Instance),
            new LiveItemFinder(DispatchProxy.Create<ILibraryManager, EmptyLibrary>()),
            NullLogger<JellywaveController>.Instance,
            () => new PluginConfiguration { StationBaseUrl = baseUrl });
        return controller.Health().Value!;
    }

    [Theory]
    [InlineData("http://radio.example:7700/?", StationUrls.RefusedQuery)]
    [InlineData("http://radio.example:7700/#", StationUrls.RefusedFragment)]
    [InlineData("http://listener:hunter2@radio.example:7700", StationUrls.RefusedCredentials)]
    public void A_refused_url_is_set_but_not_allowed(string baseUrl, string reason)
    {
        var health = Health(baseUrl);
        Assert.False(health.StationConfigured);
        Assert.Equal(reason, health.StationUrlRefusal);
        Assert.Equal("station URL is set but not allowed: " + reason, health.StationUrlStatus);
        Assert.DoesNotContain("hunter2", health.StationUrlStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("listener", health.StationUrlStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_url_is_not_configured()
    {
        var health = Health("  ");
        Assert.False(health.StationConfigured);
        Assert.Null(health.StationUrlRefusal);
        Assert.Equal("no station URL configured", health.StationUrlStatus);
    }

    [Fact]
    public void A_usable_url_has_no_status_text()
    {
        var health = Health("http://radio.example:7700");
        Assert.True(health.StationConfigured);
        Assert.Null(health.StationUrlRefusal);
        Assert.Null(health.StationUrlStatus);
        Assert.False(health.LiveItemExists);
    }
}
