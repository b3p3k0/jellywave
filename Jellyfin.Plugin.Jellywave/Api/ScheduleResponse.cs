using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>Today's programme for the station page.</summary>
public sealed class ScheduleResponse
{
    /// <summary>Gets or sets a value indicating whether the station gave a schedule.</summary>
    public bool Available { get; set; }

    /// <summary>Gets or sets the station's time zone (IANA), so the page can say whose "today" this is.</summary>
    public string? Timezone { get; set; }

    /// <summary>Gets or sets the day name in the station's time zone.</summary>
    public string? Day { get; set; }

    /// <summary>Gets or sets the station's current hour (0–23).</summary>
    public int? Hour { get; set; }

    /// <summary>Gets or sets the show on air when it is not the grid's (a timed takeover), else null.</summary>
    public string? Takeover { get; set; }

    /// <summary>Gets or sets today's blocks in order; gaps are hours with no show (default programming).</summary>
    public IReadOnlyList<ScheduleBlock> Today { get; set; } = [];
}
