using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// The poller's health for the configuration page. The top-level fields describe the primary SUB/WAVE station (the one a
/// station route called without a station id serves), so the page's summary lines keep working; <see cref="Stations"/> has
/// every configured station.
/// </summary>
public sealed class HealthResponse
{
    /// <summary>Gets or sets a value indicating whether now-playing polling is switched on.</summary>
    public bool PollingEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the primary SUB/WAVE station has a usable base URL. False with plain streams
    /// only; the station table (<see cref="Stations"/>) covers every station.
    /// </summary>
    public bool StationConfigured { get; set; }

    /// <summary>
    /// Gets or sets why a non-empty station base URL is refused (a fixed sentence, never part of the value), or null
    /// when the URL is usable or blank.
    /// </summary>
    public string? StationUrlRefusal { get; set; }

    /// <summary>
    /// Gets or sets the status line's part about the primary SUB/WAVE station when its URL is not usable: "no SUB/WAVE
    /// station URL configured" (also with plain streams only), "the SUB/WAVE station URL is set but not allowed: …" or
    /// "the SUB/WAVE station is not listed: …"; null when it is usable.
    /// </summary>
    public string? StationUrlStatus { get; set; }

    /// <summary>
    /// Gets or sets the status line's channel part when the channel name collides with another plugin's channel
    /// ("channel name collides with another plugin's channel"), resolved when the health is read; null otherwise.
    /// </summary>
    public string? ChannelStatus { get; set; }

    /// <summary>
    /// Gets or sets a warning when the plugin runs on default settings because its configuration file could not be
    /// loaded at startup, or on the starter stations because a fresh install's first save failed (card L9); null otherwise.
    /// </summary>
    public string? ConfigurationWarning { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="ConfigurationWarning"/> is about a save that failed (a fresh
    /// install's first one) rather than a load, so the page names the right problem.
    /// </summary>
    public bool ConfigurationUnsaved { get; set; }

    /// <summary>
    /// Gets or sets the file name (no folder) the unreadable configuration file was copied to (card R1), while the plugin
    /// runs on defaults; null when there is no copy (none was needed, or it failed).
    /// </summary>
    public string? ConfigurationUnreadableCopy { get; set; }

    /// <summary>
    /// Gets or sets why every save is refused (card R1: the unreadable configuration file could not be copied aside, so
    /// it is never written over); null while saves are allowed. The page shows it next to Save after a refused save,
    /// since Jellyfin answers that save with a bare error.
    /// </summary>
    public string? ConfigurationSaveRefusal { get; set; }

    /// <summary>Gets or sets a value indicating whether the poll loop is running.</summary>
    public bool Running { get; set; }

    /// <summary>
    /// Gets or sets how long the last poll's fetch phase took, in seconds; above the poll interval the items update less
    /// often than set. Null before the first poll with polling on.
    /// </summary>
    public double? LastFetchSeconds { get; set; }

    /// <summary>Gets or sets whether the last poll reached the station; null before the first poll.</summary>
    public bool? Reachable { get; set; }

    /// <summary>Gets or sets the time of the last successful poll.</summary>
    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>Gets or sets the time of the last failure.</summary>
    public DateTime? LastErrorUtc { get; set; }

    /// <summary>Gets or sets the last failure's category, for example "timed out" (never exception text).</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets since when the poller has left the primary station idle (card L14: nobody plays it or has its page
    /// open), null while it is asked; as <see cref="StationHealth.IdleSinceUtc"/>.
    /// </summary>
    public DateTime? IdleSinceUtc { get; set; }

    /// <summary>Gets or sets the live item's current description line.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets which image the live item carries: a cover id, "logo", or null when not yet applied.</summary>
    public string? Cover { get; set; }

    /// <summary>Gets or sets a value indicating whether the live item exists in Jellyfin yet.</summary>
    public bool LiveItemExists { get; set; }

    /// <summary>
    /// Gets or sets every configured station's status in grid order (<c>SortOrder</c>, list order breaking ties), disabled
    /// and refused stations included (card L4).
    /// </summary>
    public IReadOnlyList<StationHealth> Stations { get; set; } = [];
}
