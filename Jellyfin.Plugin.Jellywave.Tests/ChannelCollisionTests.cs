using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 15 (D1): when another plugin's channel derives the same channel id, every consumer fails closed through the
/// real <see cref="LiveItemFinder"/>: the poller writes nothing, the granter grants nothing, station access is refused.
/// Each case has a control without the other channel, so a harness that cannot see a write fails too.
/// </summary>
public class ChannelCollisionTests
{
    private const string Station = "SUB/WAVE";

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

    private sealed class Server
    {
        public Server(bool collides)
        {
            Channel = new Channel { Id = Library.IdFor("Channel " + Station, typeof(Channel)), Name = Station };
            Library.Items[Channel.Id] = Channel;
            Library.ListedItems.Add(LiveItem);
            if (collides)
            {
                Channels.Add(OtherPluginChannel.Named(Station));
            }

            Finder = new LiveItemFinder(Library.Manager, () => Channels, NullLogger<LiveItemFinder>.Instance);
        }

        public FakeLibrary Library { get; } = FakeLibrary.Create();

        public List<IChannel> Channels { get; } = new() { OtherPluginChannel.Named("Some Other Radio") };

        public Channel Channel { get; }

        public Audio LiveItem { get; } = new();

        public LiveItemFinder Finder { get; }

        public List<string> ImagesSaved { get; } = new();

        public PollerHealth Health { get; } = new();

        public NowPlayingService Poller()
        {
            var station = new StationClient(null!);   // the idle path never calls the station
            var artwork = new LiveItemArtwork(station, SaveImageAsync, NullLogger<LiveItemArtwork>.Instance);
            return new NowPlayingService(
                station,
                new NowPlayingState(),
                artwork,
                Finder,
                Health,
                Library.Manager,
                null!,
                NullLogger<NowPlayingService>.Instance,
                () => new PluginConfiguration { StationName = Station, NowPlayingEnabled = false });
        }

        private Task SaveImageAsync(BaseItem item, Stream stream, string mime, CancellationToken cancellationToken)
        {
            ImagesSaved.Add(mime);
            return Task.CompletedTask;
        }
    }

    private static User NewUser(bool allChannels = false)
    {
        var u = new User("alice", "Default", "Default");
        u.SetPermission(PermissionKind.EnableAllChannels, allChannels);
        return u;
    }

    private static ClaimsPrincipal Caller(Guid userId)
        => new(new ClaimsIdentity(
            [
                new Claim(StationAccess.ApiKeyClaim, "False"),
                new Claim(StationAccess.UserIdClaim, userId.ToString("N", CultureInfo.InvariantCulture))
            ],
            "Test"));

    private static AccessGranter Granter(Server server, FakeStore store)
        => new(
            store,
            new LibraryChannelLocator(server.Finder, null!),   // the entity exists or collides, so the channel manager is never asked
            NullLogger<AccessGranter>.Instance,
            () => new PluginConfiguration { StationName = Station, StationBaseUrl = "http://radio.example", GrantAccessToAllUsers = true });

    [Fact]
    public async Task Control_without_a_collision_the_poller_writes_the_live_item()
    {
        var server = new Server(collides: false);
        await server.Poller().PollOnceAsync(CancellationToken.None);

        Assert.Equal(new BaseItem[] { server.LiveItem }, server.Library.Updated);
        Assert.Single(server.ImagesSaved);
        Assert.False(server.Health.Snapshot().NameCollision);
    }

    [Fact]
    public async Task On_a_collision_the_poller_writes_nothing_and_health_says_why()
    {
        var server = new Server(collides: true);
        await server.Poller().PollOnceAsync(CancellationToken.None);

        Assert.Empty(server.Library.Updated);
        Assert.Empty(server.ImagesSaved);
        Assert.Null(server.LiveItem.Overview);
        Assert.True(server.Health.Snapshot().NameCollision);
    }

    [Fact]
    public async Task The_health_flag_clears_when_the_collision_ends()
    {
        var server = new Server(collides: true);
        var poller = server.Poller();
        await poller.PollOnceAsync(CancellationToken.None);
        server.Channels.RemoveAll(c => c.Name == Station);

        await poller.PollOnceAsync(CancellationToken.None);

        Assert.False(server.Health.Snapshot().NameCollision);
        Assert.Single(server.Library.Updated);
    }

