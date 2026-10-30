using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Jellywave.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// Finds a station route's station (card L5) through <see cref="StationResolver"/>, the one path every station route
/// takes. An action filter: it runs after <see cref="StationAccessAttribute"/> (an authorization filter, so the channel
/// gate always decides first and a refused caller's body is never read) and after model binding, so it reads the id from
/// the bound <c>stationId</c> query parameter (GET) or the <see cref="IStationSubmission"/> body (POST). An unlisted id
/// answers 404 before the action runs. With <see cref="FromReceipt"/> the station comes from the request receipt instead:
/// another user's request is 403, an unknown one (or one whose station is no longer listed) is "lost track". The station
/// is left in <c>HttpContext.Items</c> under <see cref="StationItem"/>. With <see cref="PlainStream"/> (the title route,
/// card L6) the id resolves to a listed plain stream instead, through <see cref="StationResolver.ResolvePlainStream"/>, and
/// only its id is left, under <see cref="PlainStationItem"/>: a SUB/WAVE route never finds a plain stream, and the title
/// route never finds a SUB/WAVE station. Card L14: a SUB/WAVE station found for a caller counts as having its page open
/// (<see cref="StationDemand.PageOpened"/>), so the poller asks it while the page is open; the title route does not.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class StationRouteAttribute : Attribute, IActionFilter
{
    /// <summary>The name of a GET station route's query parameter carrying the station id.</summary>
    public const string StationIdParameter = "stationId";

    /// <summary>The name of the receipt route's parameter carrying the request id.</summary>
    public const string RequestIdParameter = "requestId";

    /// <summary>The <c>HttpContext.Items</c> key under which the resolved <see cref="StationSource"/> is left for the action.</summary>
    public const string StationItem = "Jellywave.StationRoute.Station";

    /// <summary>The <c>HttpContext.Items</c> key under which a <see cref="PlainStream"/> route's <see cref="PlainStation"/> is left.</summary>
    public const string PlainStationItem = "Jellywave.StationRoute.PlainStation";

    /// <summary>
    /// Gets or sets a value indicating whether the station comes from the request receipt named by the route's
    /// <see cref="RequestIdParameter"/> (the request status route) rather than from the caller.
    /// </summary>
    public bool FromReceipt { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the route serves a listed plain stream (the title route, card L6) rather than
    /// a SUB/WAVE station. Its station id is required (no primary station), and SUB/WAVE, disabled, refused and unknown ids
    /// are 404.
    /// </summary>
    public bool PlainStream { get; set; }

    /// <inheritdoc />
    public void OnActionExecuting(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var http = context.HttpContext;

        // The channel gate must have allowed this caller; without it there is nothing to resolve for (fail closed).
        if (!http.Items.TryGetValue(StationAccess.UserIdItem, out var user) || user is not Guid userId)
        {
            throw new InvalidOperationException("The station access filter did not run for this action.");
        }

        var resolver = http.RequestServices.GetRequiredService<StationResolver>();
        if (PlainStream)
        {
            var stream = resolver.ResolvePlainStream(StationIdOf(context.ActionArguments));
            if (stream is null)
            {
                context.Result = new StatusCodeResult(StatusCodes.Status404NotFound);
                return;
            }

            http.Items[PlainStationItem] = new PlainStation(stream.Station.Id);
            return;
        }

        if (FromReceipt)
        {
            var requestId = context.ActionArguments.TryGetValue(RequestIdParameter, out var raw) ? raw as string : null;
            switch (resolver.ResolveReceipt(requestId, userId, out var owned))
            {
                case ReceiptOwnership.Owner:
                    http.Items[StationItem] = owned;
                    PageOpened(http, owned);
                    break;
                case ReceiptOwnership.OtherUser:
                    context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
                    break;
                case ReceiptOwnership.Unknown:
                    context.Result = new ObjectResult(RequestOutcome.LostTrack(requestId ?? string.Empty)) { DeclaredType = typeof(RequestOutcome) };
                    break;
                default:
                    throw new InvalidOperationException("Unknown receipt ownership");
            }

            return;
        }

        var station = resolver.Resolve(StationIdOf(context.ActionArguments));
        if (station is null)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status404NotFound);
            return;
        }

        http.Items[StationItem] = station;
        PageOpened(http, station);
    }

    /// <inheritdoc />
    public void OnActionExecuted(ActionExecutedContext context)
    {
    }

    /// <summary>
    /// Card L14: a signed-in user who can see the tile called a route of this station, so its page is open.
    /// </summary>
    private static void PageOpened(HttpContext http, StationSource? station)
    {
        if (station is not null)
        {
            http.RequestServices.GetRequiredService<StationDemand>().PageOpened(station.Station.Id);
        }
    }

    /// <summary>
    /// The caller's station id: the bound query parameter, else the body's. Null when the caller sent none.
    /// </summary>
    /// <param name="arguments">The action's bound arguments.</param>
    /// <returns>The id, or null.</returns>
    internal static string? StationIdOf(IDictionary<string, object?> arguments)
    {
        if (arguments.TryGetValue(StationIdParameter, out var query))
        {
            return query as string;
        }

        return arguments.Values.OfType<IStationSubmission>().FirstOrDefault()?.StationId;
    }
}
