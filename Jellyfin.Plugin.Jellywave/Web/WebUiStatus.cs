namespace Jellyfin.Plugin.Jellywave.Web;

/// <summary>
/// Result of the last registration attempt, shown on the configuration page.
/// </summary>
public sealed class WebUiStatus
{
    /// <summary>Gets or sets the state.</summary>
    public WebUiState State { get; set; }

    /// <summary>Gets or sets a one-line explanation for the admin.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the File Transformation plugin version found, if any.</summary>
    public string? FileTransformationVersion { get; set; }
}
