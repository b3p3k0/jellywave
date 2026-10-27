using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>Listener counts.</summary>
public sealed class ListenersInfo
{
    /// <summary>Gets or sets the number of listeners right now.</summary>
    [JsonPropertyName("current")]
    public int? Current { get; set; }

    /// <summary>Gets or sets the peak listener count.</summary>
    [JsonPropertyName("peak")]
    public int? Peak { get; set; }
}
