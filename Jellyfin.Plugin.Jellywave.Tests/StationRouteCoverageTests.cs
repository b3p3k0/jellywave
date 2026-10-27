using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Jellywave.Api;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Every action on the station page's controller is anonymous (the assets), admin-only, or behind
/// <see cref="StationAccessAttribute"/>. A new route that forgets the filter fails here.
/// </summary>
public class StationRouteCoverageTests
{
    private static MethodInfo[] Actions() => typeof(JellywaveWebController)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => !m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() is null)
        .ToArray();

    private static bool IsAnonymous(MethodInfo m) => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null;

    private static bool IsAdminOnly(MethodInfo m)
        => m.GetCustomAttributes<AuthorizeAttribute>().Any(a => a.Policy == Policies.RequiresElevation);

    private static bool IsFiltered(MethodInfo m) => m.GetCustomAttribute<StationAccessAttribute>() is not null;

    [Fact]
    public void Every_user_route_carries_the_station_access_filter()
    {
        var unguarded = Actions().Where(m => !IsAnonymous(m) && !IsAdminOnly(m) && !IsFiltered(m)).Select(m => m.Name).ToArray();
        Assert.Empty(unguarded);
    }

    [Fact]
    public void The_filtered_routes_are_the_station_routes()
    {
        var filtered = Actions().Where(IsFiltered).Select(m => m.Name).Order().ToArray();
        Assert.Equal(new[] { "Avatar", "Feed", "Like", "LikeStatus", "Lyrics", "RequestStatus", "Schedule", "SubmitRequest" }, filtered);
    }

    [Fact]
    public void Every_action_is_a_routed_endpoint()
    {
        // Guards the reflection above: a public method without an HTTP verb would still be an MVC action.
        Assert.All(Actions(), m => Assert.NotNull(m.GetCustomAttribute<HttpMethodAttribute>()));
    }

    [Fact]
    public void Assets_stay_anonymous_and_web_ui_stays_admin_only()
    {
        var actions = Actions().ToDictionary(m => m.Name);
        Assert.True(IsAnonymous(actions["Script"]) && !IsFiltered(actions["Script"]));
        Assert.True(IsAnonymous(actions["Style"]) && !IsFiltered(actions["Style"]));
        Assert.True(IsAdminOnly(actions["WebUi"]) && !IsFiltered(actions["WebUi"]));
    }
}
