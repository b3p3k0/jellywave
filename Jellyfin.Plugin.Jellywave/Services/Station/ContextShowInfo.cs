using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>The active show as the feed's <c>context</c> block reports it, with the id the schedule grid uses.</summary>
public sealed class ContextShowInfo
{
    /// <summary>Gets or sets the show id (the grid's key).</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Gets or sets the show name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
