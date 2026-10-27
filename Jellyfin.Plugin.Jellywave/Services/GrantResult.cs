namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Counts from one access-grant run, or why it did not run.
/// </summary>
public sealed class GrantResult
{
    /// <summary>Gets or sets the number of users granted access in this run.</summary>
    public int Granted { get; set; }

    /// <summary>Gets or sets the number of users who could already see the channel.</summary>
    public int AlreadyVisible { get; set; }

    /// <summary>Gets or sets the number of users with an explicit block list naming the channel.</summary>
    public int Blocked { get; set; }

    /// <summary>Gets or sets a short reason when the run did nothing; null when it ran.</summary>
    public string? Error { get; set; }
}
