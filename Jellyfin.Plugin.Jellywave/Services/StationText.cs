using System;
using System.Linq;
using Jellyfin.Plugin.Jellywave.Configuration;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// A station's descriptive fields as the server serves them (card L8): genre, notes and website, read from the saved
/// station and enforced again here, because the configuration page is not trusted for its limits. Pure functions, no I/O.
/// </summary>
public static class StationText
{
    private const string Ellipsis = "…";

    /// <summary>
    /// Cleans text for one line of the station page: control characters (line breaks included) become spaces, runs of
    /// whitespace collapse to one, and text longer than <paramref name="maxLength"/> is cut to that length, its last
    /// character an ellipsis; a surrogate pair at the cut is dropped whole, never split.
    /// </summary>
    /// <param name="value">The text, possibly null.</param>
    /// <param name="maxLength">The longest result, at least 1.</param>
    /// <returns>The cleaned text, or null when nothing is left.</returns>
    public static string? Clean(string? value, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var visible = new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        var collapsed = string.Join(' ', visible.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0)
        {
            return null;
        }

        if (collapsed.Length <= maxLength)
        {
            return collapsed;
        }

        // Never end on half of a surrogate pair (an emoji, a rare CJK character): cut before it instead.
        var cut = maxLength - 1;
        if (cut > 0 && char.IsHighSurrogate(collapsed[cut - 1]))
        {
            cut--;
        }

        return collapsed[..cut] + Ellipsis;
    }

    /// <summary>
    /// Gets the station's genre as served: cleaned, at most <see cref="StationDefinition.MaxGenreLength"/> characters.
    /// </summary>
    /// <param name="station">The station.</param>
    /// <returns>The genre, or null when blank.</returns>
    public static string? Genre(StationDefinition station)
    {
        ArgumentNullException.ThrowIfNull(station);
        return Clean(station.Genre, StationDefinition.MaxGenreLength);
    }

    /// <summary>
    /// Gets the station's notes as served: cleaned, at most <see cref="StationDefinition.MaxNotesLength"/> characters.
    /// </summary>
    /// <param name="station">The station.</param>
    /// <returns>The notes, or null when blank.</returns>
    public static string? Notes(StationDefinition station)
    {
        ArgumentNullException.ThrowIfNull(station);
        return Clean(station.Notes, StationDefinition.MaxNotesLength);
    }

    /// <summary>
    /// Gets the station's website as served: the escaped link when it passes the website rule
    /// (<see cref="StationUrls.WebsiteLink"/>), else null. Never requested by the server.
    /// </summary>
    /// <param name="station">The station.</param>
    /// <returns>The link, or null when blank or refused.</returns>
    public static string? Website(StationDefinition station)
    {
        ArgumentNullException.ThrowIfNull(station);
        return StationUrls.WebsiteLink(station.Website);
    }
}
