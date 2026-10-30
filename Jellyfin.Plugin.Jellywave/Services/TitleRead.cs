using System;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The outcome of the last time the title sampler read a plain stream (card L13), for the configuration page's station
/// table: how the read ended, and when. A copy; never the URL.
/// </summary>
public sealed class TitleRead
{
    /// <summary>Gets how the read ended.</summary>
    public TitleReadOutcome Outcome { get; init; }

    /// <summary>Gets when the read ended (UTC).</summary>
    public DateTime AtUtc { get; init; }
}
