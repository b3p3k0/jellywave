using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Subset of <c>GET /api/schedule</c>: the weekly grid is <c>schedule["0".."6"]</c> (Sunday first) of 24 show ids or
/// nulls, in the station's time zone.
/// </summary>
public sealed class ScheduleInfo
{
    /// <summary>Gets or sets the personas.</summary>
    [JsonPropertyName("personas")]
    public IReadOnlyList<SchedulePersona>? Personas { get; set; }

    /// <summary>Gets or sets the shows.</summary>
    [JsonPropertyName("shows")]
    public IReadOnlyList<ScheduleShow>? Shows { get; set; }

    /// <summary>Gets or sets the grid: day of week (0 = Sunday) to 24 hourly show ids.</summary>
    [JsonPropertyName("schedule")]
    public IReadOnlyDictionary<string, List<string?>>? Schedule { get; set; }

    /// <summary>Gets or sets the station's IANA time zone.</summary>
    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }
}
