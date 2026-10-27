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
using MediaBrowser.Controller.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card 15 (D3): the plugin's own grant passes never overlap, and a queued pass re-reads its user after the running one.
/// The store holds the first save open until the test releases it, so the second call is started while the first is
/// mid-write; without the gate it reaches <c>SaveAsync</c> synchronously and the overlap is recorded.
/// </summary>
public class AccessGranterSerializationTests
{
    private static readonly Guid ChannelId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed class HoldingStore : IUserStore
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inside;

        public Dictionary<Guid, User> Users { get; } = new();

        public List<Guid> Saved { get; } = new();

        public int MaxConcurrentSaves { get; private set; }

        public bool FirstSaveEntered { get; private set; }

        public IEnumerable<Guid> UserIds() => Users.Keys.ToArray();

        public User? GetUser(Guid id) => Users.TryGetValue(id, out var u) ? u : null;

        public void Release() => _release.TrySetResult();

        public async Task SaveAsync(User user)
        {
            var inside = Interlocked.Increment(ref _inside);
            MaxConcurrentSaves = Math.Max(MaxConcurrentSaves, inside);
            var first = !FirstSaveEntered;
            FirstSaveEntered = true;
            try
            {
                if (first)
                {
                    await _release.Task.ConfigureAwait(false);
                }

                lock (Saved)
                {
                    Saved.Add(user.Id);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inside);
            }
        }
    }

    private sealed class FixedLocator : IChannelLocator
    {
        public Task<ChannelResolution> LocateAsync(PluginConfiguration config, CancellationToken cancellationToken)
            => Task.FromResult(ChannelResolution.Found(new Channel { Id = ChannelId }));
    }

    private static User NewUser(string name, bool allChannels = false)
    {
        var u = new User(name, "Default", "Default");
        u.SetPermission(PermissionKind.EnableAllChannels, allChannels);
        return u;
    }

    private static AccessGranter Granter(IUserStore store)
        => new(store, new FixedLocator(), NullLogger<AccessGranter>.Instance, () => new PluginConfiguration { GrantAccessToAllUsers = true });

    [Fact]
    public async Task A_single_grant_waits_for_a_running_pass_and_saves_never_overlap()
    {
        var store = new HoldingStore();
        var a = NewUser("a");
        var b = NewUser("b");
        var c = NewUser("c");
        foreach (var u in new[] { a, b, c })
        {
            store.Users[u.Id] = u;
        }

        using var granter = Granter(store);
        var pass = granter.GrantAllAsync(CancellationToken.None);
        Assert.True(store.FirstSaveEntered);

        var single = granter.GrantAsync(c.Id, CancellationToken.None);
        var allPasses = granter.GrantAllAsync(CancellationToken.None);
        Assert.False(single.IsCompleted);
        Assert.Equal(1, store.MaxConcurrentSaves);

        store.Release();
        var result = await pass;
        var granted = await single;
        var again = await allPasses;

        Assert.Equal(1, store.MaxConcurrentSaves);
        Assert.Equal(3, result.Granted);
        Assert.False(granted);          // c was granted by the pass; the re-read under the gate sees it
        Assert.Equal(0, again.Granted);
        Assert.Equal(3, store.Saved.Count);
        Assert.Equal(store.Saved.Distinct().Count(), store.Saved.Count);
    }

    [Fact]
    public async Task A_queued_grant_reads_the_user_after_the_running_pass_not_before()
    {
        var store = new HoldingStore();
        var a = NewUser("a");
        var b = NewUser("b");
        store.Users[a.Id] = a;
        using var granter = Granter(store);

        var pass = granter.GrantAllAsync(CancellationToken.None);   // holds the gate inside a's save
        store.Users[b.Id] = b;
        var single = granter.GrantAsync(b.Id, CancellationToken.None);

        // Meanwhile an admin gives b every channel. A copy read before the gate would still need a grant and would be
        // saved over this edit.
        var edited = NewUser("b", allChannels: true);
        edited.Id = b.Id;
        store.Users[b.Id] = edited;

        store.Release();
        await pass;
        Assert.False(await single);
        Assert.Equal(new[] { a.Id }, store.Saved);
    }
}
