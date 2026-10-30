namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// One result line of the add-station probe. The text is a fixed sentence, the admin's own address, an address the probe
/// accepted (a redirect target or a playlist entry that passes the whole-URL rule) or a cleaned station name; the page
/// renders it as text.
/// </summary>
public sealed class ProbeLine
{
    /// <summary>Level: a step that worked.</summary>
    public const string Ok = "ok";

    /// <summary>Level: a warning; the station can still be added.</summary>
    public const string Warning = "warn";

    /// <summary>Level: the check failed.</summary>
    public const string Error = "error";

    /// <summary>Level: the address redirected; <see cref="StationProbeResult.Redirect"/> carries the address to use.</summary>
    public const string Redirect = "redirect";

    /// <summary>
    /// Initializes a new instance of the <see cref="ProbeLine"/> class.
    /// </summary>
    /// <param name="level">One of <see cref="Ok"/>, <see cref="Warning"/>, <see cref="Error"/>, <see cref="Redirect"/>.</param>
    /// <param name="text">The line.</param>
    public ProbeLine(string level, string text)
    {
        Level = level;
        Text = text;
    }

    /// <summary>Gets the level.</summary>
    public string Level { get; }

    /// <summary>Gets the text.</summary>
    public string Text { get; }
}
