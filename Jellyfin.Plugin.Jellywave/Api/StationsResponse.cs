using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// The station page's station list (cards L5, L8): the channel the page attaches to and every listed station in grid
/// order, both kinds (the header shows for every playing station). Never carries a stream or station URL.
/// </summary>
public sealed class StationsResponse
{
    /// <summary>Gets or sets the channel's Jellyfin id (the library the page attaches to).</summary>
    public Guid ChannelId { get; set; }

    /// <summary>Gets or sets the tile name, for the legacy drawer link.</summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>Gets or sets the listed stations, in grid order.</summary>
    public IReadOnlyList<StationListEntry> Stations { get; set; } = [];
}
