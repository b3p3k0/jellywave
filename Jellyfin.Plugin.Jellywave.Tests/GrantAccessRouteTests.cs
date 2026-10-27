using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 15b: <c>POST /Jellywave/GrantAccess</c> is the only way the plugin writes user policies. It is admin-only,
/// needs no setting (pressing the button means "do it now") and reports the counts the page shows.
/// </summary>
public class GrantAccessRouteTests
{
    private static readonly Guid ChannelId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed class FakeStore : IUserStore
    {
        public Dictionary<Guid, User> Users { get; } = new();

        public List<Guid> Saved { get; } = new();

        public IEnumerable<Guid> UserIds() => Users.Keys.ToArray();

        public User? GetUser(Guid id) => Users.TryGetValue(id, out var u) ? u : null;

        public Task SaveAsync(User user)
        {
            Saved.Add(user.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedLocator : IChannelLocator
    {
        public Task<ChannelResolution> LocateAsync(PluginConfiguration config, CancellationToken cancellationToken)
            => Task.FromResult(ChannelResolution.Found(new Channel { Id = ChannelId }));
    }

    private static User NewUser(string name, bool allChannels = false, Guid[]? blocked = null)
    {
        var u = new User(name, "Default", "Default");
        u.SetPermission(PermissionKind.EnableAllChannels, allChannels);
        if (blocked is not null)
        {
            u.SetPreference(PreferenceKind.BlockedChannels, blocked);
        }

        return u;
    }

    private static JellywaveController Controller(AccessGranter granter, PluginConfiguration config)
    {
        var station = new StationClient(null!);   // never called by GrantAccess
        return new JellywaveController(
            station,
            granter,
            new NowPlayingState(),
            new PollerHealth(),
            new LiveItemArtwork(station, (_, _, _, _) => Task.CompletedTask, NullLogger<LiveItemArtwork>.Instance),
            new LiveItemFinder(FakeLibrary.Create().Manager, () => [], NullLogger<LiveItemFinder>.Instance),
            NullLogger<JellywaveController>.Instance,
            () => config);
    }

    [Fact]
    public async Task The_route_grants_and_returns_the_counts_with_a_default_configuration()
    {
        var store = new FakeStore();
        var plain = NewUser("plain");
        var all = NewUser("all", allChannels: true);
        var blocked = NewUser("blocked", blocked: [ChannelId]);
        foreach (var u in new[] { plain, all, blocked })
        {
            store.Users[u.Id] = u;
        }

        var config = new PluginConfiguration();   // nothing to switch on: the press is the request
        using var granter = new AccessGranter(store, new FixedLocator(), NullLogger<AccessGranter>.Instance, () => config);

        var result = (await Controller(granter, config).GrantAccess(CancellationToken.None)).Value!;

        Assert.Null(result.Error);
        Assert.Equal(1, result.Granted);
        Assert.Equal(1, result.AlreadyVisible);
        Assert.Equal(1, result.Blocked);
        Assert.Equal(new[] { plain.Id }, store.Saved);
        Assert.Equal(new[] { ChannelId }, plain.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels));
        Assert.Empty(blocked.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels));
    }

    [Fact]
    public void The_route_is_an_admin_only_post()
    {
        var method = typeof(JellywaveController).GetMethod(nameof(JellywaveController.GrantAccess))!;
        var post = method.GetCustomAttribute<HttpPostAttribute>();

        Assert.NotNull(post);
        Assert.Equal("GrantAccess", post.Template);
        Assert.Contains(
            typeof(JellywaveController).GetCustomAttributes<AuthorizeAttribute>(),
            a => a.Policy == Policies.RequiresElevation);
    }
}
