namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Counts from one "Give them access to the renamed tile" run (rename bridge, card L1), or why it did not run. A user whose enabled list
/// and block list both name an earlier tile counts in both carried columns.
/// </summary>
public sealed class CarryOverResult
{
    /// <summary>Gets or sets the number of users whose enabled list got the new channel id.</summary>
    public int CarriedGrants { get; set; }

    /// <summary>Gets or sets the number of users whose block list got the new channel id.</summary>
    public int CarriedBlocks { get; set; }

    /// <summary>Gets or sets the number of users who named an earlier tile and already had the new id where needed.</summary>
    public int AlreadyCurrent { get; set; }

    /// <summary>Gets or sets the number of users whose lists name no earlier tile.</summary>
    public int Untouched { get; set; }

    /// <summary>Gets or sets a short reason when the run did nothing; null when it ran.</summary>
    public string? Error { get; set; }
}
