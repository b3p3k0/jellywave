using System;

namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// What the station page shows: the poller's last feed, reduced to display fields, plus the live item's
/// Jellyfin id and image tag so the page plays and draws through Jellyfin rather than the station.
/// Never carries the station URL.
/// </summary>
public sealed class FeedResponse
{
    /// <summary>Gets or sets the station name (tile name).</summary>
    public string StationName { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the stream is online; null when unknown.</summary>
    public bool? Online { get; set; }

    /// <summary>Gets or sets the track title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the artist.</summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets the album.</summary>
    public string? Album { get; set; }

    /// <summary>Gets or sets the "Artist – Title" line.</summary>
    public string? TrackLine { get; set; }

    /// <summary>Gets or sets the one-line description the live item carries.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the DJ on air.</summary>
    public string? Dj { get; set; }

    /// <summary>Gets or sets the DJ's tagline.</summary>
    public string? DjTagline { get; set; }

    /// <summary>Gets or sets the active show.</summary>
    public string? Show { get; set; }

    /// <summary>Gets or sets the next show, when the station has announced it.</summary>
    public string? NextShow { get; set; }

    /// <summary>Gets or sets the next show's presenter.</summary>
    public string? NextShowPresenter { get; set; }

    /// <summary>Gets or sets when the next show starts, as the station phrases it.</summary>
    public string? NextShowStartsAt { get; set; }

    /// <summary>Gets or sets the current listener count.</summary>
    public int? Listeners { get; set; }

    /// <summary>Gets or sets the live item's Jellyfin id, or null when it does not exist yet.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>Gets or sets the live item's Primary image tag (changes with the cover), or null when it has no image.</summary>
    public string? ImageTag { get; set; }

    /// <summary>Gets or sets the time of the last successful poll (UTC), or null when the station has not answered.</summary>
    public DateTime? FetchedUtc { get; set; }
}
