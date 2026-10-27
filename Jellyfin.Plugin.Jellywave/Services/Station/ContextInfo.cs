using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>Subset of the feed's <c>context</c> block.</summary>
public sealed class ContextInfo
{
    /// <summary>Gets or sets the active show with its id (the top-level <c>activeShow</c> carries no id).</summary>
    [JsonPropertyName("activeShow")]
    public ContextShowInfo? ActiveShow { get; set; }

    /// <summary>Gets or sets the show handover block, present near the end of a show.</summary>
    [JsonPropertyName("showHandover")]
    public ShowHandoverInfo? ShowHandover { get; set; }
}
