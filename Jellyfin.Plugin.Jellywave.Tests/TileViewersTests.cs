using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
/// Card L13 E: <c>GET /Jellywave/Viewers</c> counts who can see the tile by the grant button's own rule
/// (<see cref="AccessGranter.Classify(User, Guid, IReadOnlyCollection{Guid})"/>, rename bridge included) and names the
/// users among them with "Allow audio playback that requires transcoding" off. It reads only: nothing is saved.
/// </summary>
public class TileViewersTests
{
    private const string OldTile = "Old Radio";

    private sealed class FakeStore : IUserStore
    {
        public Dictionary<Guid, User> Users { get; } = new();

        public List<Guid> Saved { get; } = new();

        public bool Throw { get; set; }

        public IEnumerable<Guid> UserIds() => Throw ? throw new InvalidOperationException("database locked") : Users.Keys.ToArray();

        public User? GetUser(Guid id) => Users.TryGetValue(id, out var u) ? u : null;

        public Task SaveAsync(User user)
        {
            Saved.Add(user.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLocator : IChannelLocator
    {
        public ChannelResolution Resolution { get; set; } = ChannelResolution.Found(new Channel { Id = Id(PluginConfiguration.DefaultLibraryName) });

        public Task<ChannelResolution> LocateAsync(PluginConfiguration config, CancellationToken cancellationToken) => Task.FromResult(Resolution);
    }

    private static Guid NewId => Id(PluginConfiguration.DefaultLibraryName);

    private static Guid OldId => Id(OldTile);

    private static Guid Id(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes(name)));

    private static PluginConfiguration Config() => new()
    {
        SchemaVersion = PluginConfiguration.CurrentSchemaVersion,
        PreviousChannelNames = { OldTile },
        Stations = { new StationDefinition { Id = "a", Kind = StationKind.Stream, StreamUrl = "https://cdn.example/a.mp3" } }
    };

    private static User NewUser(string name, bool allChannels = false, Guid[]? enabled = null, Guid[]? blocked = null, bool transcoding = true, bool disabled = false)
    {
        var u = new User(name, "Default", "Default");
        u.SetPermission(PermissionKind.IsDisabled, disabled);
        u.SetPermission(PermissionKind.EnableAllChannels, allChannels);
        u.SetPermission(PermissionKind.EnableAudioPlaybackTranscoding, transcoding);
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

    private static FakeStore StoreWith(params User[] users)
    {
        var store = new FakeStore();
        foreach (var u in users)
        {
            store.Users[u.Id] = u;
        }

        return store;
    }

    private static AccessGranter Granter(FakeStore store, FakeLocator? locator = null, PluginConfiguration? config = null, ILogger<AccessGranter>? logger = null)
    {
        var configuration = config ?? Config();
        return new AccessGranter(store, locator ?? new FakeLocator(), logger ?? NullLogger<AccessGranter>.Instance, () => configuration, new RenameBridge(Id));
    }

    private sealed class CapturingLogger : ILogger<AccessGranter>
    {
        public List<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(logLevel + " " + formatter(state, exception));
    }

    private static JellywaveController Controller(AccessGranter granter)
    {
        var station = new StationClient(null!);   // never called by Viewers
        return new JellywaveController(
            station,
            granter,
            new NowPlayingState(),
            new PollerHealth(),
            new LiveItemArtwork(station, (_, _, _, _) => Task.CompletedTask, NullLogger<LiveItemArtwork>.Instance),
            new StationLogos(station, (_, _, _, _) => Task.CompletedTask, NullLogger<StationLogos>.Instance, TimeProvider.System),
            new LiveItemFinder(FakeLibrary.Create().Manager, () => [], NullLogger<LiveItemFinder>.Instance),
            NullLogger<JellywaveController>.Instance,
            () => Config());
    }

    /// <summary>Every standing the grant pass knows, each with transcoding on and off.</summary>
    private static User[] Everyone() =>
    [
        NewUser("all-on", allChannels: true),
        NewUser("all-off", allChannels: true, transcoding: false),
        NewUser("named-off", enabled: [NewId], transcoding: false),
        NewUser("Alpha-off", blocked: [Guid.NewGuid()], transcoding: false),   // an unrelated block list: Jellyfin shows the tile
        NewUser("hidden-off", transcoding: false),                             // no access: needs a grant
        NewUser("blocked-off", allChannels: true, blocked: [NewId], transcoding: false),
        NewUser("old-blocked-off", allChannels: true, blocked: [OldId], transcoding: false),   // blocked from the earlier tile (bridge)
        NewUser("old-named-on", enabled: [OldId]),                             // named only the earlier tile: not visible yet
    ];

    /// <summary>Disabled users, who would be counted and named if they were active.</summary>
    private static User[] Disabled() =>
    [
        NewUser("gone-all-off", allChannels: true, transcoding: false, disabled: true),
        NewUser("gone-hidden", disabled: true),
    ];

    [Fact]
    public async Task It_counts_by_the_grant_rule_and_names_visible_users_without_audio_transcoding()
    {
        var store = StoreWith([.. Everyone(), .. Disabled()]);
        using var granter = Granter(store);

        var viewers = await granter.ViewersAsync(CancellationToken.None);

        Assert.Null(viewers.Error);
        Assert.Equal(8, viewers.Users);   // disabled users are left out (MI, fix round)
        Assert.Equal(4, viewers.Visible);
        // Sorted, case ignored; users who cannot see the tile are not named, users with transcoding on are not named.
        Assert.Equal(new[] { "all-off", "Alpha-off", "named-off" }, viewers.NoAudioTranscoding);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task The_count_is_the_grant_pass_s_already_visible_count()
    {
        // The same rule as the button, not a copy of it: a grant pass on the same (active) users finds the same visible
        // ones; disabled users beside them change nothing in the count.
        using var counter = Granter(StoreWith([.. Everyone(), .. Disabled()]));
        var viewers = await counter.ViewersAsync(CancellationToken.None);
        using var granter = Granter(StoreWith(Everyone()));
        var grant = await granter.GrantAllAsync(CancellationToken.None);

        Assert.Equal(grant.AlreadyVisible, viewers.Visible);
        Assert.Equal(grant.AlreadyVisible + grant.Granted + grant.Blocked, viewers.Users);
    }

    [Fact]
    public async Task Every_user_with_transcoding_on_names_nobody()
    {
        using var granter = Granter(StoreWith(NewUser("a", allChannels: true), NewUser("b", enabled: [NewId]), NewUser("c", transcoding: false)));

        var viewers = await granter.ViewersAsync(CancellationToken.None);

        Assert.Equal(3, viewers.Users);
        Assert.Equal(2, viewers.Visible);
        Assert.Empty(viewers.NoAudioTranscoding);
    }

    [Fact]
    public async Task Nothing_is_counted_without_a_tile_or_on_a_collision()
    {
        var store = StoreWith(NewUser("a", allChannels: true, transcoding: false));

        using var missing = Granter(store, new FakeLocator { Resolution = ChannelResolution.Missing });
        var noTile = await missing.ViewersAsync(CancellationToken.None);
        Assert.Equal(AccessGranter.MissingChannelError, noTile.Error);
        Assert.Equal(0, noTile.Users);
        Assert.Empty(noTile.NoAudioTranscoding);

        using var collides = Granter(store, new FakeLocator { Resolution = ChannelResolution.Collision("Other") });
        var collision = await collides.ViewersAsync(CancellationToken.None);
        Assert.Equal(ChannelResolution.CollisionStatus, collision.Error);
        Assert.Empty(collision.NoAudioTranscoding);

        using var notReady = new AccessGranter(store, new FakeLocator(), NullLogger<AccessGranter>.Instance, () => null);
        Assert.Equal(AccessGranter.NotReadyError, (await notReady.ViewersAsync(CancellationToken.None)).Error);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task The_route_answers_with_the_count_and_a_fixed_error_when_it_throws()
    {
        var store = StoreWith(NewUser("b", allChannels: true, transcoding: false), NewUser("a", allChannels: true));
        using var granter = Granter(store);

        var viewers = (await Controller(granter).Viewers(CancellationToken.None)).Value!;
        Assert.Equal(2, viewers.Visible);
        Assert.Equal(new[] { "b" }, viewers.NoAudioTranscoding);

        store.Throw = true;
        var failed = (await Controller(granter).Viewers(CancellationToken.None)).Value!;
        Assert.Equal(JellywaveController.ViewersFailedError, failed.Error);
        Assert.DoesNotContain("locked", failed.Error, StringComparison.Ordinal);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task It_logs_nothing_found_missing_or_colliding()
    {
        // RA S3 (E1, E2): the page asks on every visit; THREAT-MODEL says the count writes no log line, so no user name either.
        foreach (var locator in new[] { new FakeLocator(), new FakeLocator { Resolution = ChannelResolution.Missing }, new FakeLocator { Resolution = ChannelResolution.Collision("Other") } })
        {
            var logger = new CapturingLogger();
            using var granter = Granter(StoreWith(Everyone()), locator, logger: logger);

            await granter.ViewersAsync(CancellationToken.None);

            Assert.Empty(logger.Lines);
        }

        // Control: the grant pass on the same store does log, so the logger is wired.
        var control = new CapturingLogger();
        using var grant = Granter(StoreWith(Everyone()), new FakeLocator { Resolution = ChannelResolution.Missing }, logger: control);
        await grant.GrantAllAsync(CancellationToken.None);
        Assert.NotEmpty(control.Lines);
    }

    [Fact]
    public async Task A_user_deleted_since_the_snapshot_is_not_counted()
    {
        // RA S3 (E6): the id list is a snapshot; a user gone by the time it is read counts nowhere.
        var store = StoreWith(NewUser("a", allChannels: true, transcoding: false));
        store.Users[Guid.NewGuid()] = null!;   // listed, then deleted
        using var granter = Granter(store);

        var viewers = await granter.ViewersAsync(CancellationToken.None);

        Assert.Equal(1, viewers.Users);
        Assert.Equal(1, viewers.Visible);
        Assert.Equal(new[] { "a" }, viewers.NoAudioTranscoding);
    }

    [Fact]
    public async Task A_cancelled_request_is_not_answered_with_an_error()
    {
        // RA S3 (E7): a cancelled page request ends as a cancellation, not as a 200 with "the count failed".
        using var granter = Granter(StoreWith(NewUser("a", allChannels: true)));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Controller(granter).Viewers(cancelled.Token));
    }

    [Fact]
    public void The_route_is_a_get_on_the_admin_controller()
    {
        var method = typeof(JellywaveController).GetMethod(nameof(JellywaveController.Viewers))!;
        Assert.Equal("Viewers", method.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Null(method.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>());
        Assert.Null(method.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>());   // the class's admin policy applies
    }
}
