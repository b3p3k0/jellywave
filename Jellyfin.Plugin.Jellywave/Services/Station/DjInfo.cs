using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>DJ on air.</summary>
public sealed class DjInfo
{
    /// <summary>Gets or sets the DJ name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the DJ tagline.</summary>
    [JsonPropertyName("tagline")]
    public string? Tagline { get; set; }

    /// <summary>Gets or sets the portrait path, for example <c>/persona-avatar/p_8754e4</c>.</summary>
    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }

    /// <summary>Gets or sets the station name the DJ is hosting.</summary>
    [JsonPropertyName("station")]
    public string? Station { get; set; }
}
