namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>Like status or result for the station page.</summary>
public sealed class LikeOutcome
{
    /// <summary>Gets or sets a value indicating whether the station has likes enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the likeable song id, or null when nothing is likeable right now.</summary>
    public string? SongId { get; set; }

    /// <summary>Gets or sets a value indicating whether the server (all Jellyfin users count as one caller to the station) has liked it.</summary>
    public bool Liked { get; set; }

    /// <summary>Gets or sets the like count.</summary>
    public int Count { get; set; }

    /// <summary>Gets or sets a human line when the like was refused.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets the seconds to wait before trying again.</summary>
    public int? RetryAfterSeconds { get; set; }
}
