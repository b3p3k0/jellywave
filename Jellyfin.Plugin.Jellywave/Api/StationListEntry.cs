using System;

namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// One listed station in the station page's list (cards L5, L5b, L8): what the page needs to name, draw and follow it. Never
/// a stream or station URL; the only link is a station's <see cref="Website"/>, which passed the website rule.
/// </summary>
public sealed class StationListEntry
{
    /// <summary>The <see cref="Kind"/> of a SUB/WAVE station.</summary>
    public const string KindSubWave = "SubWave";

    /// <summary>The <see cref="Kind"/> of a plain stream.</summary>
    public const string KindStream = "Stream";

    /// <summary>Gets or sets the station id, for the station routes' <c>stationId</c> (SUB/WAVE stations only).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the station name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind: <see cref="KindSubWave"/> or <see cref="KindStream"/> (card L8).</summary>
    public string Kind { get; set; } = KindSubWave;

    /// <summary>Gets or sets the station's live item id, or null when Jellyfin has not created it yet.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>
    /// Gets or sets the live item's Primary image tag, or null when it has none or (SUB/WAVE) still shows another origin's
    /// cover. For a plain stream it is the logo's.
    /// </summary>
    public string? ImageTag { get; set; }

    /// <summary>Gets or sets the station's notes (any kind, card L8): plain text, at most 280 characters, or null.</summary>
    public string? Notes { get; set; }

    /// <summary>Gets or sets a plain stream's genre (card L8): plain text, at most 60 characters, or null.</summary>
    public string? Genre { get; set; }

    /// <summary>Gets or sets a plain stream's website (card L8): an http or https link that passed the website rule, or null.</summary>
    public string? Website { get; set; }

    /// <summary>Gets or sets a plain stream's format label for the header (card L8): <c>MP3</c>, <c>AAC</c> or <c>HLS</c>.</summary>
    public string? Codec { get; set; }

    /// <summary>Gets or sets a plain stream's bitrate in kbit/s as configured (card L8), or null when unset.</summary>
    public int? BitrateKbps { get; set; }
}
