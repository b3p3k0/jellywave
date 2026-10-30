using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Keeps the Primary image of the plain streams' items (card L2; every SUB/WAVE item gets its track art from
/// <see cref="LiveItemArtwork"/> since card L4): a stream's own logo, downloaded by the plugin from
/// <see cref="StationDefinition.LogoUrl"/> through <see cref="StationClient.GetLogoAsync"/> (no redirects, 8 s, 4 MB,
/// <see cref="ImageSniffer"/>), and the embedded logo for a stream without a usable logo. Never through Jellyfin's
/// <c>ImageUrl</c>, which Jellyfin fetches with its own client.
/// A logo is known by its key (station id, kind and logo URL). It is downloaded again only when the key changes or the
/// item has lost its Primary image. A logo that fails (refused URL excluded: that one never downloads) is remembered for
/// <see cref="FailedLogoRetry"/>, so a broken logo costs one request per period, not one per poll.
/// Two steps, like <see cref="LiveItemArtwork"/>: <see cref="PrepareAsync"/> downloads before the caller holds a fresh item,
/// <see cref="ApplyAsync"/> saves.
/// </summary>
public sealed class StationLogos
{
    /// <summary>
    /// Most logo downloads one poll starts (<see cref="BeginPoll"/>); the rest wait for the next poll. Each download is
    /// bounded by <see cref="StationClient"/> (8 s for the headers, 8 s for the body), so logos delay the next now-playing
    /// poll by at most twice that per download, whatever the number of stations.
    /// </summary>
    public const int MaxDownloadsPerPoll = 2;

    /// <summary>Logo status (<see cref="Status"/>): the stream's own logo is applied to its item.</summary>
    public const string StatusOwn = "own";

    /// <summary>Logo status: the stream has no logo URL, so its item shows the embedded logo.</summary>
    public const string StatusEmbedded = "embedded";

    /// <summary>Logo status: the stream's logo is waiting for its download (the poll budget, or the item is not created yet).</summary>
    public const string StatusDeferred = "deferred";

    /// <summary>Logo status: the logo URL is refused, or the logo failed (download or save) and is tried again later.</summary>
    public const string StatusFailed = "failed";

    private const string RefusedLogoTemplate = "Logo URL of station {Name} is not allowed ({Reason}); the item shows the embedded logo";
    private const string FailedLogoTemplate = "Logo of station {Name} could not be used ({Reason}); the item shows the embedded logo and the logo is tried again later";

    /// <summary>
    /// How long a logo that failed (unreachable, a redirect, not an image, too large) is not asked for again; the item
    /// shows the embedded logo meanwhile. The card 17 pattern (<see cref="LiveItemArtwork.MissingCoverRetry"/>).
    /// </summary>
    public static readonly TimeSpan FailedLogoRetry = TimeSpan.FromMinutes(30);

