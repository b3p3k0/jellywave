namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// What the station page shows after a request: the receipt or refusal, then the polled outcome.
/// Mapped from the station's answers so the page never sees the station itself.
/// </summary>
public sealed class RequestOutcome
{
    /// <summary>Gets or sets a value indicating whether the station took the request (poll <see cref="RequestId"/> for the result).</summary>
    public bool Accepted { get; set; }

    /// <summary>Gets or sets the id to poll.</summary>
    public string? RequestId { get; set; }

    /// <summary>Gets or sets the status: pending, resolved, failed, unknown, refused, unavailable.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the station's outcome word once resolved (matched, substituted, chat, no-match, ...).</summary>
    public string? Outcome { get; set; }

    /// <summary>Gets or sets a human line: the station's own message, or the plugin's explanation.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets the DJ's acknowledgement text.</summary>
    public string? Ack { get; set; }

    /// <summary>Gets or sets the resolved track as "Artist – Title".</summary>
    public string? Track { get; set; }

    /// <summary>Gets or sets the queue position of the resolved track.</summary>
    public int? QueuePosition { get; set; }

    /// <summary>Gets or sets how many seconds to wait before trying again (rate limit).</summary>
    public int? RetryAfterSeconds { get; set; }
}
