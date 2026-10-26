using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>Stream format as the station reports it.</summary>
public sealed class StationStreamInfo
{
    /// <summary>Gets or sets the mount path, e.g. /stream.mp3.</summary>
    [JsonPropertyName("mount")]
    public string? Mount { get; set; }

    /// <summary>Gets or sets the format, e.g. mp3.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    /// <summary>Gets or sets the bitrate in kbit/s.</summary>
    [JsonPropertyName("bitrate")]
    public int? Bitrate { get; set; }

    /// <summary>Gets or sets the sample rate in Hz.</summary>
    [JsonPropertyName("sampleRate")]
    public int? SampleRate { get; set; }

    /// <summary>Gets or sets the channel count.</summary>
    [JsonPropertyName("channels")]
    public int? Channels { get; set; }
}
