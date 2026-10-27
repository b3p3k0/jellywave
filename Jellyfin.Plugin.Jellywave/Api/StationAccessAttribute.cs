using System;
using Jellyfin.Plugin.Jellywave.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// Puts a station route behind <see cref="StationAccess"/>: 403 unless the caller is a signed-in user who can
/// see the channel. Runs as an authorization filter, after Jellyfin's own <c>[Authorize]</c> and before model
/// binding, so a refused caller's body is never read. An allowed caller's user id is left in
/// <c>HttpContext.Items[<see cref="StationAccess.UserIdItem"/>]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class StationAccessAttribute : Attribute, IAuthorizationFilter
{
    /// <inheritdoc />
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var access = context.HttpContext.RequestServices.GetRequiredService<StationAccess>();
        if (access.Check(context.HttpContext.User, out var userId) != StationAccessReason.Allowed)
        {
            // A plain 403: no X-Application-Error-Code, which jellyfin-web would treat as a parental-control logout.
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return;
        }

        context.HttpContext.Items[StationAccess.UserIdItem] = userId;
    }
}
