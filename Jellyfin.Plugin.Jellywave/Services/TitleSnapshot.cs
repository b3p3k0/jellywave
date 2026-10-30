using System;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// One plain stream's title state (card L6) as <see cref="StationTitles.Snapshot"/> reads it: a copy, never the live entry.
/// </summary>
public sealed class TitleSnapshot
{
    /// <summary>Gets the state: one of the <c>State*</c> constants of <see cref="StationTitles"/>.</summary>
    public string State { get; init; } = StationTitles.StateWaiting;

    /// <summary>Gets the cleaned title; set only in <see cref="StationTitles.StateTitle"/>.</summary>
    public string? Title { get; init; }

    /// <summary>Gets when the last sample that read the stream's metadata succeeded (UTC), or null.</summary>
    public DateTime? LastGoodUtc { get; init; }
}
