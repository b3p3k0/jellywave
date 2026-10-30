namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// The body of <c>POST /Jellywave/Probe</c>: the stream address the admin pasted into the "Add station" dialog.
/// </summary>
public sealed class ProbeRequest
{
    /// <summary>Gets or sets the address to check.</summary>
    public string? Url { get; set; }

    /// <summary>
    /// Gets or sets the stored base URL of the SUB/WAVE station being edited, or null. The probe also tries it when the
    /// address lies under it, so a re-check does not lose a station whose stream path sits deeper than its directory.
    /// </summary>
    public string? BaseUrl { get; set; }
}