    private readonly StationClient _stationClient;
    private readonly Func<BaseItem, Stream, string, CancellationToken, Task> _saveImage;
    private readonly ILogger<StationLogos> _logger;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Key, bool Embedded)> _applied = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _failedUntil = new(StringComparer.Ordinal);
    private readonly WarningStreaks _warnings = new();
    private int _downloadsLeft = MaxDownloadsPerPoll;

    /// <summary>
    /// Initializes a new instance of the <see cref="StationLogos"/> class. DI resolves this constructor.
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="providerManager">The provider manager that saves item images.</param>
    /// <param name="logger">The logger.</param>
    public StationLogos(StationClient stationClient, IProviderManager providerManager, ILogger<StationLogos> logger)
        : this(stationClient, (item, stream, mime, ct) => providerManager.SaveImage(item, stream, mime, ImageType.Primary, null, ct), logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StationLogos"/> class with an explicit image saver and clock (tests).
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="saveImage">Saves a stream as the item's Primary image.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="clock">The clock, for <see cref="FailedLogoRetry"/>.</param>
    internal StationLogos(StationClient stationClient, Func<BaseItem, Stream, string, CancellationToken, Task> saveImage, ILogger<StationLogos> logger, TimeProvider clock)
    {
        _stationClient = stationClient;
        _saveImage = saveImage;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Gets the logo key of a station: its id, its kind and, for a plain stream, its logo URL (trimmed). A change of any
    /// of them is a different logo.
    /// </summary>
    /// <param name="station">The station.</param>
    /// <returns>The key.</returns>
    public static string KeyOf(StationDefinition station)
    {
        ArgumentNullException.ThrowIfNull(station);
        var logoUrl = station.Kind == StationKind.Stream ? station.LogoUrl?.Trim() : null;
        return string.Join('\n', station.Id, station.Kind.ToString(), logoUrl ?? string.Empty);
    }

    /// <summary>
    /// Starts a poll: up to <see cref="MaxDownloadsPerPoll"/> downloads may follow.
    /// </summary>
    public void BeginPoll()
    {
        lock (_gate)
        {
            _downloadsLeft = MaxDownloadsPerPoll;
        }
    }

    /// <summary>
    /// Decides which image a station's item should get and downloads the station's logo when it has to, within the
    /// poll's download budget (<see cref="BeginPoll"/>); a download over budget is left for a later poll.
    /// </summary>
    /// <param name="station">A listed plain stream (a SUB/WAVE station gets the embedded logo).</param>
    /// <param name="itemHasImage">Whether the item, as just read, has a Primary image.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What to save, or null when the item already shows the right image or the download has to wait.</returns>
    public async Task<PreparedLogo?> PrepareAsync(StationDefinition station, bool itemHasImage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(station);
        var key = KeyOf(station);
        var now = _clock.GetUtcNow();
        bool embeddedApplied, ownApplied, failing;
        lock (_gate)
        {
            var known = _applied.TryGetValue(station.Id, out var applied) && string.Equals(applied.Key, key, StringComparison.Ordinal);
            embeddedApplied = itemHasImage && known && applied.Embedded;
            ownApplied = itemHasImage && known && !applied.Embedded;
            failing = _failedUntil.TryGetValue(key, out var until) && now < until;
        }

        var embedded = embeddedApplied ? null : new PreparedLogo(station.Id, key, null);
        if (station.Kind != StationKind.Stream || string.IsNullOrWhiteSpace(station.LogoUrl))
        {
            _warnings.End(station.Id);
            return embedded;
        }

        if (!StationUrls.TryParseLogoUrl(station.LogoUrl, out var logoUrl) || logoUrl is null)
        {
            WarnOnce(key, RefusedLogoTemplate, station, StationUrls.StreamUrlRefusal(station.LogoUrl));
            return embedded;
        }

        if (ownApplied)
        {
            return null;
        }

        if (failing)
        {
            return embedded;
        }

        lock (_gate)
        {
            if (_downloadsLeft <= 0)
            {
                // Over budget: the real logo waits for a later poll, but an item with no image gets the embedded logo now
                // (an applied embedded logo does not stop the later download).
                return itemHasImage ? null : embedded;
            }

            _downloadsLeft--;
        }

        StationImage? image;
        try
        {
            image = await _stationClient.GetLogoAsync(logoUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug("Logo download for station {Name} failed: {Message}", JellywaveChannel.StationName(station), ex.Message);
            image = null;
        }

        lock (_gate)
        {
            if (image is null)
            {
                Prune(now);
                _failedUntil[key] = now + FailedLogoRetry;
            }
            else
            {
                _failedUntil.Remove(key);
            }
        }

        if (image is null)
        {
            WarnOnce(key, FailedLogoTemplate, station, "unreachable, redirected, not a JPEG, PNG, GIF or WebP image, or too large");
            return embedded;
        }

        _warnings.End(station.Id);

        return new PreparedLogo(station.Id, key, image);
    }

    /// <summary>
    /// Saves the prepared image onto the item and remembers it.
    /// </summary>
    /// <param name="item">The station's item, freshly read.</param>
    /// <param name="prepared">The result of <see cref="PrepareAsync"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when an image was saved and the item needs persisting with an image update.</returns>
    public async Task<bool> ApplyAsync(BaseItem item, PreparedLogo prepared, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(prepared);
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

            await _saveImage(item, logo, ImageSniffer.Png, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            using var stream = prepared.Image.OpenStream();
            await _saveImage(item, stream, prepared.Image.MimeType, CancellationToken.None).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _applied[prepared.StationId] = (prepared.Key, prepared.Image is null);
        }

        return true;
    }

    /// <summary>
    /// Records that saving a prepared logo failed (metadata folder not writable, disk full): a downloaded logo joins the
    /// negative cache, so the next polls show the embedded logo instead of downloading it again every poll.
    /// </summary>
    /// <param name="prepared">The logo whose save failed.</param>
    public void SaveFailed(PreparedLogo prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.Image is null)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            Prune(now);
            _failedUntil[prepared.Key] = now + FailedLogoRetry;
        }
    }

    /// <summary>
    /// Drops what is remembered about stations that are no longer listed plain streams (applied logos, warning streaks), so
    /// a station that comes back, or changes kind and back, decides its image afresh.
    /// </summary>
    /// <param name="listedIds">The ids of the plain streams listed now.</param>
    public void Retain(IReadOnlySet<string> listedIds)
    {
        ArgumentNullException.ThrowIfNull(listedIds);
        _warnings.Retain(listedIds);
        lock (_gate)
        {
            foreach (var id in new List<string>(_applied.Keys))
            {
                if (!listedIds.Contains(id))
                {
                    _applied.Remove(id);
                }
            }
        }
    }

    /// <summary>
    /// Says what a plain stream's item shows, for the configuration page (card L4): <see cref="StatusOwn"/>,
    /// <see cref="StatusEmbedded"/>, <see cref="StatusDeferred"/> or <see cref="StatusFailed"/>. Never the URL.
    /// </summary>
    /// <param name="station">A listed plain stream.</param>
    /// <returns>The status.</returns>
    public string Status(StationDefinition station)
    {
        ArgumentNullException.ThrowIfNull(station);
        if (station.Kind != StationKind.Stream || string.IsNullOrWhiteSpace(station.LogoUrl))
        {
            return StatusEmbedded;
        }

        if (!StationUrls.TryParseLogoUrl(station.LogoUrl, out _))
        {
            return StatusFailed;
        }

        var key = KeyOf(station);
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_applied.TryGetValue(station.Id, out var applied) && string.Equals(applied.Key, key, StringComparison.Ordinal) && !applied.Embedded)
            {
                return StatusOwn;
            }

            return _failedUntil.TryGetValue(key, out var until) && now < until ? StatusFailed : StatusDeferred;
        }
    }

    // Expired failures are dropped whenever a new one is recorded, so the map holds at most the keys failing right now.
    private void Prune(DateTimeOffset now)
    {
        foreach (var entry in new List<KeyValuePair<string, DateTimeOffset>>(_failedUntil))
        {
            if (entry.Value <= now)
            {
                _failedUntil.Remove(entry.Key);
            }
        }
    }

    // One Warning per station per streak: the streak is the logo key plus the kind of problem, so a fixed URL that is
    // later broken again (or a refused URL that becomes a failing one) warns again, and a good logo ends the streak.
    private void WarnOnce(string key, string template, StationDefinition station, string? reason)
    {
        if (_warnings.Begin(station.Id, key + "\n" + template))
        {
#pragma warning disable CA2254 // Two fixed templates chosen in this class, not user input.
            _logger.LogWarning(template, JellywaveChannel.StationName(station), reason);
#pragma warning restore CA2254
        }
    }
}
