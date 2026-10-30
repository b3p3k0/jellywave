using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Builds <see cref="JellywaveWebController"/> as DI would, with its <see cref="StationResolver"/> and
/// <see cref="StationDemand"/> (card L14) in the request services
/// and the caller as <see cref="StationAccessAttribute"/> leaves an allowed one, and runs an action the way MVC does after
/// model binding: the action's own <see cref="StationRouteAttribute"/> first (taken from the method, so a test cannot run
/// a route without the filter the route really carries), then the action.
/// </summary>
internal static class WebRoute
{
    /// <summary>The caller when a test names none.</summary>
    public static readonly Guid Listener = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    public static JellywaveWebController Controller(
        Func<PluginConfiguration?> configuration,
        NowPlayingState? state = null,
        LiveItemArtwork? artwork = null,
        LiveItemFinder? finder = null,
        IImageProcessor? images = null,
        StationClient? client = null,
        ScheduleCache? schedule = null,
        RequestReceipts? receipts = null,
        Guid? user = null,
        Channel? channel = null,
        StationTitles? titles = null,
        StationDemand? demand = null,
        StationCalls? calls = null)
    {
        receipts ??= new RequestReceipts();
        client ??= new StationClient(null!);
        var resolver = new StationResolver(receipts, configuration, NullLogger<StationResolver>.Instance);
        var services = new ServiceCollection().AddSingleton(resolver).AddSingleton(demand ?? new StationDemand(new FakePlayingItems()));
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Items[StationAccess.UserIdItem] = user ?? Listener;
        if (channel is not null)
        {
            http.Items[StationAccess.ChannelItem] = channel;
        }

        state ??= new NowPlayingState();
        finder ??= new LiveItemFinder(null!, () => [], NullLogger<LiveItemFinder>.Instance);
        artwork ??= new LiveItemArtwork(new StationClient(null!), (_, _, _, _) => Task.CompletedTask, NullLogger<LiveItemArtwork>.Instance);
        return new JellywaveWebController(
            state,
            finder,
            schedule ?? new ScheduleCache(client),
            receipts,
            new StationList(resolver, finder, state, artwork, images!),
            titles ?? new StationTitles(),
            calls ?? new StationCalls(client, NullLogger<StationCalls>.Instance),
            NullLogger<JellywaveWebController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    /// <summary>
    /// Runs one action: its station route filter with the given bound arguments (an argument left out was not sent),
    /// then, unless the filter answered, the action itself. Returns what MVC would write.
    /// </summary>
    public static async Task<IActionResult> RunAsync(JellywaveWebController controller, string action, params (string Name, object? Value)[] args)
    {
        var method = typeof(JellywaveWebController).GetMethod(action, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new ArgumentException("No action " + action, nameof(action));
        var arguments = args.ToDictionary(a => a.Name, a => a.Value);
        var filter = method.GetCustomAttribute<StationRouteAttribute>();
        if (filter is not null)
        {
            var context = new ActionExecutingContext(
                new ActionContext(controller.HttpContext, new RouteData(), new ActionDescriptor()),
                new List<IFilterMetadata>(),
                arguments,
                controller);
            filter.OnActionExecuting(context);
            if (context.Result is not null)
            {
                return context.Result;
            }
        }

        var values = method.GetParameters()
            .Select(p => p.ParameterType == typeof(CancellationToken) ? CancellationToken.None : arguments.GetValueOrDefault(p.Name!))
            .ToArray();
        var result = method.Invoke(controller, values);
        if (result is Task task)
        {
            await task;
            result = task.GetType().GetProperty("Result")!.GetValue(task);
        }

        return result switch
        {
            IConvertToActionResult convertible => convertible.Convert(),
            IActionResult plain => plain,
            _ => throw new InvalidOperationException("Unexpected action result " + result)
        };
    }

    public static T Value<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<ObjectResult>(result).Value);

    public static int StatusOf(IActionResult result) => result switch
    {
        StatusCodeResult status => status.StatusCode,
        ObjectResult value => value.StatusCode ?? StatusCodes.Status200OK,
        _ => StatusCodes.Status200OK
    };
}
