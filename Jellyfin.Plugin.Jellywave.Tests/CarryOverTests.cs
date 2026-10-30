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
using MediaBrowser.Controller.Channels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L1, the rename bridge's one-shot half: "Give them access to the renamed tile" adds the current channel id wherever an earlier tile
/// name's id is in a user's enabled list or block list. Add-only, under the granter's gate, each user re-read right
/// before the save; nothing on a collision or a missing channel.
/// </summary>
public class CarryOverTests
{
    private const string OldTile = "IHateRadio";

    private static readonly FakeLibrary Library = FakeLibrary.Create();
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class FakeStore : IUserStore
    {
        public Dictionary<Guid, User> Users { get; } = new();

        public List<string> Events { get; } = new();

        public List<User> SavedEntities { get; } = new();

        public bool ReadOnly { get; set; }

        /// <summary>Gets or sets a replacement handed out on the next read of a user (a dashboard save after the snapshot).</summary>
        public Dictionary<Guid, User> ReplaceOnRead { get; } = new();

        public IEnumerable<Guid> UserIds()
        {
            Events.Add("snapshot");
            return Users.Keys.ToArray();
        }

        public User? GetUser(Guid id)
        {
            if (ReplaceOnRead.Remove(id, out var replacement))
            {
                Users[id] = replacement;
            }

            Events.Add("read " + id);
            return Users.TryGetValue(id, out var u) ? u : null;
        }

        public Task SaveAsync(User user)
        {
            if (ReadOnly)
            {
                throw new InvalidOperationException("this path must not write");
            }

            Events.Add("save " + user.Id);
            SavedEntities.Add(user);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLocator : IChannelLocator
    {
        public ChannelResolution Resolution { get; set; } = ChannelResolution.Found(new Channel { Id = NewId, Name = PluginConfiguration.DefaultLibraryName });

        public Task<ChannelResolution> LocateAsync(PluginConfiguration config, CancellationToken cancellationToken) => Task.FromResult(Resolution);
    }

    private sealed class RecordingLogger : ILogger<AccessGranter>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(logLevel + " " + formatter(state, exception));
    }

    private static Guid Id(string name) => Library.IdFor("Channel " + name, typeof(Channel));

    private static Guid NewId => Id(PluginConfiguration.DefaultLibraryName);

    private static Guid OldId => Id(OldTile);

    private static PluginConfiguration Renamed() => new()
    {
        SchemaVersion = 1,
        PreviousChannelNames = { OldTile },
        Stations = { new StationDefinition { Id = "live", BaseUrl = "http://radio.example" } }
    };

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

    private static AccessGranter Granter(FakeStore store, FakeLocator? locator = null, PluginConfiguration? config = null, ILogger<AccessGranter>? logger = null)
    {
        var configuration = config ?? Renamed();
        return new AccessGranter(store, locator ?? new FakeLocator(), logger ?? NullLogger<AccessGranter>.Instance, () => configuration, new RenameBridge(Id));
    }

    private static FakeStore StoreWith(params User[] users)
    {
        var store = new FakeStore();
        foreach (var u in users)
        {
            store.Users[u.Id] = u;
        }

        return store;
    }

    private static Guid[] Enabled(User u) => u.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels);

    private static Guid[] Blocked(User u) => u.GetPreferenceValues<Guid>(PreferenceKind.BlockedChannels);

