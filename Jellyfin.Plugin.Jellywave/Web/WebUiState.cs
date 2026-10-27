namespace Jellyfin.Plugin.Jellywave.Web;

/// <summary>
/// Whether the station page is injected into the web client.
/// </summary>
public enum WebUiState
{
    /// <summary>The startup task has not run yet.</summary>
    Unknown,

    /// <summary>The File Transformation plugin is not installed; the page is unavailable (everything else works).</summary>
    NotInstalled,

    /// <summary>The transformations are registered; the page is served with the web client.</summary>
    Active,

    /// <summary>The File Transformation plugin was found but registration failed; see the server log.</summary>
    Failed
}
