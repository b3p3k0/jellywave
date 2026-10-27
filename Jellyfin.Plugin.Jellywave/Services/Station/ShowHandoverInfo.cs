using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>What the station says about the coming show change.</summary>
public sealed class ShowHandoverInfo
{
    /// <summary>Gets or sets the next show.</summary>
    [JsonPropertyName("nextShow")]
    public NextShowInfo? NextShow { get; set; }
}
