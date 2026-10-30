using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// <see cref="IPlayingItems"/> over Jellyfin's <see cref="ISessionManager"/>: the now-playing item of every session
/// (<see cref="SessionInfo.NowPlayingItem"/>, which Jellyfin sets on playback start and clears on stop; paused counts as
/// playing), and its <see cref="ISessionManager.PlaybackStart"/> event passed on with the started item's external id and the
/// session's users (card L14). Reads only; never touches a session.
/// </summary>
public sealed class SessionPlayingItems : IPlayingItems, IDisposable
{
    private readonly ISessionManager _sessions;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionPlayingItems"/> class.
    /// </summary>
    /// <param name="sessions">The session manager.</param>
    public SessionPlayingItems(ISessionManager sessions)
    {
        _sessions = sessions;
        _sessions.PlaybackStart += OnPlaybackStart;
    }

    /// <inheritdoc />
    public event EventHandler<PlaybackStartedEventArgs>? PlaybackStarted;

    /// <inheritdoc />
    public IReadOnlySet<Guid> PlayingItemIds() => Playing(null);

    /// <inheritdoc />
    public IReadOnlySet<Guid> PlayingItemIds(Func<Guid, bool> userCounts)
    {
        ArgumentNullException.ThrowIfNull(userCounts);
        return Playing(userCounts);
    }

    /// <inheritdoc />
    public void Dispose() => _sessions.PlaybackStart -= OnPlaybackStart;

    /// <summary>A session's user and the users added to it (Jellyfin's own <c>GetUsers</c> for playback events).</summary>
    private static IEnumerable<Guid> UsersOf(SessionInfo session)
        => new[] { session.UserId }.Concat((session.AdditionalUsers ?? []).Select(u => u?.UserId ?? Guid.Empty)).Where(id => id != Guid.Empty);

    /// <summary>The items the sessions play; with <paramref name="userCounts"/>, only the sessions with a user it accepts.</summary>
    private HashSet<Guid> Playing(Func<Guid, bool>? userCounts)
    {
        var ids = new HashSet<Guid>();
        foreach (var session in _sessions.Sessions ?? [])
        {
            var id = session?.NowPlayingItem?.Id ?? session?.FullNowPlayingItem?.Id ?? Guid.Empty;
            if (id != Guid.Empty && !ids.Contains(id) && (userCounts is null || UsersOf(session!).Any(userCounts)))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        var externalId = e?.Item?.ExternalId ?? e?.Session?.FullNowPlayingItem?.ExternalId;
        // Jellyfin fills Users with the session's users; the session itself is the fallback.
        var users = (e?.Users ?? []).Where(u => u is not null).Select(u => u.Id)
            .Concat(e?.Session is { } session ? UsersOf(session) : [])
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        PlaybackStarted?.Invoke(this, new PlaybackStartedEventArgs(externalId, users));
    }
}
