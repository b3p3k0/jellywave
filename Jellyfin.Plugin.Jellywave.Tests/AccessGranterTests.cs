using System;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Jellywave.Services;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

public class AccessGranterTests
{
    private static readonly Guid Channel = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static User NewUser() => new("alice", "Default", "Default");

    [Fact]
    public void Plain_user_needs_a_grant()
    {
        var u = NewUser();
        u.SetPermission(PermissionKind.EnableAllChannels, false);
        Assert.Equal(AccessGranter.Standing.NeedsGrant, AccessGranter.Classify(u, Channel));
    }

    [Fact]
    public void All_channels_permission_is_visible()
    {
        var u = NewUser();
        u.SetPermission(PermissionKind.EnableAllChannels, true);
        Assert.Equal(AccessGranter.Standing.Visible, AccessGranter.Classify(u, Channel));
    }

    [Fact]
    public void Channel_in_enabled_list_is_visible()
    {
        var u = NewUser();
        u.SetPermission(PermissionKind.EnableAllChannels, false);
        u.SetPreference(PreferenceKind.EnabledChannels, [Other, Channel]);
        Assert.Equal(AccessGranter.Standing.Visible, AccessGranter.Classify(u, Channel));
    }

    [Fact]
    public void Explicit_block_of_this_channel_is_respected()
    {
        var u = NewUser();
        u.SetPermission(PermissionKind.EnableAllChannels, true);   // Jellyfin ignores this when a block list exists
        u.SetPreference(PreferenceKind.BlockedChannels, [Channel]);
        Assert.Equal(AccessGranter.Standing.Blocked, AccessGranter.Classify(u, Channel));
    }

    [Fact]
    public void Block_list_naming_another_channel_means_visible()
    {
        var u = NewUser();
        u.SetPermission(PermissionKind.EnableAllChannels, false);
        u.SetPreference(PreferenceKind.BlockedChannels, [Other]);
        Assert.Equal(AccessGranter.Standing.Visible, AccessGranter.Classify(u, Channel));
    }

    [Fact]
    public void Grant_adds_only_this_channel_and_keeps_the_rest()
    {
        var u = NewUser();
        u.SetPermission(PermissionKind.EnableAllChannels, false);
        u.SetPreference(PreferenceKind.EnabledChannels, [Other]);
        AccessGranter.AddToEnabledChannels(u, Channel);
        Assert.Equal(new[] { Other, Channel }, u.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels));
        Assert.False(u.HasPermission(PermissionKind.EnableAllChannels));
        AccessGranter.AddToEnabledChannels(u, Channel);   // idempotent
        Assert.Equal(2, u.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels).Length);
    }
}
