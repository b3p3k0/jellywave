using System;

namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// One configured station's status for the configuration page's station table (card L4; rendered by card L3). Carries no
/// URL and no exception text: the not-listed reason and the failure category are fixed sentences.
/// </summary>
public sealed class StationHealth
{
    /// <summary>Gets or sets the station id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the station's display name ("SUB/WAVE" when blank).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind: "SubWave" or "Stream" (a number for an unknown kind).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the station is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether the tile lists the station as an item.</summary>
    public bool Listed { get; set; }

    /// <summary>
    /// Gets or sets why the station is not listed (a fixed sentence from <c>StationSources</c>, never part of a URL), or null
    /// when it is listed.
    /// </summary>
    public string? NotListedReason { get; set; }

    /// <summary>Gets or sets whether the station's last poll reached it (listed SUB/WAVE stations); null before its first poll.</summary>
    public bool? Reachable { get; set; }

    /// <summary>Gets or sets the time of the station's last successful poll (listed SUB/WAVE stations).</summary>
    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>Gets or sets the time of the station's last failed poll (listed SUB/WAVE stations).</summary>
    public DateTime? LastErrorUtc { get; set; }

    /// <summary>Gets or sets the last failure's category (listed SUB/WAVE stations), for example "timed out".</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets since when the poller has left the station idle (listed SUB/WAVE stations, card L14: nobody plays it or
    /// has its page open, so it is not asked); null while it is asked. A success or failure after this time came from
    /// "Reload status".
    /// </summary>
    public DateTime? IdleSinceUtc { get; set; }

    /// <summary>
    /// Gets or sets the image the station's item carries (listed SUB/WAVE stations): a cover id, "logo", or null when not
    /// yet applied.
    /// </summary>
    public string? Cover { get; set; }

    /// <summary>
    /// Gets or sets how the title sampler's last read of a listed plain stream ended (card L13): "Answered", "Moved" (a
    /// redirect, which the sampler does not follow) or "NoAnswer"; null when it was not read since the last restart
    /// (nobody played it with "Show what's playing" on, or an HLS stream, which is never read) and for other stations.
    /// </summary>
    public string? LastReadOutcome { get; set; }

    /// <summary>Gets or sets when that read ended (UTC); null with <see cref="LastReadOutcome"/>.</summary>
    public DateTime? LastReadUtc { get; set; }

    /// <summary>
    /// Gets or sets a listed plain stream's logo status: "own", "embedded", "deferred" or "failed"; null for other stations.
    /// </summary>
    public string? Logo { get; set; }
}
