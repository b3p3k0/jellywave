using System;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The latest now-playing snapshot, shared between the poller and the channel. Thread-safe.
/// </summary>
public sealed class NowPlayingState
{
    /// <summary>
    /// Longest station-supplied field kept in the description (the rest is cut with an ellipsis).
    /// </summary>
    public const int MaxFieldLength = 120;

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
        var artist = Clean(feed.NowPlaying?.Artist);
        var title = Clean(feed.NowPlaying?.Title);
        var album = Clean(feed.NowPlaying?.Album);
        var dj = Clean(feed.Dj?.Name);
        var show = Clean(feed.ActiveShow?.Name);

        if (!feed.StreamOnline)
        {
            // A stale track may still be reported while the mount is down; the listener hears nothing.
            sb.Append("Off air");
        }
        else if (artist.Length > 0 || title.Length > 0)
        {
            sb.Append("Now playing: ");
            if (artist.Length > 0)
            {
                sb.Append(artist).Append(" – ");
            }

            sb.Append(title.Length > 0 ? title : "untitled");
            if (album.Length > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $" ({album})");
            }
        }

        if (dj.Length > 0)
        {
            AppendSeparated(sb, "DJ " + dj);
        }

        if (show.Length > 0)
        {
            AppendSeparated(sb, show);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>
    /// Collapses whitespace (including line breaks) to single spaces, trims, and caps the length.
    /// Station text lands in a database column and in client UIs; it must stay one short line.
    /// </summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= MaxFieldLength ? collapsed : collapsed[..(MaxFieldLength - 1)] + "…";
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
