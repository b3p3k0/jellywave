namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>A like from the station page.</summary>
public sealed class LikeSubmission : IStationSubmission
{
    /// <summary>Gets or sets the song id the listener saw.</summary>
    public string? SongId { get; set; }

    /// <summary>Gets or sets the station the like is for; null for the primary station.</summary>
    public string? StationId { get; set; }
}
