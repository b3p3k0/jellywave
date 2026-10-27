using System;
using Jellyfin.Plugin.Jellywave.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// Puts a station route behind <see cref="StationAccess"/>: 403 unless the caller is a signed-in user who can
/// see the channel, 404 while the channel entity does not exist yet. Runs as an authorization filter, after
/// Jellyfin's own <c>[Authorize]</c> and before model binding, so a refused caller's body is never read. An
/// allowed caller's user id and the channel entity are left in <c>HttpContext.Items</c> under
/// <see cref="StationAccess.UserIdItem"/> and <see cref="StationAccess.ChannelItem"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class StationAccessAttribute : Attribute, IAuthorizationFilter
{
    /// <inheritdoc />
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var access = context.HttpContext.RequestServices.GetRequiredService<StationAccess>();
        var result = access.Check(context.HttpContext.User);
        switch (result.Reason)
        {
            case StationAccessReason.Allowed:
                context.HttpContext.Items[StationAccess.UserIdItem] = result.UserId;
                context.HttpContext.Items[StationAccess.ChannelItem] = result.Channel;
                break;
            case StationAccessReason.NoChannel:
                // Fresh install or a rename not yet refreshed: nothing to serve, and the page retries soon.
                context.Result = new StatusCodeResult(StatusCodes.Status404NotFound);
                break;
            case StationAccessReason.ApiKey:
            case StationAccessReason.NoUser:
            case StationAccessReason.UnknownUser:
            case StationAccessReason.NotVisible:
                // A plain 403: no X-Application-Error-Code, which jellyfin-web would treat as a parental-control logout.
                context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
                break;
            default:
                throw new InvalidOperationException("Unknown station access reason " + result.Reason);
        }
    }
}
