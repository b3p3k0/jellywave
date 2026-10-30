namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>A listener request from the station page.</summary>
public sealed class RequestSubmission : IStationSubmission
{
    /// <summary>Gets or sets the request text.</summary>
    public string? Text { get; set; }

    /// <summary>Gets or sets the listener's name as they want the DJ to see it.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the station the request is for; null for the primary station.</summary>
    public string? StationId { get; set; }
}