    [Fact]
    public async Task Control_without_a_collision_the_granter_grants()
    {
        var server = new Server(collides: false);
        var store = new FakeStore();
        var user = NewUser(); store.Users[user.Id] = user;
        using var granter = Granter(server, store);

        var result = await granter.GrantAllAsync(CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.Equal(new[] { user.Id }, store.Saved);
        Assert.True(await granter.GrantAsync(NewUserIn(store), CancellationToken.None));
    }

    [Fact]
    public async Task On_a_collision_the_granter_grants_nothing_and_says_why()
    {
        var server = new Server(collides: true);
        var store = new FakeStore();
        var user = NewUser(); store.Users[user.Id] = user;
        using var granter = Granter(server, store);

        var result = await granter.GrantAllAsync(CancellationToken.None);
        var single = await granter.GrantAsync(user.Id, CancellationToken.None);

        Assert.False(result.Enabled);
        Assert.Equal("channel name collides with another plugin's channel", result.Error);
        Assert.False(single);
        Assert.Empty(store.Saved);
        Assert.Empty(user.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels));
    }

    [Fact]
    public void Control_without_a_collision_station_access_allows()
    {
        var server = new Server(collides: false);
        var store = new FakeStore();
        var user = NewUser(allChannels: true); store.Users[user.Id] = user;

        var result = new StationAccess(store, server.Finder, NullLogger<StationAccess>.Instance).Check(Caller(user.Id));

        Assert.Equal(StationAccessReason.Allowed, result.Reason);
        Assert.Same(server.Channel, result.Channel);
    }

    [Fact]
    public void On_a_collision_station_access_is_refused_even_for_a_user_who_sees_every_channel()
    {
        var server = new Server(collides: true);
        var store = new FakeStore();
        var user = NewUser(allChannels: true); store.Users[user.Id] = user;

        var result = new StationAccess(store, server.Finder, NullLogger<StationAccess>.Instance).Check(Caller(user.Id));

        Assert.Equal(StationAccessReason.NameCollision, result.Reason);
        Assert.Null(result.Channel);
        Assert.Equal(Guid.Empty, result.UserId);
    }

    [Fact]
    public void On_a_collision_the_health_route_carries_the_status_text()
    {
        var server = new Server(collides: true);   // the poller has not run: the status comes from the live lookup
        var station = new StationClient(null!);
        var controller = new JellywaveController(
            station,
            null!,
            new NowPlayingState(),
            server.Health,
            new LiveItemArtwork(station, (_, _, _, _) => Task.CompletedTask, NullLogger<LiveItemArtwork>.Instance),
            server.Finder,
            NullLogger<JellywaveController>.Instance,
            () => new PluginConfiguration { StationName = Station, StationBaseUrl = "http://radio.example" });

        var health = controller.Health().Value!;

        Assert.Equal("channel name collides with another plugin's channel", health.ChannelStatus);
        Assert.False(health.LiveItemExists);
    }

    [Fact]
    public void Without_a_collision_the_health_route_has_no_channel_status()
    {
        var server = new Server(collides: false);
        server.Health.SetNameCollision(true);   // the last poll saw a collision, then the admin renamed the tile
        var station = new StationClient(null!);
        var controller = new JellywaveController(
            station,
            null!,
            new NowPlayingState(),
            server.Health,
            new LiveItemArtwork(station, (_, _, _, _) => Task.CompletedTask, NullLogger<LiveItemArtwork>.Instance),
            server.Finder,
            NullLogger<JellywaveController>.Instance,
            () => new PluginConfiguration { StationName = Station, StationBaseUrl = "http://radio.example" });

        var health = controller.Health().Value!;

        Assert.Null(health.ChannelStatus);
        Assert.True(health.LiveItemExists);
    }

    private static Guid NewUserIn(FakeStore store)
    {
        var user = NewUser();
        store.Users[user.Id] = user;
        return user.Id;
    }
}
