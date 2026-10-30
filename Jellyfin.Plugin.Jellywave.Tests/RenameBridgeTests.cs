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
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
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
/// Card L1, the rename bridge's read-only half: a user whose block list names an earlier tile name's id is kept from the
/// renamed tile (<c>IChannel.IsEnabledFor</c>, which Jellyfin 12.3's <c>ChannelManager.GetChannelsInternalAsync</c> asks
/// after <c>IsVisible</c>) and from the station routes (<see cref="StationAccess"/>, 403), although Jellyfin's own
/// <c>Channel.IsVisible</c> passes the new id. Ids come from Jellyfin 12.3's rule (<see cref="FakeLibrary.IdFor"/>).
/// </summary>
public class RenameBridgeTests
{
    private const string OldTile = "IHateRadio";

    private sealed class FakeStore : IUserStore
    {
        public Dictionary<Guid, User> Users { get; } = new();

        public int Reads { get; private set; }

        public bool Throw { get; set; }

        public IEnumerable<Guid> UserIds() => Users.Keys.ToArray();

        public User? GetUser(Guid id)
        {
            Reads++;
            if (Throw)
            {
                throw new InvalidOperationException("database gone");
            }

            return Users.TryGetValue(id, out var u) ? u : null;
        }

        public Task SaveAsync(User user) => throw new InvalidOperationException("the bridge never writes");
    }

    private static readonly FakeLibrary Library = FakeLibrary.Create();

    private static Guid Id(string name) => Library.IdFor("Channel " + name, typeof(Channel));

    private static Guid NewId => Id(PluginConfiguration.DefaultLibraryName);

    private static Guid OldId => Id(OldTile);

    private static RenameBridge Bridge() => new(name => Id(name));

    private static PluginConfiguration Renamed(params string[] previous)
    {
        var config = new PluginConfiguration { SchemaVersion = 1 };
        foreach (var name in previous.Length == 0 ? [OldTile] : previous)
        {
            config.PreviousChannelNames.Add(name);
        }

        return config;
    }

