namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// What one title sample of a plain stream learned (card L6, <see cref="StationClient.SampleIcyTitleAsync"/>): whether the
/// stream sends in-stream metadata at all, and the <c>StreamTitle</c> of the one block read, as sent (untrusted;
/// <see cref="TitleSampler"/> cleans it). Transport failures are exceptions, not samples.
/// </summary>
public sealed class IcySample
{
    /// <summary>A stream that sends no usable <c>icy-metaint</c> (missing, not a number, or over the cap): no audio was read.</summary>
    public static readonly IcySample NoMetadata = new(false, null);

    private IcySample(bool hasMetadata, string? title)
    {
        HasMetadata = hasMetadata;
        Title = title;
    }

    /// <summary>Gets a value indicating whether the stream sends in-stream metadata that was read.</summary>
    public bool HasMetadata { get; }

    /// <summary>Gets the block's <c>StreamTitle</c> as sent, or null when the block carried none (or was empty).</summary>
    public string? Title { get; }

    /// <summary>
    /// A sample whose metadata block was read.
    /// </summary>
    /// <param name="title">The block's <c>StreamTitle</c> as sent, or null.</param>
    /// <returns>The sample.</returns>
    public static IcySample Read(string? title) => new(true, title);
}
