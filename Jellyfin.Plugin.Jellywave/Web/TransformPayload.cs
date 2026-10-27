namespace Jellyfin.Plugin.Jellywave.Web;

/// <summary>
/// What the File Transformation plugin hands to a callback: the file as currently served.
/// The plugin converts its JSON <c>{"contents": "..."}</c> into this type by property name.
/// </summary>
public sealed class TransformPayload
{
    /// <summary>Gets or sets the file contents.</summary>
    public string? Contents { get; set; }
}
