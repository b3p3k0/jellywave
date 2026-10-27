using System;
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
/// first created, so the image is saved through <see cref="IProviderManager.SaveImage(BaseItem, System.IO.Stream, string, ImageType, int?, CancellationToken)"/>,
/// which writes a local file with a fresh modification time and therefore a new image tag for clients.
/// </summary>
public sealed class LiveItemArtwork
{
    /// <summary>
    /// Marker for "the embedded logo is the current image" in <see cref="Applied"/>.
    /// </summary>
    public const string LogoKey = "logo";

    private readonly StationClient _stationClient;
    private readonly IProviderManager _providerManager;
    private readonly ILogger<LiveItemArtwork> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveItemArtwork"/> class.
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="providerManager">The provider manager that saves item images.</param>
    /// <param name="logger">The logger.</param>
    public LiveItemArtwork(StationClient stationClient, IProviderManager providerManager, ILogger<LiveItemArtwork> logger)
    {
        _stationClient = stationClient;
        _providerManager = providerManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets the cover id last saved onto the item, <see cref="LogoKey"/> for the logo, or null when unknown (fresh start).
    /// </summary>
    public string? Applied { get; private set; }

    /// <summary>
    /// Makes the item's Primary image match <paramref name="coverId"/>: downloads and saves the cover, or saves the logo
    /// when there is no id, no base URL, or the station has no art. Does nothing when the item already shows it.
    /// Station failures (timeouts, bad JSON) propagate so the caller can treat them like a failed poll.
    /// </summary>
    /// <param name="item">The live item.</param>
    /// <param name="baseUrl">The station base URL, or null when unknown.</param>
    /// <param name="coverId">The wanted cover id, or null for the logo.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when an image was saved and the item needs persisting with an image update.</returns>
    public async Task<bool> ApplyAsync(BaseItem item, Uri? baseUrl, string? coverId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var wanted = coverId is not null && baseUrl is not null ? coverId : LogoKey;
        if (string.Equals(wanted, Applied, StringComparison.Ordinal) && item.HasImage(ImageType.Primary))
        {
            return false;
        }

        StationImage? cover = null;
        if (!string.Equals(wanted, LogoKey, StringComparison.Ordinal))
        {
            cover = await _stationClient.GetCoverAsync(baseUrl!, wanted, cancellationToken).ConfigureAwait(false);
            if (cover is null)
            {
                _logger.LogDebug("Station has no cover art for {CoverId}; using the logo", wanted);
                wanted = LogoKey;
                if (string.Equals(wanted, Applied, StringComparison.Ordinal) && item.HasImage(ImageType.Primary))
                {
                    return false;
                }
            }
        }

        if (cover is null)
        {
            using var logo = JellywaveChannel.OpenLogoStream();
            if (logo is null)
            {
                _logger.LogError("Embedded logo resource is missing from the assembly; the live item keeps its current image");
                return false;
            }

            await _providerManager.SaveImage(item, logo, "image/png", ImageType.Primary, null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using var stream = cover.OpenStream();
            await _providerManager.SaveImage(item, stream, cover.MimeType, ImageType.Primary, null, cancellationToken).ConfigureAwait(false);
        }

        Applied = wanted;
        return true;
    }
}
