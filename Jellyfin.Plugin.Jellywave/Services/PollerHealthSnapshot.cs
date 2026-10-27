using System;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>One consistent reading of <see cref="PollerHealth"/>.</summary>
public sealed class PollerHealthSnapshot
{
    /// <summary>Gets or sets a value indicating whether the poll loop is running.</summary>
    public bool Running { get; set; }

    /// <summary>Gets or sets whether the last poll reached the station; null before the first poll.</summary>
    public bool? Reachable { get; set; }

    /// <summary>Gets or sets the time of the last successful poll.</summary>
    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>Gets or sets the time of the last failure.</summary>
    public DateTime? LastErrorUtc { get; set; }

    /// <summary>Gets or sets the last failure's message.</summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets a value indicating whether the last poll found the channel name colliding with another plugin's channel.</summary>
    public bool NameCollision { get; set; }
}
