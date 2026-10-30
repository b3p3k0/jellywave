namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// How the title sampler's last read of a plain stream ended (card L13, fix round S2).
/// </summary>
public enum TitleReadOutcome
{
    /// <summary>The stream answered with a success status and its headers: it is up, whether or not a title arrived in the budget.</summary>
    Answered,

    /// <summary>The stream answered with a redirect, which the sampler never follows (the relay does, so it may play).</summary>
    Moved,

    /// <summary>No success answer: not reachable, an error status, a timeout before the headers, or an unexpected failure.</summary>
    NoAnswer
}
