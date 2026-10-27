using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// The filter turns a refusal into a plain 403 (404 while the channel is missing) and hands an allowed caller's
/// id and the channel entity to the action.
/// </summary>
public class StationAccessAttributeTests
{
    private static readonly Guid ChannelId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed class FakeStore : IUserStore
    {
        public Dictionary<Guid, User> Users { get; } = new();

        public IEnumerable<Guid> UserIds() => Users.Keys.ToArray();

        public User? GetUser(Guid id) => Users.TryGetValue(id, out var u) ? u : null;

        public Task SaveAsync(User user) => throw new InvalidOperationException("access checks never write");
    }

    private static readonly Channel TheChannel = new() { Id = ChannelId };

    private static AuthorizationFilterContext Run(User user, bool apiKey, bool channelExists = true)
    {
        var store = new FakeStore();
        store.Users[user.Id] = user;
        var access = new StationAccess(store, () => channelExists ? TheChannel : null, NullLogger<StationAccess>.Instance);
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(access).BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("Jellyfin-IsApiKey", apiKey ? "True" : "False"),
                    new Claim("Jellyfin-UserId", user.Id.ToString("N", CultureInfo.InvariantCulture))
                ],
                "Test"))
        };
        var context = new AuthorizationFilterContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>());
        new StationAccessAttribute().OnAuthorization(context);
        return context;
    }

    private static User Listener(bool allChannels)
    {
        var u = new User("bob", "Default", "Default");
        u.SetPermission(PermissionKind.EnableAllChannels, allChannels);
        return u;
    }

    [Fact]
    public void Allowed_caller_passes_with_its_user_id()
    {
        var user = Listener(allChannels: true);
        var context = Run(user, apiKey: false);
        Assert.Null(context.Result);
        Assert.Equal(user.Id, context.HttpContext.Items[StationAccess.UserIdItem]);
        Assert.Same(TheChannel, context.HttpContext.Items[StationAccess.ChannelItem]);
    }

    [Fact]
    public void Missing_channel_is_404_and_serves_nothing()
    {
        var context = Run(Listener(allChannels: true), apiKey: false, channelExists: false);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.False(context.HttpContext.Items.ContainsKey(StationAccess.UserIdItem));
        Assert.False(context.HttpContext.Items.ContainsKey(StationAccess.ChannelItem));
    }

    [Fact]
    public void Hidden_channel_is_403()
    {
        var context = Run(Listener(allChannels: false), apiKey: false);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.False(context.HttpContext.Items.ContainsKey(StationAccess.UserIdItem));
        Assert.False(context.HttpContext.Items.ContainsKey(StationAccess.ChannelItem));
    }

    [Fact]
    public void Api_key_is_403()
    {
        var context = Run(Listener(allChannels: true), apiKey: true);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.False(context.HttpContext.Items.ContainsKey(StationAccess.UserIdItem));
    }
}
