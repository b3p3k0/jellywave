using System;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The latest now-playing snapshot, shared between the poller and the channel. Thread-safe.
/// </summary>
public sealed class NowPlayingState
{
    private readonly object _gate = new();
    private NowPlayingFeed? _feed;
    private DateTime _fetchedUtc;

    /// <summary>
    /// Gets the time of the last successful fetch, or <see cref="DateTime.MinValue"/>.
    /// </summary>
    public DateTime FetchedUtc
    {
        get
        {
            lock (_gate)
            {
                return _fetchedUtc;
            }
        }
    }

    /// <summary>
    /// Stores a fresh feed and reports whether the on-air track or show changed.
    /// </summary>
    /// <param name="feed">The feed just fetched.</param>
    /// <param name="nowUtc">The fetch time.</param>
    /// <returns>True when the description built from the feed differs from the previous one.</returns>
    public bool Update(NowPlayingFeed feed, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(feed);
        lock (_gate)
        {
            var before = Describe(_feed);
            _feed = feed;
            _fetchedUtc = nowUtc;
            return !string.Equals(before, Describe(feed), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Forgets the last feed (station unreachable or polling disabled).
    /// </summary>
    /// <returns>True when there was a feed to forget.</returns>
    public bool Clear()
    {
        lock (_gate)
        {
            var had = _feed is not null;
            _feed = null;
            return had;
        }
    }

    /// <summary>
    /// Gets the one-line description of what is on air, or null when nothing is known.
    /// </summary>
    /// <returns>For example "Now playing: Artist – Title (Album) · DJ Bobby · The Ville".</returns>
    public string? Description()
    {
        lock (_gate)
        {
            return Describe(_feed);
        }
    }

    private static string? Describe(NowPlayingFeed? feed)
    {
        if (feed is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        var track = feed.NowPlaying;
        if (track is not null && (!string.IsNullOrWhiteSpace(track.Title) || !string.IsNullOrWhiteSpace(track.Artist)))
        {
            sb.Append("Now playing: ");
            if (!string.IsNullOrWhiteSpace(track.Artist))
            {
                sb.Append(track.Artist.Trim()).Append(" – ");
            }

            sb.Append(string.IsNullOrWhiteSpace(track.Title) ? "untitled" : track.Title.Trim());
            if (!string.IsNullOrWhiteSpace(track.Album))
            {
                sb.Append(CultureInfo.InvariantCulture, $" ({track.Album.Trim()})");
            }
        }
        else if (!feed.StreamOnline)
        {
            sb.Append("Off air");
        }

        if (!string.IsNullOrWhiteSpace(feed.Dj?.Name))
        {
            AppendSeparated(sb, "DJ " + feed.Dj.Name.Trim());
        }

        if (!string.IsNullOrWhiteSpace(feed.ActiveShow?.Name))
        {
            AppendSeparated(sb, feed.ActiveShow.Name.Trim());
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static void AppendSeparated(StringBuilder sb, string text)
    {
        if (sb.Length > 0)
        {
            sb.Append(" · ");
        }

        sb.Append(text);
    }
}
