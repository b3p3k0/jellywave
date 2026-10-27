using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>The show that follows the current one.</summary>
public sealed class NextShowInfo
{
    /// <summary>Gets or sets the show name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the presenter's name.</summary>
    [JsonPropertyName("presenter")]
    public string? Presenter { get; set; }

    /// <summary>Gets or sets the start time as the station phrases it ("eight in the morning").</summary>
    [JsonPropertyName("startsAt")]
    public string? StartsAt { get; set; }
}
