using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Jellywave.Services;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card L14: an <see cref="IPlayingItems"/> a test controls: the item ids the sessions play, each by a user
/// (<see cref="Listener"/> unless a test names another), and playback starts raised with an item's external id and its
/// user, as <see cref="SessionPlayingItems"/> raises them from Jellyfin's event.
/// </summary>
internal sealed class FakePlayingItems : IPlayingItems
{
    /// <summary>The user of every session a test does not give another user.</summary>
    public static readonly Guid Listener = Guid.Parse("0a0a0a0a-0000-0000-0000-00000000000a");

    public event EventHandler<PlaybackStartedEventArgs>? PlaybackStarted;

    public HashSet<Guid> Ids { get; } = new();

    /// <summary>Gets the user of each played item whose session is not <see cref="Listener"/>'s.</summary>
    public Dictionary<Guid, Guid> UserOf { get; } = new();

    public IReadOnlySet<Guid> PlayingItemIds() => new HashSet<Guid>(Ids);

    public IReadOnlySet<Guid> PlayingItemIds(Func<Guid, bool> userCounts)
        => Ids.Where(id => userCounts(UserOf.GetValueOrDefault(id, Listener))).ToHashSet();

    /// <summary>A session starts playing the item: it plays from now on, and the start is raised.</summary>
    public void Start(Guid itemId, string? externalId, Guid? user = null)
    {
        Ids.Add(itemId);
        if (user is { } u && u != Listener)
        {
            UserOf[itemId] = u;
        }

        PlaybackStarted?.Invoke(this, new PlaybackStartedEventArgs(externalId, [user ?? Listener]));
    }

    /// <summary>
    /// A session reports a playback start and its stop right after (fix round R1): the start is raised, and nothing plays
    /// when the next poll looks. Touches no set, so a test may call it from another thread while the poll loop runs.
    /// </summary>
    public void StartedAndStopped(string? externalId, Guid? user = null)
        => PlaybackStarted?.Invoke(this, new PlaybackStartedEventArgs(externalId, [user ?? Listener]));
}