    private static User NewUser(bool allChannels = false, Guid[]? enabled = null, Guid[]? blocked = null)
    {
        var u = new User("alice", "Default", "Default");
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

    private static JellywaveChannel Channel(FakeStore store, PluginConfiguration config)
        => new(NullLogger<JellywaveChannel>.Instance, new NowPlayingState(), store, Bridge(), () => config);

    private static string N(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private static (FakeStore Store, User User) StoreWith(User user)
    {
        var store = new FakeStore();
        store.Users[user.Id] = user;
        return (store, user);
    }

    private static ClaimsPrincipal Caller(Guid userId)
        => new(new ClaimsIdentity(
            [
                new Claim(StationAccess.ApiKeyClaim, "False"),
                new Claim(StationAccess.UserIdClaim, N(userId))
            ],
            "Test"));

    private static StationAccess Access(FakeStore store, PluginConfiguration config, Channel channel)
    {
        var bridge = Bridge();
        return new StationAccess(store, () => ChannelResolution.Found(channel), user => bridge.BlocksPrevious(user, config), NullLogger<StationAccess>.Instance);
    }

    private static Channel NewChannel() => new() { Id = NewId, Name = PluginConfiguration.DefaultLibraryName };

    [Fact]
    public void Control_jellyfin_itself_shows_the_renamed_tile_to_a_user_blocked_from_the_old_one()
    {
        // The reason for the bridge: with a block list present IsVisible only checks the list, which names the old id.
        var user = NewUser(blocked: [OldId]);
        Assert.True(NewChannel().IsVisible(user));
    }

    [Fact]
    public void Is_enabled_for_hides_the_tile_from_a_user_blocked_from_an_earlier_tile()
    {
        var (store, user) = StoreWith(NewUser(blocked: [OldId]));
        Assert.False(Channel(store, Renamed()).IsEnabledFor(N(user.Id)));
    }

    [Fact]
    public void Is_enabled_for_honours_every_earlier_name()
    {
        var (store, user) = StoreWith(NewUser(blocked: [Id("SUB/WAVE")]));
        Assert.False(Channel(store, Renamed(OldTile, "SUB/WAVE")).IsEnabledFor(N(user.Id)));
    }

    [Theory]
    [InlineData(true, false)]    // EnableAllChannels, no lists
    [InlineData(false, true)]    // old tile by name in the enabled list
    public void Is_enabled_for_passes_a_user_without_an_old_block(bool allChannels, bool enabledOld)
    {
        var (store, user) = StoreWith(NewUser(allChannels: allChannels, enabled: enabledOld ? [OldId] : null));
        Assert.True(Channel(store, Renamed()).IsEnabledFor(N(user.Id)));
    }

    [Fact]
    public void Is_enabled_for_passes_a_block_list_naming_another_channel()
    {
        var (store, user) = StoreWith(NewUser(blocked: [Guid.Parse("22222222-2222-2222-2222-222222222222")]));
        Assert.True(Channel(store, Renamed()).IsEnabledFor(N(user.Id)));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("{0e9d0c8c-3b1e-4f53-9d0b-8f0f5b0e9a11}")]   // Jellyfin passes "N"; anything else is refused
    public void Is_enabled_for_fails_closed_on_an_unusable_id(string? userId)
    {
        var (store, _) = StoreWith(NewUser(allChannels: true));
        Assert.False(Channel(store, Renamed()).IsEnabledFor(userId!));
    }

    [Fact]
    public void Is_enabled_for_refuses_an_existing_user_named_in_another_format()
    {
        // Jellyfin passes "N"; a "D" or braced id of a real, unblocked user is still refused (strict parse).
        var (store, user) = StoreWith(NewUser(allChannels: true));
        Assert.False(Channel(store, Renamed()).IsEnabledFor(user.Id.ToString("D", CultureInfo.InvariantCulture)));
        Assert.False(Channel(store, Renamed()).IsEnabledFor(user.Id.ToString("B", CultureInfo.InvariantCulture)));
        Assert.True(Channel(store, Renamed()).IsEnabledFor(N(user.Id)));
    }

    [Fact]
    public void Is_enabled_for_fails_closed_on_an_unknown_user_or_a_failing_read()
    {
        var (store, user) = StoreWith(NewUser(allChannels: true));
        Assert.False(Channel(store, Renamed()).IsEnabledFor(N(Guid.NewGuid())));

        store.Throw = true;
        Assert.False(Channel(store, Renamed()).IsEnabledFor(N(user.Id)));
    }

    [Fact]
    public void Without_earlier_names_the_user_is_not_read()
    {
        // Kept cheap: Jellyfin asks for every channel listing.
        var (store, user) = StoreWith(NewUser(blocked: [OldId]));
        store.Throw = true;

        Assert.True(Channel(store, new PluginConfiguration { SchemaVersion = 1 }).IsEnabledFor(N(user.Id)));
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public void An_earlier_name_that_derives_the_current_id_is_ignored()
    {
        // A rename back to an old name: the current id is Jellyfin's own business (IsVisible), not the bridge's.
        var bridge = Bridge();
        var config = Renamed(OldTile, PluginConfiguration.DefaultLibraryName, OldTile);
        Assert.Equal(new[] { OldId }, bridge.PreviousIds(config));
        Assert.Equal(new[] { OldTile }, bridge.PreviousNames(config));
    }

    [Fact]
    public void Previous_ids_follow_the_servers_case_rule()
    {
        var library = FakeLibrary.Create();
        library.CaseSensitiveIds = false;
        var bridge = new RenameBridge(name => ChannelIds.Derive(library.Manager, name));
        var config = Renamed("SUB/WAVE", "sub/wave", "Live radio");

        Assert.Equal(new[] { library.IdFor("Channel sub/wave", typeof(Channel)) }, bridge.PreviousIds(config));
    }

    [Fact]
    public void Carried_names_still_hide_the_tile()
    {
        // S4 changes only what the carry-over proposes; the hiding side uses every earlier name.
        var config = Renamed();
        config.CarriedChannelNames.Add(OldTile);
        var (store, user) = StoreWith(NewUser(blocked: [OldId]));
        Assert.False(Channel(store, config).IsEnabledFor(N(user.Id)));
    }

    [Fact]
    public void An_unmigrated_configuration_still_honours_its_old_tile()
    {
        // S1: when the migration failed, the plugin runs the file as loaded (schema 0); the 0.4.x tile name counts as earlier.
        var config = new PluginConfiguration { StationName = OldTile };
        var (store, user) = StoreWith(NewUser(blocked: [OldId]));

        Assert.Equal(new[] { OldId }, Bridge().PreviousIds(config));
        Assert.False(Channel(store, config).IsEnabledFor(N(user.Id)));
        Assert.Empty(Bridge().PreviousIds(new PluginConfiguration { StationName = OldTile, SchemaVersion = 1 }));
    }

    [Fact]
    public void Station_access_refuses_a_user_blocked_from_an_earlier_tile()
    {
        var (store, user) = StoreWith(NewUser(blocked: [OldId]));
        var result = Access(store, Renamed(), NewChannel()).Check(Caller(user.Id));

        Assert.Equal(StationAccessReason.PreviousChannelBlocked, result.Reason);
        Assert.Equal(Guid.Empty, result.UserId);
        Assert.Null(result.Channel);
    }

    [Fact]
    public void Station_access_passes_a_user_without_an_old_block()
    {
        var (store, user) = StoreWith(NewUser(allChannels: true));
        var result = Access(store, Renamed(), NewChannel()).Check(Caller(user.Id));

        Assert.Equal(StationAccessReason.Allowed, result.Reason);
        Assert.Equal(user.Id, result.UserId);
    }

    [Fact]
    public void Station_access_still_applies_jellyfins_own_rule_after_the_bridge()
    {
        var (store, user) = StoreWith(NewUser(enabled: [OldId]));   // had the old tile by name, not carried over yet
        Assert.Equal(StationAccessReason.NotVisible, Access(store, Renamed(), NewChannel()).Check(Caller(user.Id)).Reason);
    }

    [Fact]
    public void The_filter_answers_403_for_an_old_block()
    {
        var (store, user) = StoreWith(NewUser(blocked: [OldId]));
        var access = Access(store, Renamed(), NewChannel());
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(access).BuildServiceProvider(),
            User = Caller(user.Id)
        };
        var context = new AuthorizationFilterContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), []);

        new StationAccessAttribute().OnAuthorization(context);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.False(http.Response.Headers.ContainsKey("X-Application-Error-Code"));
    }
}
