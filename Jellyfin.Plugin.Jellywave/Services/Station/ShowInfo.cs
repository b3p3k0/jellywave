using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>Active show.</summary>
public sealed class ShowInfo
{
    /// <summary>Gets or sets the show name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
