using System.Collections.Generic;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// What the configuration page needs for the rename notice: the earlier tile names and how many users still name one
/// of them without the new channel id. Read without writing anything.
/// </summary>
public sealed class CarryOverStatus
{
    /// <summary>
    /// Gets or sets the earlier tile names a carry-over would still act on: ids differ from the current tile's, not carried
    /// yet, not another plugin's channel; in the order recorded.
    /// </summary>
    public IReadOnlyList<string> PreviousNames { get; set; } = [];

    /// <summary>Gets or sets the tile name in effect.</summary>
    public string TileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of users a "Give them access to the renamed tile" run would change now.</summary>
    public int Pending { get; set; }

    /// <summary>
    /// Gets or sets how many earlier tile names now derive the id of another plugin's registered channel. A carry-over
    /// skips them (it would act on that channel's users); blocks on them still hide this tile.
    /// </summary>
    public int SkippedOtherChannel { get; set; }

    /// <summary>Gets or sets a short reason when the count could not be taken; null otherwise.</summary>
    public string? Error { get; set; }
}
