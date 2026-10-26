namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// What the station probe learned.
/// </summary>
public sealed class StationProbeResult
{
    /// <summary>Gets or sets a value indicating whether the station answered.</summary>
    public bool Ok { get; set; }

    /// <summary>Gets or sets the station name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the on-air DJ tagline (used as channel description).</summary>
    public string? Tagline { get; set; }

    /// <summary>Gets or sets a value indicating whether the stream is online.</summary>
    public bool? StreamOnline { get; set; }

    /// <summary>Gets or sets the stream mount path.</summary>
    public string? StreamMount { get; set; }

    /// <summary>Gets or sets the stream format.</summary>
    public string? StreamFormat { get; set; }

    /// <summary>Gets or sets the stream bitrate in kbit/s.</summary>
    public int? StreamBitrateKbps { get; set; }

    /// <summary>Gets or sets the stream sample rate.</summary>
    public int? StreamSampleRate { get; set; }

    /// <summary>Gets or sets the stream channel count.</summary>
    public int? StreamChannels { get; set; }

    /// <summary>Gets or sets a value indicating whether the station's web player is password protected.</summary>
    public bool? PrivatePlayer { get; set; }

    /// <summary>Gets or sets a value indicating whether the stream requires listener auth (unsupported by this plugin).</summary>
    public bool? ListenerAuth { get; set; }

    /// <summary>Gets or sets the current track as "artist – title".</summary>
    public string? NowPlaying { get; set; }

    /// <summary>Gets or sets the error message when <see cref="Ok"/> is false.</summary>
    public string? Error { get; set; }
}
