namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Whose listener request a station request id is, according to <see cref="RequestReceipts"/>.
/// </summary>
public enum ReceiptOwnership
{
    /// <summary>The caller submitted it and the receipt has not expired.</summary>
    Owner,

    /// <summary>Another user submitted it.</summary>
    OtherUser,

    /// <summary>No live receipt: never issued here, expired, evicted, or lost in a restart.</summary>
    Unknown
}
