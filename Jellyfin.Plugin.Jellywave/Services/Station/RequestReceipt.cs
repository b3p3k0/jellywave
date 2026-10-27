using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>Answer to <c>POST /api/request</c>: a receipt (202) or a refusal (400, 429, 503).</summary>
public sealed class RequestReceipt
{
    /// <summary>Gets or sets a value indicating whether the request was taken.</summary>
    [JsonPropertyName("success")]
    public bool? Success { get; set; }

    /// <summary>Gets or sets the id to poll for the outcome.</summary>
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    /// <summary>Gets or sets the status ("pending" on a receipt).</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>Gets or sets the station's message on a refusal.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>Gets or sets the seconds to wait on a rate-limit refusal.</summary>
    [JsonPropertyName("retryAfter")]
    public int? RetryAfter { get; set; }
}
