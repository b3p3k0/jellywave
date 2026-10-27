using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>Subset of the feed's <c>context</c> block.</summary>
public sealed class ContextInfo
{
    /// <summary>Gets or sets the show handover block, present near the end of a show.</summary>
    [JsonPropertyName("showHandover")]
    public ShowHandoverInfo? ShowHandover { get; set; }
}
