using System;

namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// The title route's answer (card L6): one plain stream's "Now:" state as the sampler last recorded it. Never a URL.
/// </summary>
public sealed class TitleResponse
{
    /// <summary>Gets or sets the station id the answer is about (the page drops an answer for another station).</summary>
    public string StationId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the state: <c>Waiting</c> (no sample yet), <c>Title</c>, <c>NoTitles</c> (the stream sends none) or
    /// <c>Failing</c> (the last sample failed).
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Gets or sets the title as the stream sent it (cleaned, at most 200 characters); only in the <c>Title</c> state.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets when a sample last read the stream's metadata (UTC), or null.</summary>
    public DateTime? UpdatedUtc { get; set; }
}
