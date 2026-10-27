using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// The access rules for the station routes. Visibility runs through Jellyfin's real <c>Channel.IsVisible</c>
/// (no tags and no rating on the channel, so the parental part needs none of BaseItem's static managers).
/// </summary>
public class StationAccessTests
{
    private static readonly Guid ChannelId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class FakeStore : IUserStore
    {
        public Dictionary<Guid, User> Users { get; } = new();

        public IEnumerable<Guid> UserIds() => Users.Keys.ToArray();

        public User? GetUser(Guid id) => Users.TryGetValue(id, out var u) ? u : null;

        public Task SaveAsync(User user) => throw new InvalidOperationException("access checks never write");
    }

    private static User NewUser(bool allChannels = false, Guid[]? enabled = null, Guid[]? blocked = null, bool admin = false)
    {
        var u = new User("alice", "Default", "Default");
        u.SetPermission(PermissionKind.EnableAllChannels, allChannels);
        u.SetPermission(PermissionKind.IsAdministrator, admin);
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

    // Claim names and value formats as Jellyfin 12.3 sets them (CustomAuthenticationHandler): the user id in
    // "N" format and IsApiKey as bool.ToString ("True"/"False"). An API key carries an empty user id.
    private static ClaimsPrincipal Caller(Guid? userId, string? isApiKey = "False")
    {
        var claims = new List<Claim>();
        if (isApiKey is not null)
        {
            claims.Add(new Claim("Jellyfin-IsApiKey", isApiKey));
        }

        if (userId is not null)
        {
            claims.Add(new Claim("Jellyfin-UserId", userId.Value.ToString("N", CultureInfo.InvariantCulture)));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static StationAccess Access(FakeStore store, Channel? channel)
        => new(store, () => channel, NullLogger<StationAccess>.Instance);

    private static Channel TheChannel() => new() { Id = ChannelId, Name = "SUB/WAVE" };

    private static (FakeStore Store, User User) StoreWith(User user)
    {
        var store = new FakeStore();
        store.Users[user.Id] = user;
        return (store, user);
    }

    [Fact]
    public void Claim_names_match_jellyfin()
    {
        Assert.Equal("Jellyfin-UserId", StationAccess.UserIdClaim);
        Assert.Equal("Jellyfin-IsApiKey", StationAccess.ApiKeyClaim);
    }

    [Fact]
    public void Api_key_is_refused()
    {
        var (store, _) = StoreWith(NewUser(allChannels: true));
        Assert.Equal(StationAccessReason.ApiKey, Access(store, TheChannel()).Check(Caller(Guid.Empty, "True"), out var id));
        Assert.Equal(Guid.Empty, id);
    }

    [Fact]
    public void Api_key_with_a_user_id_is_still_refused()
    {
        var (store, user) = StoreWith(NewUser(allChannels: true));
        Assert.Equal(StationAccessReason.ApiKey, Access(store, TheChannel()).Check(Caller(user.Id, "True"), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("maybe")]
    public void Missing_or_unreadable_api_key_claim_is_refused(string? value)
    {
        var (store, user) = StoreWith(NewUser(allChannels: true));
        Assert.Equal(StationAccessReason.ApiKey, Access(store, TheChannel()).Check(Caller(user.Id, value), out _));
    }

    [Fact]
    public void Missing_user_id_is_refused()
    {
        var (store, _) = StoreWith(NewUser(allChannels: true));
        Assert.Equal(StationAccessReason.NoUser, Access(store, TheChannel()).Check(Caller(null), out _));
    }

    [Fact]
    public void Empty_user_id_is_refused()
    {
        var (store, _) = StoreWith(NewUser(allChannels: true));
        Assert.Equal(StationAccessReason.NoUser, Access(store, TheChannel()).Check(Caller(Guid.Empty), out _));
    }

    [Fact]
    public void Unknown_user_is_refused()
    {
        var (store, _) = StoreWith(NewUser(allChannels: true));
        Assert.Equal(StationAccessReason.UnknownUser, Access(store, TheChannel()).Check(Caller(Guid.NewGuid()), out _));
    }

    [Fact]
    public void Missing_channel_fails_closed()
    {
        var (store, user) = StoreWith(NewUser(allChannels: true));
        Assert.Equal(StationAccessReason.NoChannel, Access(store, null).Check(Caller(user.Id), out var id));
        Assert.Equal(Guid.Empty, id);
    }

    [Fact]
    public void Channel_in_block_list_is_refused_even_with_all_channels()
    {
        var (store, user) = StoreWith(NewUser(allChannels: true, blocked: [ChannelId]));
        Assert.Equal(StationAccessReason.NotVisible, Access(store, TheChannel()).Check(Caller(user.Id), out _));
    }

    [Fact]
    public void User_without_channel_access_is_refused()
    {
        var (store, user) = StoreWith(NewUser(enabled: [Other]));
        Assert.Equal(StationAccessReason.NotVisible, Access(store, TheChannel()).Check(Caller(user.Id), out _));
    }

    [Fact]
    public void Administrator_without_channel_access_is_refused()
    {
        var (store, user) = StoreWith(NewUser(admin: true));
        Assert.Equal(StationAccessReason.NotVisible, Access(store, TheChannel()).Check(Caller(user.Id), out _));
    }

    [Fact]
    public void All_channels_permission_passes()
    {
        var (store, user) = StoreWith(NewUser(allChannels: true));
        Assert.Equal(StationAccessReason.Allowed, Access(store, TheChannel()).Check(Caller(user.Id), out var id));
        Assert.Equal(user.Id, id);
    }

    [Fact]
    public void Channel_in_enabled_list_passes()
    {
        var (store, user) = StoreWith(NewUser(enabled: [Other, ChannelId]));
        Assert.Equal(StationAccessReason.Allowed, Access(store, TheChannel()).Check(Caller(user.Id), out var id));
        Assert.Equal(user.Id, id);
    }

    [Fact]
    public void Block_list_naming_another_channel_passes()
    {
        var (store, user) = StoreWith(NewUser(blocked: [Other]));
        Assert.Equal(StationAccessReason.Allowed, Access(store, TheChannel()).Check(Caller(user.Id), out _));
    }

    [Fact]
    public void User_id_in_d_format_is_read_too()
    {
        var (store, user) = StoreWith(NewUser(allChannels: true));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("Jellyfin-IsApiKey", "False"), new Claim("Jellyfin-UserId", user.Id.ToString("D", CultureInfo.InvariantCulture))],
            "Test"));
        Assert.Equal(StationAccessReason.Allowed, Access(store, TheChannel()).Check(principal, out _));
    }
}
