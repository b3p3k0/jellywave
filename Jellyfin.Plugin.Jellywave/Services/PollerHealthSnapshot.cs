using System;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>One consistent reading of <see cref="PollerHealth"/>: the loop, and one station's fields.</summary>
public sealed class PollerHealthSnapshot
{
    /// <summary>Gets or sets a value indicating whether the poll loop is running.</summary>
    public bool Running { get; set; }

    /// <summary>
    /// Gets or sets how long the last poll's fetch phase took, in seconds (one decimal); null before the first poll with
    /// polling on.
    /// </summary>
    public double? LastFetchSeconds { get; set; }

    /// <summary>Gets or sets whether the station's last poll reached it; null before its first poll under its current key.</summary>
    public bool? Reachable { get; set; }

    /// <summary>Gets or sets the time of the last successful poll.</summary>
    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>Gets or sets the time of the last failure.</summary>
    public DateTime? LastErrorUtc { get; set; }

    /// <summary>Gets or sets the last failure's category (<see cref="StationFailures"/>): a fixed text, never a URL or exception text.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets since when the poller has left the station idle (card L14: nobody listens or has its page open, so it is
    /// not asked); null while it is asked.
    /// </summary>
    public DateTime? IdleSinceUtc { get; set; }
}
