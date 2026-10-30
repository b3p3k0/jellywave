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
/// Card L1: "Give them access to the renamed tile" shares the grant button's gate, so it never saves while a grant pass is mid-write and
/// reads its users only after that pass has saved. The store holds the first save open until the test releases it
/// (the pattern of <see cref="AccessGranterSerializationTests"/>).
/// </summary>
public class CarryOverGateTests
{
    private static readonly FakeLibrary Library = FakeLibrary.Create();

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
            => Task.FromResult(ChannelResolution.Found(new Channel { Id = Id(PluginConfiguration.DefaultLibraryName) }));
    }

    private static Guid Id(string name) => Library.IdFor("Channel " + name, typeof(Channel));

    [Fact]
    public async Task A_carry_over_waits_for_a_running_grant_pass()
    {
        var oldId = Id("IHateRadio");
        var newId = Id(PluginConfiguration.DefaultLibraryName);
        var store = new HoldingStore();
        var plain = new User("plain", "Default", "Default");            // the grant pass holds inside this save
        var byName = new User("byName", "Default", "Default");          // the carry-over's user
        byName.SetPreference(PreferenceKind.EnabledChannels, [oldId]);
        store.Users[plain.Id] = plain;
        store.Users[byName.Id] = byName;
        var config = new PluginConfiguration { SchemaVersion = 1, PreviousChannelNames = { "IHateRadio" } };
        using var granter = new AccessGranter(store, new FixedLocator(), NullLogger<AccessGranter>.Instance, () => config, new RenameBridge(Id));

        var grant = granter.GrantAllAsync(CancellationToken.None);
        Assert.True(store.FirstSaveEntered);

        var carry = granter.CarryOverAsync(CancellationToken.None);
        Assert.False(carry.IsCompleted);
        Assert.Equal(1, store.MaxConcurrentSaves);

        store.Release();
        await grant;
        var carried = await carry;

        Assert.Equal(1, store.MaxConcurrentSaves);
        // The grant pass already added the new id for byName (no block list, so it needed a grant); the queued carry-over
        // read byName after that save and found it current.
        Assert.Equal(0, carried.CarriedGrants);
        Assert.Equal(1, carried.AlreadyCurrent);
        Assert.Equal(new[] { oldId, newId }, byName.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels));
    }
}
