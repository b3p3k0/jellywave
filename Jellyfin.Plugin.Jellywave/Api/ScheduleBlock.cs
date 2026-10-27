namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>A run of consecutive hours with the same show, in the station's time zone.</summary>
public sealed class ScheduleBlock
{
    /// <summary>Gets or sets the start hour (0–23).</summary>
    public int StartHour { get; set; }

    /// <summary>Gets or sets the end hour, exclusive (1–24).</summary>
    public int EndHour { get; set; }

    /// <summary>Gets or sets the show name.</summary>
    public string Show { get; set; } = string.Empty;

    /// <summary>Gets or sets the host's name, when known.</summary>
    public string? Host { get; set; }

    /// <summary>Gets or sets a value indicating whether the station's clock is inside this block now.</summary>
    public bool Now { get; set; }
}
