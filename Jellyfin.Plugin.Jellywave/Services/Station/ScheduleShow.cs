using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>A show in <c>GET /api/schedule</c>.</summary>
public sealed class ScheduleShow
{
    /// <summary>Gets or sets the id used in the grid.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Gets or sets the name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the host persona id.</summary>
    [JsonPropertyName("personaId")]
    public string? PersonaId { get; set; }
}
