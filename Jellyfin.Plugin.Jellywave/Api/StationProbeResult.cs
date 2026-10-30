using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// What the add-station probe (<c>POST /Jellywave/Probe</c>, card L3) learned. Carries no exception text and no part of
/// a station answer except cleaned names and addresses that pass the plugin's URL rules.
/// </summary>
public sealed class StationProbeResult
{
    /// <summary>Gets or sets a value indicating whether an audio stream the plugin can serve was found.</summary>
    public bool Ok { get; set; }

    /// <summary>Gets or sets the detected kind, "SubWave" or "Stream"; null when no stream was found.</summary>
    public string? Kind { get; set; }

    /// <summary>Gets or sets the checked stream address (after a playlist, its entry).</summary>
    public string? StreamUrl { get; set; }

    /// <summary>Gets or sets a SUB/WAVE station's base URL (no trailing slash): where <c>/api/state</c> answered.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Gets or sets a SUB/WAVE station's stream path under <see cref="BaseUrl"/>.</summary>
    public string? StreamPath { get; set; }

    /// <summary>Gets or sets the codec: "mp3" or "aac"; null when unknown.</summary>
    public string? Codec { get; set; }

    /// <summary>Gets or sets the container: "mp3", "aac" or "hls"; null when unknown.</summary>
    public string? Container { get; set; }

    /// <summary>Gets or sets the bitrate in kbit/s, when the station said.</summary>
    public int? BitrateKbps { get; set; }

    /// <summary>Gets or sets the sample rate in Hz, when the station said.</summary>
    public int? SampleRate { get; set; }

    /// <summary>Gets or sets the channel count, when the station said.</summary>
    public int? Channels { get; set; }

    /// <summary>Gets or sets the station's name (cleaned: one line, at most 120 characters).</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets a SUB/WAVE station's tagline (cleaned).</summary>
    public string? Tagline { get; set; }

    /// <summary>Gets or sets the stream's genre from <c>icy-genre</c> (cleaned: one line, at most 60 characters; card L8).</summary>
    public string? Genre { get; set; }

    /// <summary>Gets or sets the stream's notes from <c>icy-description</c> (cleaned: one line, at most 280 characters; card L8).</summary>
    public string? Notes { get; set; }

    /// <summary>Gets or sets the station's website from <c>icy-url</c>, when it passes the website rule (card L8; never requested).</summary>
    public string? Website { get; set; }

    /// <summary>Gets or sets the address a redirect pointed to, when it passes the whole-URL rule.</summary>
    public string? Redirect { get; set; }

    /// <summary>Gets the result lines, in the order the checks ran.</summary>
    public IList<ProbeLine> Lines { get; } = new List<ProbeLine>();
}
