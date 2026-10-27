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
/// A cover is known by its station origin and id (<see cref="AppliedOrigin"/>, <see cref="Applied"/>), so two stations
/// that use the same id never share art. A cover the station has no art for is remembered for
/// <see cref="MissingCoverRetry"/>, so a track without art costs one request, not one per poll.
/// </summary>
public sealed class LiveItemArtwork
{
    /// <summary>
    /// Marker for "the embedded logo is the current image" in <see cref="Applied"/>.
    /// </summary>
    public const string LogoKey = "logo";

    /// <summary>
    /// How long a cover the station had no art for is shown as the logo without asking the station again
    /// (art added on the station later shows within this time).
    /// </summary>
    public static readonly TimeSpan MissingCoverRetry = TimeSpan.FromMinutes(30);

    private readonly StationClient _stationClient;
    private readonly Func<BaseItem, Stream, string, CancellationToken, Task> _saveImage;
    private readonly ILogger<LiveItemArtwork> _logger;
    private readonly TimeProvider _clock;
    private string? _missingOrigin;
    private string? _missingCoverId;
    private DateTimeOffset _missingUntil;

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
        : this(stationClient, saveImage, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveItemArtwork"/> class with an explicit image saver and clock (tests).
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="saveImage">Saves a stream as the item's Primary image.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="clock">The clock, for <see cref="MissingCoverRetry"/>.</param>
    internal LiveItemArtwork(StationClient stationClient, Func<BaseItem, Stream, string, CancellationToken, Task> saveImage, ILogger<LiveItemArtwork> logger, TimeProvider clock)
    {
        _stationClient = stationClient;
        _saveImage = saveImage;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Gets the cover id last saved onto the item, <see cref="LogoKey"/> for the logo, or null when unknown (fresh start).
    /// </summary>
    public string? Applied { get; private set; }

    /// <summary>
    /// Gets the station origin (<see cref="StationUrls.Origin"/>) of the cover last saved onto the item, or null for the
    /// logo and when unknown. Together with <see cref="Applied"/> it is the applied key.
    /// </summary>
    public string? AppliedOrigin { get; private set; }

    /// <summary>
    /// Decides which image the item should show and downloads the cover when it is not the one already applied.
    /// A cover the station had no art for is not asked for again until its id or the origin changes or
    /// <see cref="MissingCoverRetry"/> has passed. Station failures (timeouts, oversized chunked bodies) propagate so
    /// the caller can retry on the next poll.
    /// </summary>
    /// <param name="baseUrl">The station base URL, or null when unknown.</param>
    /// <param name="coverId">The wanted cover id, or null for the logo.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What to apply; its image is null when the logo should be used or the cover is already applied.</returns>
    public async Task<PreparedArtwork> PrepareAsync(Uri? baseUrl, string? coverId, CancellationToken cancellationToken)
    {
        if (coverId is null || baseUrl is null)
        {
            return new PreparedArtwork(null, LogoKey, null);
        }

        var origin = StationUrls.Origin(baseUrl);
        if (string.Equals(coverId, Applied, StringComparison.Ordinal) && string.Equals(origin, AppliedOrigin, StringComparison.Ordinal))
        {
            return new PreparedArtwork(origin, coverId, null);
        }

        if (string.Equals(coverId, _missingCoverId, StringComparison.Ordinal)
            && string.Equals(origin, _missingOrigin, StringComparison.Ordinal)
            && _clock.GetUtcNow() < _missingUntil)
        {
            return new PreparedArtwork(null, LogoKey, null);
        }

        var cover = await _stationClient.GetCoverAsync(baseUrl, coverId, cancellationToken).ConfigureAwait(false);
        if (cover is null)
        {
            _logger.LogDebug("Station has no cover art for {CoverId}; using the logo", coverId);
            _missingOrigin = origin;
            _missingCoverId = coverId;
            _missingUntil = _clock.GetUtcNow() + MissingCoverRetry;
            return new PreparedArtwork(null, LogoKey, null);
        }

        return new PreparedArtwork(origin, coverId, cover);
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
        if (string.Equals(prepared.Key, Applied, StringComparison.Ordinal)
            && string.Equals(prepared.Origin, AppliedOrigin, StringComparison.Ordinal)
            && item.HasImage(ImageType.Primary))
        {
            return false;
        }

        if (prepared.Image is null)
        {
            if (!string.Equals(prepared.Key, LogoKey, StringComparison.Ordinal))
            {
                // The cover was skipped as "already applied" but the item lost its image (external re-save); fetch next poll.
                Applied = null;
                AppliedOrigin = null;
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
        AppliedOrigin = prepared.Origin;
        return true;
    }
}
