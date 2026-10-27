using System.IO;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// An image fetched from the station: bytes plus the media type the station declared.
/// </summary>
public sealed class StationImage
{
    private readonly byte[] _bytes;

    /// <summary>
    /// Initializes a new instance of the <see cref="StationImage"/> class.
    /// </summary>
    /// <param name="mimeType">The media type, for example <c>image/jpeg</c>.</param>
    /// <param name="bytes">The image bytes.</param>
    public StationImage(string mimeType, byte[] bytes)
    {
        MimeType = mimeType;
        _bytes = bytes;
    }

    /// <summary>Gets the media type.</summary>
    public string MimeType { get; }

    /// <summary>Gets the size in bytes.</summary>
    public int Length => _bytes.Length;

    /// <summary>
    /// Opens a read-only stream over the bytes.
    /// </summary>
    /// <returns>The stream; dispose it when done.</returns>
    public Stream OpenStream() => new MemoryStream(_bytes, writable: false);
}
