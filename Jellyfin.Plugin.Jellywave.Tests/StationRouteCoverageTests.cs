using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Every action on the station page's controller is anonymous (the assets), admin-only, or behind
/// <see cref="StationAccessAttribute"/>. A new route that forgets the filter fails here. Card L5: every station route
/// except the station list also resolves its station through <see cref="StationRouteAttribute"/>, from the caller's
/// <c>stationId</c> (query for GET, body for POST) or, for the request status route only, from the receipt.
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

    private static StationRouteAttribute? RouteOf(MethodInfo m) => m.GetCustomAttribute<StationRouteAttribute>();

    private static bool IsPost(MethodInfo m) => m.GetCustomAttribute<HttpPostAttribute>() is not null;

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
        Assert.Equal(new[] { "Avatar", "Feed", "Like", "LikeStatus", "Lyrics", "RequestStatus", "Schedule", "Stations", "SubmitRequest", "Title" }, filtered);
    }

    [Fact]
    public void Only_the_title_route_resolves_a_plain_stream_and_it_takes_its_id_from_the_query()
    {
        // Card L6: the plain-stream variant of the filter serves the title route alone; every other station route keeps the
        // SUB/WAVE resolution, which refuses a plain stream's id.
        var plain = Assert.Single(Actions(), m => RouteOf(m)?.PlainStream == true);
        Assert.Equal("Title", plain.Name);
        Assert.False(RouteOf(plain)!.FromReceipt);
        Assert.NotNull(plain.GetCustomAttribute<HttpGetAttribute>());
        var id = Assert.Single(plain.GetParameters());
        Assert.Equal(StationRouteAttribute.StationIdParameter, id.Name);
        Assert.NotNull(id.GetCustomAttribute<FromQueryAttribute>());
    }

    [Fact]
    public void Every_station_route_but_the_list_resolves_its_station_through_the_route_filter()
    {
        var unresolved = Actions().Where(m => IsFiltered(m) && RouteOf(m) is null).Select(m => m.Name).ToArray();
        Assert.Equal(new[] { "Stations" }, unresolved);

        // The list takes nothing from the caller; it serves the listed stations only.
        var list = Actions().Single(m => m.Name == "Stations");
        Assert.Empty(list.GetParameters());
    }

    [Fact]
    public void The_route_filter_only_runs_behind_the_access_filter_and_after_it()
    {
        // StationAccess is an authorization filter, StationRoute an action filter: MVC runs every authorization filter
        // before model binding and every action filter after it, so the channel gate always decides first.
        Assert.All(Actions().Where(m => RouteOf(m) is not null), m => Assert.True(IsFiltered(m), m.Name));
        Assert.True(typeof(IAuthorizationFilter).IsAssignableFrom(typeof(StationAccessAttribute)));
        Assert.True(typeof(IActionFilter).IsAssignableFrom(typeof(StationRouteAttribute)));
        Assert.False(typeof(IAuthorizationFilter).IsAssignableFrom(typeof(StationRouteAttribute)));
        Assert.False(typeof(IAsyncAuthorizationFilter).IsAssignableFrom(typeof(StationRouteAttribute)));
        Assert.False(typeof(IResourceFilter).IsAssignableFrom(typeof(StationRouteAttribute)));
    }

    [Fact]
    public void Only_the_request_status_route_takes_its_station_from_the_receipt_and_it_takes_no_station_id()
    {
        var fromReceipt = Actions().Where(m => RouteOf(m)?.FromReceipt == true).ToArray();
        var status = Assert.Single(fromReceipt);
        Assert.Equal("RequestStatus", status.Name);
        Assert.Contains(status.GetParameters(), p => p.Name == StationRouteAttribute.RequestIdParameter && p.GetCustomAttribute<FromRouteAttribute>() is not null);
        Assert.DoesNotContain(status.GetParameters(), p => p.Name == StationRouteAttribute.StationIdParameter);
        Assert.DoesNotContain(status.GetParameters(), p => typeof(IStationSubmission).IsAssignableFrom(p.ParameterType));
    }

    [Fact]
    public void Get_station_routes_take_the_station_id_from_the_query_and_post_routes_from_the_body()
    {
        foreach (var m in Actions().Where(m => RouteOf(m) is { FromReceipt: false }))
        {
            var parameters = m.GetParameters();
            var query = parameters.Where(p => p.Name == StationRouteAttribute.StationIdParameter).ToArray();
            var bodies = parameters.Where(p => typeof(IStationSubmission).IsAssignableFrom(p.ParameterType)).ToArray();
            if (IsPost(m))
            {
                Assert.Empty(query);
                var body = Assert.Single(bodies);
                Assert.NotNull(body.GetCustomAttribute<FromBodyAttribute>());
            }
            else
            {
                Assert.NotNull(m.GetCustomAttribute<HttpGetAttribute>());
                Assert.Empty(bodies);
                var id = Assert.Single(query);
                Assert.Equal(typeof(string), id.ParameterType);
                Assert.NotNull(id.GetCustomAttribute<FromQueryAttribute>());
            }
        }
    }

    /// <summary>What would let the controller pick a station, or reach a station URL, other than the filter's.</summary>
    private static readonly Type[] StationAccessors = [typeof(StationSource), typeof(StationDefinition), typeof(PluginConfiguration), typeof(Uri), typeof(StationResolver)];

    /// <summary>
    /// The paths by which a value of <paramref name="root"/> hands out a station or a URL: the type itself, its generic
    /// arguments and element type, and, for the plugin's own types, what its public instance properties and fields hold
    /// and what its public instance methods return (also through <c>out</c>), transitively. Every method is followed,
    /// those that take a <see cref="Uri"/> included (a station can choose what comes back, as the probe's redirect does).
    /// </summary>
    internal static List<string> StationPaths(Type root)
    {
        var found = new List<string>();
        var seen = new HashSet<Type>();
        void Walk(Type type, string path)
        {
            if (type.IsByRef || type.IsArray || type.IsPointer)
            {
                Walk(type.GetElementType()!, path + "[]");
                return;
            }

            if (StationAccessors.Contains(type))
            {
                found.Add(path + " : " + type.Name);
                return;
            }

            if (!seen.Add(type))
            {
                return;
            }

            foreach (var argument in type.IsGenericType ? type.GetGenericArguments() : [])
            {
                Walk(argument, path + "<" + argument.Name + ">");
            }

            if (type.Assembly != typeof(Plugin).Assembly && type.DeclaringType != typeof(StationRouteCoverageTests))
            {
                return;
            }

            const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance;
            foreach (var property in type.GetProperties(Public))
            {
                Walk(property.PropertyType, path + "." + property.Name);
            }

            foreach (var field in type.GetFields(Public))
            {
                Walk(field.FieldType, path + "." + field.Name);
            }

            foreach (var method in type.GetMethods(Public).Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object)))
            {
                var parameters = method.GetParameters();
                Walk(method.ReturnType, path + "." + method.Name + "()");
                foreach (var output in parameters.Where(p => p.IsOut))
                {
                    Walk(output.ParameterType, path + "." + method.Name + "(out " + output.Name + ")");
                }
            }
        }

        Walk(root, root.Name);
        return found;
    }

    /// <summary>The paths the controller's dependencies may hand out anyway, each with the rule that keeps it unused.</summary>
    /// <remarks>Card R2a: empty since the controller holds no <see cref="StationClient"/> (its station calls go through
    /// <see cref="StationCalls"/>), so the probe's redirect target is no longer within its reach at all.</remarks>
    private static readonly Dictionary<string, string> AllowedStationPaths = new(StringComparer.Ordinal);

    [Fact]
    public void Nothing_the_controller_holds_hands_out_a_station_or_a_url()
    {
        // Card L5 (RA S2): the routes' station comes only from the filter (CallerStation). No constructor parameter or
        // field of the controller may be, or hand out through a public member, a station source or definition, the
        // configuration, a URL or the resolver, apart from the listed paths. What the controller's own code calls is
        // StationRouteIlTests' rule.
        var controller = typeof(JellywaveWebController);
        var held = controller.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
            .Concat(controller.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).Select(f => f.FieldType))
            .Distinct();

        var paths = held.SelectMany(StationPaths).ToArray();

        var unexpected = paths.Where(p => !AllowedStationPaths.ContainsKey(p)).ToArray();
        Assert.True(unexpected.Length == 0, string.Join("; ", unexpected));
        Assert.All(AllowedStationPaths.Keys, p => Assert.Contains(p, paths));   // the list holds no stale entry
    }

    [Fact]
    public void The_station_path_walk_finds_what_it_looks_for()
    {
        // Control for the rule above: the walk sees the resolver itself, a list of sources it returns, a URL in a returned
        // type's property, and a station handed out through an out parameter.
        Assert.Contains(StationPaths(typeof(StationResolver)), p => p.EndsWith(": StationResolver", StringComparison.Ordinal));
        Assert.Contains(StationPaths(typeof(Leaky)), p => p.Contains("Sources()<StationSource>", StringComparison.Ordinal));
        Assert.Contains(StationPaths(typeof(Leaky)), p => p.Contains("Answer().Location", StringComparison.Ordinal));
        Assert.Contains(StationPaths(typeof(Leaky)), p => p.Contains("(out station)", StringComparison.Ordinal));
        Assert.Contains(StationPaths(typeof(StationListOwner)), p => p == "StationListOwner.Stations : StationResolver");
        Assert.Empty(StationPaths(typeof(StationList)));
    }

    [Fact]
    public void The_controller_never_picks_a_station_itself()
    {
        // Second, text-level check: static paths (Plugin.Instance, StationSources) are out of reach of the reflection rule.
        // The routes read their station only from the filter (CallerStation); the primary-station and configuration paths
        // the routes used before card L5 must not come back.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "Jellyfin.Plugin.Jellywave", "Api", "JellywaveWebController.cs"));
        foreach (var banned in new[] { "StationSources.", "PrimaryStation", "PrimaryCandidate", "TryGetBaseUrl", "Plugin.Instance", "Resolve(", "ResolveReceipt(", ".Check(" })
        {
            Assert.DoesNotContain(banned, source, StringComparison.Ordinal);
        }
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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "build.yaml")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }

    /// <summary>A type that hands out stations and URLs in every way the walk must see (test control only).</summary>
    private sealed class Leaky
    {
        public IReadOnlyList<StationSource> Sources() => [];

        public StreamAnswer Answer() => new();

        public bool TryFind(string id, out StationDefinition? station)
        {
            station = id.Length == 0 ? null : new StationDefinition();
            return station is not null;
        }
    }

    /// <summary>A holder whose public property is the resolver (test control only).</summary>
    private sealed class StationListOwner
    {
        public StationResolver? Stations { get; set; }
    }
}
