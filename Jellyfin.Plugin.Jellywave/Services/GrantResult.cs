namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Counts from one access-grant run.
/// </summary>
public sealed class GrantResult
{
    /// <summary>
    /// A run that did nothing because the setting is off or the channel is unknown.
    /// </summary>
    public static readonly GrantResult Disabled = new() { Enabled = false };

    /// <summary>Gets or sets a value indicating whether the setting was on and the channel was found.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the number of users granted access in this run.</summary>
    public int Granted { get; set; }

    /// <summary>Gets or sets the number of users who could already see the channel.</summary>
    public int AlreadyVisible { get; set; }

    /// <summary>Gets or sets the number of users with an explicit block list naming the channel.</summary>
    public int Blocked { get; set; }

    /// <summary>Gets or sets a short error category when the pass could not run.</summary>
    public string? Error { get; set; }
}