    [Fact]
    public async Task Grants_and_blocks_naming_the_old_tile_are_carried_and_the_rest_is_counted()
    {
        var granted = NewUser("granted", enabled: [Other, OldId]);
        var blocked = NewUser("blocked", blocked: [OldId]);
        var both = NewUser("both", enabled: [OldId], blocked: [OldId]);
        var current = NewUser("current", enabled: [OldId, NewId]);
        var all = NewUser("all", allChannels: true);
        var unrelated = NewUser("unrelated", enabled: [Other]);
        var store = StoreWith(granted, blocked, both, current, all, unrelated);

        var result = await Granter(store).CarryOverAsync(CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(2, result.CarriedGrants);
        Assert.Equal(2, result.CarriedBlocks);
        Assert.Equal(1, result.AlreadyCurrent);
        Assert.Equal(2, result.Untouched);
        Assert.Equal(new[] { granted.Id, blocked.Id, both.Id }.OrderBy(i => i), store.SavedEntities.Select(u => u.Id).OrderBy(i => i));

        Assert.Equal(new[] { Other, OldId, NewId }, Enabled(granted));
        Assert.Empty(Blocked(granted));
        Assert.Equal(new[] { OldId, NewId }, Blocked(blocked));
        Assert.Empty(Enabled(blocked));
        Assert.Equal(new[] { OldId, NewId }, Enabled(both));
        Assert.Equal(new[] { OldId, NewId }, Blocked(both));
    }

    [Fact]
    public async Task The_carry_over_only_adds()
    {
        var u = NewUser("u", enabled: [Other, OldId], blocked: [Other]);
        var store = StoreWith(u);

        await Granter(store).CarryOverAsync(CancellationToken.None);

        Assert.Equal(new[] { Other, OldId, NewId }, Enabled(u));   // nothing removed, the old id stays
        Assert.Equal(new[] { Other }, Blocked(u));                   // no old block, so no new block
        Assert.False(u.HasPermission(PermissionKind.EnableAllChannels));
    }

    [Fact]
    public async Task A_second_run_carries_nothing_and_reads_no_user()
    {
        var store = StoreWith(NewUser("granted", enabled: [OldId]), NewUser("blocked", blocked: [OldId]));
        var config = Renamed();
        var granter = Granter(store, config: config);

        await granter.CarryOverAsync(CancellationToken.None);
        Assert.Equal(new[] { OldTile }, config.CarriedChannelNames);
        var saves = store.SavedEntities.Count;
        store.Events.Clear();
        var again = await granter.CarryOverAsync(CancellationToken.None);

        Assert.Null(again.Error);
        Assert.Equal(0, again.CarriedGrants);
        Assert.Equal(0, again.CarriedBlocks);
        Assert.Equal(saves, store.SavedEntities.Count);
        Assert.Empty(store.Events);
    }

    [Fact]
    public async Task An_admin_removal_after_the_carry_over_is_never_proposed_again()
    {
        // S4: the carry-over is one-shot. Later taking the tile away from a user who had the old tile by name must not
        // bring back the notice or let a second press re-add it.
        var granted = NewUser("granted", enabled: [OldId]);
        var store = StoreWith(granted);
        var config = Renamed();
        var granter = Granter(store, config: config);
        await granter.CarryOverAsync(CancellationToken.None);

        granted.SetPreference(PreferenceKind.EnabledChannels, [OldId]);   // the admin unticks the new tile

        Assert.Equal(0, granter.CarryOverStatus().Pending);
        Assert.Empty(granter.CarryOverStatus().PreviousNames);
        var again = await granter.CarryOverAsync(CancellationToken.None);
        Assert.Equal(0, again.CarriedGrants);
        Assert.Equal(new[] { OldId }, Enabled(granted));
    }

    [Fact]
    public async Task A_later_rename_is_carried_without_redoing_the_first()
    {
        var store = StoreWith(NewUser("old", enabled: [OldId]));
        var config = Renamed();
        var granter = Granter(store, config: config);
        await granter.CarryOverAsync(CancellationToken.None);

        // Live Radio -> Kevin Radio: only the outgoing "Live Radio" is pending.
        config.PreviousChannelNames.Add(PluginConfiguration.DefaultLibraryName);
        config.LibraryName = "Kevin Radio";
        var status = granter.CarryOverStatus();
        Assert.Equal(new[] { PluginConfiguration.DefaultLibraryName }, status.PreviousNames);
        Assert.Equal(1, status.Pending);
    }

    [Fact]
    public async Task A_run_that_fails_on_a_save_is_not_recorded_as_carried()
    {
        // F-S2 (R01): the names are recorded only after every user was handled, so a broken run is offered again.
        var store = new ThrowingStore();
        foreach (var u in new[] { NewUser("a", enabled: [OldId]), NewUser("b", enabled: [OldId]) })
        {
            store.Users[u.Id] = u;
        }

        var config = Renamed();
        var granter = new AccessGranter(store, new FakeLocator(), NullLogger<AccessGranter>.Instance, () => config, new RenameBridge(Id));

        await Assert.ThrowsAsync<InvalidOperationException>(() => granter.CarryOverAsync(CancellationToken.None));

        Assert.Equal(2, store.Saves);   // the first user saved, the second save threw
        Assert.Empty(config.CarriedChannelNames);
        Assert.Equal(new[] { OldTile }, granter.CarryOverStatus().PreviousNames);   // still offered
    }

    private sealed class ThrowingStore : IUserStore
    {
        public Dictionary<Guid, User> Users { get; } = new();

        public int Saves { get; private set; }

        public IEnumerable<Guid> UserIds() => Users.Keys.ToArray();

        public User? GetUser(Guid id) => Users.TryGetValue(id, out var u) ? u : null;

        public Task SaveAsync(User user)
        {
            if (++Saves == 2)
            {
                throw new InvalidOperationException("database gone");
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_failed_run_is_not_recorded_as_carried()
    {
        var store = StoreWith(NewUser("granted", enabled: [OldId]));
        var config = Renamed();
        var granter = Granter(store, new FakeLocator { Resolution = ChannelResolution.Missing }, config);

        await granter.CarryOverAsync(CancellationToken.None);

        Assert.Empty(config.CarriedChannelNames);
    }

    [Fact]
    public async Task An_earlier_name_that_is_now_another_plugins_channel_is_not_carried_but_still_hides()
    {
        // S2: copying grants or blocks of that id would act on the other plugin's channel.
        var granted = NewUser("granted", enabled: [OldId]);
        var blocked = NewUser("blocked", blocked: [OldId]);
        var store = StoreWith(granted, blocked);
        var config = Renamed();
        var bridge = new RenameBridge(Id, () => new[] { OldId });
        var granter = new AccessGranter(store, new FakeLocator(), NullLogger<AccessGranter>.Instance, () => config, bridge);

        var status = granter.CarryOverStatus();
        Assert.Equal(1, status.SkippedOtherChannel);
        Assert.Equal(0, status.Pending);
        Assert.Empty(status.PreviousNames);

        var result = await granter.CarryOverAsync(CancellationToken.None);
        Assert.Equal(0, result.CarriedGrants + result.CarriedBlocks);
        Assert.Empty(store.SavedEntities);
        Assert.Equal(new[] { OldId }, Enabled(granted));

        // The hiding side keeps the id: fail closed.
        Assert.True(bridge.BlocksPrevious(blocked, config));
    }

    [Fact]
    public void The_bridge_learns_the_other_channels_from_the_finder()
    {
        var finder = new LiveItemFinder(Library.Manager, () => [OtherPluginChannel.Named(OldTile)], NullLogger<LiveItemFinder>.Instance);
        var (names, ids, skipped) = new RenameBridge(finder).CarryCandidates(Renamed());

        Assert.Empty(names);
        Assert.Empty(ids);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public async Task The_grant_button_counts_an_old_blocked_user_as_blocked()
    {
        // N4: the bridge hides the tile from this user, so "already visible" would be wrong.
        var oldBlocked = NewUser("oldBlocked", blocked: [OldId]);
        var store = StoreWith(oldBlocked);
        var granter = Granter(store);

        var result = await granter.GrantAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Blocked);
        Assert.Equal(0, result.AlreadyVisible);
        Assert.Empty(store.SavedEntities);
        Assert.Equal(AccessGranter.Standing.Blocked, AccessGranter.Classify(oldBlocked, NewId, [OldId]));
        Assert.Equal(AccessGranter.Standing.Visible, AccessGranter.Classify(oldBlocked, NewId));   // Jellyfin's rule alone
    }

    [Fact]
    public void No_admin_action_allows_anonymous_callers()
    {
        var anonymous = typeof(JellywaveController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>(true).Any())
            .Select(m => m.Name)
            .ToArray();

        Assert.Empty(anonymous);
        Assert.Empty(typeof(JellywaveController).GetCustomAttributes<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>(true));
    }

    [Fact]
    public async Task A_collision_writes_nothing()
    {
        var store = StoreWith(NewUser("granted", enabled: [OldId]));
        var locator = new FakeLocator { Resolution = ChannelResolution.Collision("Live Radio") };

        var result = await Granter(store, locator).CarryOverAsync(CancellationToken.None);

        Assert.Equal(ChannelResolution.CollisionStatus, result.Error);
        Assert.Empty(store.SavedEntities);
        Assert.DoesNotContain(store.Events, e => e.StartsWith("read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_channel_or_configuration_writes_nothing()
    {
        var store = StoreWith(NewUser("granted", enabled: [OldId]));

        var missing = await Granter(store, new FakeLocator { Resolution = ChannelResolution.Missing }).CarryOverAsync(CancellationToken.None);
        var notReady = await new AccessGranter(store, new FakeLocator(), NullLogger<AccessGranter>.Instance, () => null, new RenameBridge(Id)).CarryOverAsync(CancellationToken.None);
        var noBridge = await new AccessGranter(store, new FakeLocator(), NullLogger<AccessGranter>.Instance, Renamed).CarryOverAsync(CancellationToken.None);

        Assert.Equal(AccessGranter.MissingChannelError, missing.Error);
        Assert.Equal(AccessGranter.NotReadyError, notReady.Error);
        Assert.Equal(AccessGranter.NotReadyError, noBridge.Error);
        Assert.Empty(store.SavedEntities);
    }

    [Fact]
    public async Task Each_user_is_read_after_the_snapshot_and_right_before_its_save()
    {
        var a = NewUser("a", enabled: [OldId]);
        var b = NewUser("b", blocked: [OldId]);
        var store = StoreWith(a, b);

        // b changed after the snapshot (an admin saved b's policy): the carry-over must write the fresh copy.
        var fresh = NewUser("b", blocked: [OldId, Other]);
        typeof(User).GetProperty(nameof(User.Id))!.SetValue(fresh, b.Id);
        store.ReplaceOnRead[b.Id] = fresh;

        await Granter(store).CarryOverAsync(CancellationToken.None);

        Assert.Equal("snapshot", store.Events[0]);
        foreach (var saved in store.SavedEntities)
        {
            var at = store.Events.IndexOf("save " + saved.Id);
            Assert.Equal("read " + saved.Id, store.Events[at - 1]);
        }

        Assert.Contains(store.SavedEntities, u => ReferenceEquals(u, fresh));
        Assert.DoesNotContain(store.SavedEntities, u => ReferenceEquals(u, b));
        Assert.Equal(new[] { OldId, Other, NewId }, Blocked(fresh));
    }

    [Fact]
    public async Task A_user_changed_to_current_after_the_snapshot_is_not_saved()
    {
        var a = NewUser("a", enabled: [OldId]);
        var store = StoreWith(a);
        var fresh = NewUser("a", enabled: [OldId, NewId]);
        typeof(User).GetProperty(nameof(User.Id))!.SetValue(fresh, a.Id);
        store.ReplaceOnRead[a.Id] = fresh;

        var result = await Granter(store).CarryOverAsync(CancellationToken.None);

        Assert.Equal(1, result.AlreadyCurrent);
        Assert.Empty(store.SavedEntities);
    }

    [Fact]
    public async Task The_log_line_has_counts_and_no_user_names()
    {
        var logger = new RecordingLogger();
        var store = StoreWith(NewUser("alice-secret-name", enabled: [OldId]));

        await Granter(store, logger: logger).CarryOverAsync(CancellationToken.None);

        var line = Assert.Single(logger.Messages);
        Assert.StartsWith("Information Channel access carry-over: 1 grants carried", line, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_status_counts_pending_users_without_writing()
    {
        var store = StoreWith(
            NewUser("granted", enabled: [OldId]),
            NewUser("blocked", blocked: [OldId]),
            NewUser("current", enabled: [OldId, NewId]),
            NewUser("all", allChannels: true));
        store.ReadOnly = true;
        var granter = Granter(store);

        var status = granter.CarryOverStatus();

        Assert.Null(status.Error);
        Assert.Equal(2, status.Pending);
        Assert.Equal(new[] { OldTile }, status.PreviousNames);
        Assert.Equal(PluginConfiguration.DefaultLibraryName, status.TileName);

        store.ReadOnly = false;
        await granter.CarryOverAsync(CancellationToken.None);
        Assert.Equal(0, granter.CarryOverStatus().Pending);
    }

    [Fact]
    public void The_status_without_earlier_names_reads_no_user()
    {
        var store = StoreWith(NewUser("granted", enabled: [OldId]));
        var status = Granter(store, config: new PluginConfiguration { SchemaVersion = 1 }).CarryOverStatus();

        Assert.Equal(0, status.Pending);
        Assert.Empty(status.PreviousNames);
        Assert.Empty(store.Events);
    }

    private static JellywaveController Controller(AccessGranter granter, LiveItemFinder finder, PluginConfiguration config)
    {
        var station = new StationClient(null!);   // never called by the carry-over routes
        return new JellywaveController(
            station,
            granter,
            new NowPlayingState(),
            new PollerHealth(),
            new LiveItemArtwork(station, (_, _, _, _) => Task.CompletedTask, NullLogger<LiveItemArtwork>.Instance),
            new StationLogos(station, (_, _, _, _) => Task.CompletedTask, NullLogger<StationLogos>.Instance, TimeProvider.System),
            finder,
            NullLogger<JellywaveController>.Instance,
            () => config);
    }

    [Fact]
    public async Task The_routes_run_the_carry_over_and_report_the_status()
    {
        var u = NewUser("granted", enabled: [OldId]);
        var store = StoreWith(u);
        var config = Renamed();
        using var granter = Granter(store, config: config);
        var controller = Controller(granter, new LiveItemFinder(Library.Manager, () => [], NullLogger<LiveItemFinder>.Instance), config);

        Assert.Equal(1, controller.CarryAccessStatus().Value!.Pending);
        var result = (await controller.CarryAccess(CancellationToken.None)).Value!;
        Assert.Equal(1, result.CarriedGrants);
        Assert.Equal(0, controller.CarryAccessStatus().Value!.Pending);
    }

    [Fact]
    public void The_status_route_reports_a_collision_instead_of_a_count()
    {
        var store = StoreWith(NewUser("granted", enabled: [OldId]));
        store.ReadOnly = true;
        var config = Renamed();
        using var granter = Granter(store, config: config);
        var finder = new LiveItemFinder(Library.Manager, () => [OtherPluginChannel.Named(PluginConfiguration.DefaultLibraryName)], NullLogger<LiveItemFinder>.Instance);

        var status = Controller(granter, finder, config).CarryAccessStatus().Value!;

        Assert.Equal(ChannelResolution.CollisionStatus, status.Error);
        Assert.Equal(0, status.Pending);
    }

    [Fact]
    public void The_routes_are_admin_only_and_the_write_is_a_post()
    {
        var post = typeof(JellywaveController).GetMethod(nameof(JellywaveController.CarryAccess))!.GetCustomAttribute<HttpPostAttribute>();
        var get = typeof(JellywaveController).GetMethod(nameof(JellywaveController.CarryAccessStatus))!.GetCustomAttribute<HttpGetAttribute>();

        Assert.Equal("CarryAccess", post?.Template);
        Assert.Equal("CarryAccess", get?.Template);
        Assert.Contains(
            typeof(JellywaveController).GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(),
            a => a.Policy == MediaBrowser.Common.Api.Policies.RequiresElevation);
    }
}
