using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Subset of SUB/WAVE's <c>GET /api/state</c>.
/// </summary>
public sealed class StationState
{
    /// <summary>Gets or sets the station block.</summary>
    [JsonPropertyName("station")]
    public StationInfo? Station { get; set; }

    /// <summary>Gets or sets the privacy block.</summary>
    [JsonPropertyName("privacy")]
    public PrivacyInfo? Privacy { get; set; }
}
