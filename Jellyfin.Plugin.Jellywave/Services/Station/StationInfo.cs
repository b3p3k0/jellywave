using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>Station identity.</summary>
public sealed class StationInfo
{
    /// <summary>Gets or sets the station name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
