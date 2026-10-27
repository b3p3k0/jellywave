using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>A persona (host) in <c>GET /api/schedule</c>.</summary>
public sealed class SchedulePersona
{
    /// <summary>Gets or sets the id.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Gets or sets the name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
