using System;
using System.Collections.Generic;
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
/// Keeps each SUB/WAVE station's item image in step with its current track (card L4: every listed SUB/WAVE station): the
/// station's cover art when it has one, the embedded station logo otherwise. Jellyfin applies a channel item's
/// <c>ImageUrl</c> only when the item is first created, so the image is saved through
/// <see cref="IProviderManager.SaveImage(BaseItem, Stream, string, ImageType, int?, CancellationToken)"/>, which writes a
/// local file with a fresh modification time and therefore a new image tag for clients.
/// Two steps on purpose: <see cref="PrepareAsync"/> does the (slow) download before the caller looks the item up, and
/// <see cref="ApplyAsync"/> saves, so the window in which Jellyfin's own channel refresh can overwrite the item stays short.
/// Per station id, a cover is known by its station origin and id (<see cref="AppliedOrigin"/>, <see cref="Applied"/>), so a
/// station whose base URL changed, or two stations whose tracks share an id, never share art. A cover the station has no
/// art for is remembered per station (origin and cover id) for <see cref="MissingCoverRetry"/>, so a track without art
/// costs one request, not one per poll. The poller drops the memory of stations it no longer polls (<see cref="Retain"/>),
/// so both maps hold at most one entry per listed SUB/WAVE station. Thread-safe: the poller prepares several stations at
/// once.
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
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Key, string? Origin)> _applied = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Origin, string CoverId, DateTimeOffset Until)> _missing = new(StringComparer.Ordinal);

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
    /// Gets what a station's item should show when there is no cover to try: the embedded logo.
    /// </summary>
    /// <param name="stationId">The station id.</param>
    /// <returns>The prepared logo.</returns>
    public static PreparedArtwork Logo(string stationId) => new(stationId, null, LogoKey, null);

    /// <summary>
    /// Gets the cover id last saved onto a station's item, <see cref="LogoKey"/> for the logo, or null when unknown (fresh
    /// start, or the station is no longer polled).
    /// </summary>
    /// <param name="stationId">The station id.</param>
    /// <returns>The applied key.</returns>
    public string? Applied(string stationId)
    {
        lock (_gate)
        {
            return _applied.TryGetValue(stationId, out var applied) ? applied.Key : null;
        }
    }

    /// <summary>
    /// Gets the station origin (<see cref="StationUrls.Origin"/>) of the cover last saved onto a station's item, or null for
    /// the logo and when unknown. Together with <see cref="Applied"/> it is the applied key.
    /// </summary>
    /// <param name="stationId">The station id.</param>
    /// <returns>The applied origin.</returns>
    public string? AppliedOrigin(string stationId)
    {
        lock (_gate)
        {
            return _applied.TryGetValue(stationId, out var applied) ? applied.Origin : null;
        }
    }

    /// <summary>
    /// Decides which image a station's item should show and downloads the cover when it is not the one already applied.
    /// A cover the station had no art for is not asked for again until its id or the origin changes or
    /// <see cref="MissingCoverRetry"/> has passed. Station failures (timeouts, oversized chunked bodies) propagate so
    /// the caller can retry on the next poll.
    /// </summary>
    /// <param name="stationId">The station id.</param>
    /// <param name="baseUrl">The station base URL, or null when unknown.</param>
    /// <param name="coverId">The wanted cover id, or null for the logo.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What to apply; its image is null when the logo should be used or the cover is already applied.</returns>
    public async Task<PreparedArtwork> PrepareAsync(string stationId, Uri? baseUrl, string? coverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        if (coverId is null || baseUrl is null)
        {
            return Logo(stationId);
        }

        var origin = StationUrls.Origin(baseUrl);
        lock (_gate)
        {
            if (_applied.TryGetValue(stationId, out var applied)
                && string.Equals(coverId, applied.Key, StringComparison.Ordinal)
                && string.Equals(origin, applied.Origin, StringComparison.Ordinal))
            {
                return new PreparedArtwork(stationId, origin, coverId, null);
            }

            if (_missing.TryGetValue(stationId, out var missing)
                && string.Equals(coverId, missing.CoverId, StringComparison.Ordinal)
                && string.Equals(origin, missing.Origin, StringComparison.Ordinal)
                && _clock.GetUtcNow() < missing.Until)
            {
                return Logo(stationId);
            }
        }

        var cover = await _stationClient.GetCoverAsync(baseUrl, coverId, cancellationToken).ConfigureAwait(false);
        if (cover is null)
        {
            _logger.LogDebug("Station {StationId} has no cover art for {CoverId}; using the logo", stationId, coverId);
            lock (_gate)
            {
                _missing[stationId] = (origin, coverId, _clock.GetUtcNow() + MissingCoverRetry);
            }

            return Logo(stationId);
        }

        return new PreparedArtwork(stationId, origin, coverId, cover);
    }

    /// <summary>
    /// Drops what is remembered of every station not in <paramref name="stationIds"/> (applied key and missing cover), so
    /// a station that comes back, or changes kind and back, decides its image afresh.
    /// </summary>
    /// <param name="stationIds">The ids of the listed SUB/WAVE stations.</param>
    public void Retain(IReadOnlySet<string> stationIds)
    {
        ArgumentNullException.ThrowIfNull(stationIds);
        lock (_gate)
        {
            foreach (var id in new List<string>(_applied.Keys))
            {
                if (!stationIds.Contains(id))
                {
                    _applied.Remove(id);
                }
            }

            foreach (var id in new List<string>(_missing.Keys))
            {
                if (!stationIds.Contains(id))
                {
                    _missing.Remove(id);
                }
            }
        }
    }

    /// <summary>
    /// Saves the prepared image onto the station's item unless the item already shows it.
    /// </summary>
    /// <param name="item">The station's item.</param>
    /// <param name="prepared">The result of <see cref="PrepareAsync"/> for that station.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when an image was saved and the item needs persisting with an image update.</returns>
    public async Task<bool> ApplyAsync(BaseItem item, PreparedArtwork prepared, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(prepared);
        lock (_gate)
        {
            if (_applied.TryGetValue(prepared.StationId, out var applied)
                && string.Equals(prepared.Key, applied.Key, StringComparison.Ordinal)
                && string.Equals(prepared.Origin, applied.Origin, StringComparison.Ordinal)
                && item.HasImage(ImageType.Primary))
            {
                return false;
            }

            if (prepared.Image is null && !string.Equals(prepared.Key, LogoKey, StringComparison.Ordinal))
            {
                // The cover was skipped as "already applied" but the item lost its image (external re-save); fetch next poll.
                _applied.Remove(prepared.StationId);
                return false;
            }
        }

        // CancellationToken.None on purpose: Jellyfin 12.3's ImageSaver.SaveImageToLocation (ImageSaver.cs:277-303) opens
        // the target with FileMode.Create before it observes the token, so a stop in the middle of a save would leave a truncated image.
        // A save is local and short: the poll's token is checked before it starts and again by the caller's write after it.
        cancellationToken.ThrowIfCancellationRequested();
        if (prepared.Image is null)
        {
            using var logo = JellywaveChannel.OpenLogoStream();
            if (logo is null)
            {
                _logger.LogError("Embedded logo resource is missing from the assembly; the item keeps its current image");
                return false;
            }

            await _saveImage(item, logo, "image/png", CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            using var stream = prepared.Image.OpenStream();
            await _saveImage(item, stream, prepared.Image.MimeType, CancellationToken.None).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _applied[prepared.StationId] = (prepared.Key, prepared.Origin);
        }

        return true;
    }
}
