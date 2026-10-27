namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>A like from the station page.</summary>
public sealed class LikeSubmission
{
    /// <summary>Gets or sets the song id the listener saw.</summary>
    public string? SongId { get; set; }
}
