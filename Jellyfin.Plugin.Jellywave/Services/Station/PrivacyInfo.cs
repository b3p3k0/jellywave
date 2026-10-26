using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>Station privacy flags.</summary>
public sealed class PrivacyInfo
{
    /// <summary>Gets or sets a value indicating whether the web player is behind a password.</summary>
    [JsonPropertyName("privatePlayer")]
    public bool PrivatePlayer { get; set; }

    /// <summary>Gets or sets a value indicating whether the stream mounts require listener auth.</summary>
    [JsonPropertyName("listenerAuth")]
    public bool ListenerAuth { get; set; }
}
