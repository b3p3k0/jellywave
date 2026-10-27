using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

public class AccessGranterPassTests
{
    private static readonly Guid Channel = Guid.Parse("11111111-1111-1111-1111-111111111111");

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

    private sealed class FakeLocator : IChannelLocator
    {
        public Guid? Id { get; set; } = Channel;

        public Task<ChannelResolution> LocateAsync(PluginConfiguration config, CancellationToken cancellationToken)
            => Task.FromResult(Id is { } id ? ChannelResolution.Found(new MediaBrowser.Controller.Channels.Channel { Id = id }) : ChannelResolution.Missing);
    }

    private static User NewUser(string name, bool allChannels = false, Guid[]? enabled = null, Guid[]? blocked = null)
    {
        var u = new User(name, "Default", "Default");
        u.SetPermission(PermissionKind.EnableAllChannels, allChannels);
        if (enabled is not null)
        {
            u.SetPreference(PreferenceKind.EnabledChannels, enabled);
        }

        if (blocked is not null)
        {
            u.SetPreference(PreferenceKind.BlockedChannels, blocked);
        }

        return u;
    }

    private static AccessGranter Granter(FakeStore store, FakeLocator locator)
        => new(store, locator, NullLogger<AccessGranter>.Instance, () => new PluginConfiguration { StationBaseUrl = "http://radio.example" });

    [Fact]
    public async Task No_configuration_writes_nothing()
    {
        var store = new FakeStore();
        var u = NewUser("plain"); store.Users[u.Id] = u;
        var granter = new AccessGranter(store, new FakeLocator(), NullLogger<AccessGranter>.Instance, () => null);
        var result = await granter.GrantAllAsync(CancellationToken.None);
        Assert.Equal(AccessGranter.NotReadyError, result.Error);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task Unknown_channel_writes_nothing()
    {
        var store = new FakeStore();
        var u = NewUser("plain"); store.Users[u.Id] = u;
        var result = await Granter(store, new FakeLocator { Id = null }).GrantAllAsync(CancellationToken.None);
        Assert.Equal(AccessGranter.MissingChannelError, result.Error);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task Pass_saves_exactly_the_users_who_need_it_and_counts_the_rest()
    {
        var store = new FakeStore();
        var plain = NewUser("plain");
        var all = NewUser("all", allChannels: true);
        var has = NewUser("has", enabled: [Channel]);
        var blocked = NewUser("blocked", blocked: [Channel]);
        foreach (var u in new[] { plain, all, has, blocked })
        {
            store.Users[u.Id] = u;
        }

        var result = await Granter(store, new FakeLocator()).GrantAllAsync(CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(1, result.Granted);
        Assert.Equal(2, result.AlreadyVisible);
        Assert.Equal(1, result.Blocked);
        Assert.Equal(new[] { plain.Id }, store.Saved);
        Assert.Equal(new[] { Channel }, plain.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels));
        Assert.False(plain.HasPermission(PermissionKind.EnableAllChannels));
        Assert.Empty(blocked.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels));
    }

    [Fact]
    public async Task Second_pass_is_a_no_op()
    {
        var store = new FakeStore();
        var plain = NewUser("plain"); store.Users[plain.Id] = plain;
        var granter = Granter(store, new FakeLocator());
        await granter.GrantAllAsync(CancellationToken.None);
        var again = await granter.GrantAllAsync(CancellationToken.None);
        Assert.Equal(0, again.Granted);
        Assert.Single(store.Saved);
    }

    [Fact]
    public async Task Pass_adds_only_this_channel_and_keeps_the_rest_of_the_list()
    {
        var store = new FakeStore();
        var other = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var u = NewUser("other", enabled: [other]); store.Users[u.Id] = u;

        var result = await Granter(store, new FakeLocator()).GrantAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Granted);
        Assert.Equal(new[] { other, Channel }, u.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels));
        Assert.False(u.HasPermission(PermissionKind.EnableAllChannels));
    }
}
