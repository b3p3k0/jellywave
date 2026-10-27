using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>Answer to <c>GET /api/request/{id}</c>.</summary>
public sealed class RequestStatus
{
    /// <summary>Gets or sets the status: pending, resolved, failed, or unknown (404).</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>Gets or sets the DJ's acknowledgement text, when there is one.</summary>
    [JsonPropertyName("ack")]
    public string? Ack { get; set; }

    /// <summary>Gets or sets the track the request resolved to.</summary>
    [JsonPropertyName("track")]
    public RequestTrack? Track { get; set; }

    /// <summary>Gets or sets the queue position of the resolved track.</summary>
    [JsonPropertyName("queuePosition")]
    public int? QueuePosition { get; set; }

    /// <summary>Gets or sets the outcome: matched, substituted, cooldown, already-queued, chat, no-match, unsupported, error.</summary>
    [JsonPropertyName("outcome")]
    public string? Outcome { get; set; }

    /// <summary>Gets or sets the station's message.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
