using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Keeps the live item's Primary image in step with the current track: the station's cover art when it has one,
/// the embedded station logo otherwise. Jellyfin applies a channel item's <c>ImageUrl</c> only when the item is
/// first created, so the image is saved through <see cref="IProviderManager.SaveImage(BaseItem, Stream, string, ImageType, int?, CancellationToken)"/>,
/// which writes a local file with a fresh modification time and therefore a new image tag for clients.
/// Two steps on purpose: <see cref="PrepareAsync"/> does the (slow) download before the caller looks the item up,
/// and <see cref="ApplyAsync"/> saves, so the window in which Jellyfin's own channel refresh can overwrite the item stays short.
/// </summary>
public sealed class LiveItemArtwork
{
    /// <summary>
    /// Marker for "the embedded logo is the current image" in <see cref="Applied"/>.
    /// </summary>
    public const string LogoKey = "logo";

    private readonly StationClient _stationClient;
    private readonly Func<BaseItem, Stream, string, CancellationToken, Task> _saveImage;
    private readonly ILogger<LiveItemArtwork> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveItemArtwork"/> class. DI resolves this constructor.
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="providerManager">The provider manager that saves item images.</param>
    /// <param name="logger">The logger.</param>
    public LiveItemArtwork(StationClient stationClient, IProviderManager providerManager, ILogger<LiveItemArtwork> logger)
        : this(stationClient, (item, stream, mime, ct) => providerManager.SaveImage(item, stream, mime, ImageType.Primary, null, ct), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveItemArtwork"/> class with an explicit image saver (tests).
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="saveImage">Saves a stream as the item's Primary image.</param>
    /// <param name="logger">The logger.</param>
    public LiveItemArtwork(StationClient stationClient, Func<BaseItem, Stream, string, CancellationToken, Task> saveImage, ILogger<LiveItemArtwork> logger)
    {
        _stationClient = stationClient;
        _saveImage = saveImage;
        _logger = logger;
    }

    /// <summary>
    /// Gets the cover id last saved onto the item, <see cref="LogoKey"/> for the logo, or null when unknown (fresh start).
    /// </summary>
    public string? Applied { get; private set; }

    /// <summary>
    /// Decides which image the item should show and downloads the cover when it is not the one already applied.
    /// Station failures (timeouts, oversized chunked bodies) propagate so the caller can retry on the next poll.
    /// </summary>
    /// <param name="baseUrl">The station base URL, or null when unknown.</param>
    /// <param name="coverId">The wanted cover id, or null for the logo.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What to apply; its image is null when the logo should be used or the cover is already applied.</returns>
    public async Task<PreparedArtwork> PrepareAsync(Uri? baseUrl, string? coverId, CancellationToken cancellationToken)
    {
        if (coverId is null || baseUrl is null)
        {
            return new PreparedArtwork(LogoKey, null);
        }

        if (string.Equals(coverId, Applied, StringComparison.Ordinal))
        {
            return new PreparedArtwork(coverId, null);
        }

        var cover = await _stationClient.GetCoverAsync(baseUrl, coverId, cancellationToken).ConfigureAwait(false);
        if (cover is null)
        {
            _logger.LogDebug("Station has no cover art for {CoverId}; using the logo", coverId);
            return new PreparedArtwork(LogoKey, null);
        }

        return new PreparedArtwork(coverId, cover);
    }

    /// <summary>
    /// Saves the prepared image onto the item unless the item already shows it.
    /// </summary>
    /// <param name="item">The live item.</param>
    /// <param name="prepared">The result of <see cref="PrepareAsync"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when an image was saved and the item needs persisting with an image update.</returns>
    public async Task<bool> ApplyAsync(BaseItem item, PreparedArtwork prepared, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(prepared);
        if (string.Equals(prepared.Key, Applied, StringComparison.Ordinal) && item.HasImage(ImageType.Primary))
        {
            return false;
        }

        if (prepared.Image is null)
        {
            if (!string.Equals(prepared.Key, LogoKey, StringComparison.Ordinal))
            {
                // The cover was skipped as "already applied" but the item lost its image (external re-save); fetch next poll.
                Applied = null;
                return false;
            }

            using var logo = JellywaveChannel.OpenLogoStream();
            if (logo is null)
            {
                _logger.LogError("Embedded logo resource is missing from the assembly; the live item keeps its current image");
                return false;
            }

            await _saveImage(item, logo, "image/png", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using var stream = prepared.Image.OpenStream();
            await _saveImage(item, stream, prepared.Image.MimeType, cancellationToken).ConfigureAwait(false);
        }

        Applied = prepared.Key;
        return true;
    }
}
