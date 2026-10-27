using System;

namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>The poller's health for the configuration page.</summary>
public sealed class HealthResponse
{
    /// <summary>Gets or sets a value indicating whether now-playing polling is switched on.</summary>
    public bool PollingEnabled { get; set; }

    /// <summary>Gets or sets a value indicating whether a station base URL is configured.</summary>
    public bool StationConfigured { get; set; }

    /// <summary>
    /// Gets or sets why a non-empty station base URL is refused (a fixed sentence, never part of the value), or null
    /// when the URL is usable or blank.
    /// </summary>
    public string? StationUrlRefusal { get; set; }

    /// <summary>
    /// Gets or sets the status line's station part when the URL is not usable: "no station URL configured" or
    /// "station URL is set but not allowed: …"; null when it is usable.
    /// </summary>
    public string? StationUrlStatus { get; set; }

    /// <summary>
    /// Gets or sets the status line's channel part when the channel name collides with another plugin's channel
    /// ("channel name collides with another plugin's channel"), resolved when the health is read; null otherwise.
    /// </summary>
    public string? ChannelStatus { get; set; }

    /// <summary>Gets or sets a value indicating whether the poll loop is running.</summary>
    public bool Running { get; set; }

    /// <summary>Gets or sets whether the last poll reached the station; null before the first poll.</summary>
    public bool? Reachable { get; set; }

    /// <summary>Gets or sets the time of the last successful poll.</summary>
    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>Gets or sets the time of the last failure.</summary>
    public DateTime? LastErrorUtc { get; set; }

    /// <summary>Gets or sets the last failure's message.</summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets the live item's current description line.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets which image the live item carries: a cover id, "logo", or null when not yet applied.</summary>
    public string? Cover { get; set; }

    /// <summary>Gets or sets a value indicating whether the live item exists in Jellyfin yet.</summary>
    public bool LiveItemExists { get; set; }
}
