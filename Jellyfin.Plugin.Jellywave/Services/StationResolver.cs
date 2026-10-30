using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Jellywave.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// The one place a station route finds its station (card L5): a station id from the caller, or a request receipt, is
/// resolved only to a SUB/WAVE station the tile lists (<see cref="StationSources.Publish"/>). An unknown, disabled,
/// refused or plain-stream id resolves to nothing, so nothing is sent to any station. An absent id means the primary
/// station (<see cref="StationSources.Primary"/>): an older cached <c>ui.js</c> sends none. Read by
/// <c>StationRouteAttribute</c>, which runs after <see cref="StationAccess"/> allowed the caller.
/// </summary>
public sealed class StationResolver
{
    private readonly RequestReceipts _receipts;
    private readonly Func<PluginConfiguration?> _configuration;
    private readonly ILogger<StationResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StationResolver"/> class (DI constructor).
    /// </summary>
    /// <param name="receipts">Which user sent which listener request to which station.</param>
    /// <param name="logger">The logger.</param>
    public StationResolver(RequestReceipts receipts, ILogger<StationResolver> logger)
        : this(receipts, () => Plugin.Instance?.Configuration, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StationResolver"/> class with an explicit configuration source (tests).
    /// </summary>
    /// <param name="receipts">Which user sent which listener request to which station.</param>
    /// <param name="configuration">Returns the current configuration, or null when the plugin is not ready.</param>
    /// <param name="logger">The logger.</param>
    internal StationResolver(RequestReceipts receipts, Func<PluginConfiguration?> configuration, ILogger<StationResolver> logger)
    {
        _receipts = receipts;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Gets the SUB/WAVE stations the tile lists, in grid order. Plain streams are not included.
    /// </summary>
    /// <returns>The listed SUB/WAVE stations; empty when the plugin is not ready.</returns>
    public IReadOnlyList<StationSource> SubWaveStations()
    {
        var config = _configuration();
        return config is null ? [] : Listed(config);
    }

    /// <summary>
    /// Gets every station the tile lists, both kinds, in grid order (card L8: the station page's list, which draws a header
    /// for plain streams too). Never used to resolve a station route: those take <see cref="Resolve"/>, which knows SUB/WAVE
    /// stations only.
    /// </summary>
    /// <returns>The listed stations; empty when the plugin is not ready.</returns>
    public IReadOnlyList<StationSource> ListedStations()
    {
        var config = _configuration();
        return config is null ? [] : StationSources.Publish(config);
    }

    /// <summary>
    /// Resolves a caller's station id. Compared exactly (ordinal): the page sends the id the station list gave it.
    /// </summary>
    /// <param name="stationId">The id from the query or the body; null when the caller sent none (the primary station).</param>
    /// <returns>The listed SUB/WAVE station, or null (the route answers 404 and asks no station).</returns>
    public StationSource? Resolve(string? stationId)
    {
        var config = _configuration();
        if (config is null)
        {
            _logger.LogDebug("Station route refused: the plugin is not ready");
            return null;
        }

        var station = stationId is null
            ? StationSources.Primary(config) is { Key: not null } primary && primary.Station.Kind == StationKind.SubWave ? primary : null
            : Listed(config).FirstOrDefault(s => string.Equals(s.Station.Id, stationId, StringComparison.Ordinal));
        if (station is null)
        {
            // The id is the caller's text: it is not logged.
            _logger.LogDebug("Station route refused: {What}", stationId is null ? "no primary SUB/WAVE station is listed" : "no listed SUB/WAVE station has that id");
        }

        return station;
    }

    /// <summary>
    /// Resolves a caller's station id for the title route (card L6) to a listed plain stream. Compared exactly (ordinal), like
    /// <see cref="Resolve"/>. There is no primary plain stream: an absent id resolves to nothing.
    /// </summary>
    /// <param name="stationId">The id from the query.</param>
    /// <returns>The listed plain stream, or null (SUB/WAVE, disabled, refused or unknown: the route answers 404).</returns>
    public StationSource? ResolvePlainStream(string? stationId)
    {
        var config = _configuration();
        if (config is null || stationId is null)
        {
            _logger.LogDebug("Title route refused: {What}", config is null ? "the plugin is not ready" : "no station id");
            return null;
        }

        var station = StationSources.Publish(config)
            .FirstOrDefault(s => s.Key is null && s.Station.Kind == StationKind.Stream && string.Equals(s.Station.Id, stationId, StringComparison.Ordinal));
        if (station is null)
        {
            // The id is the caller's text: it is not logged.
            _logger.LogDebug("Title route refused: no listed plain stream has that id");
        }

        return station;
    }

    /// <summary>
    /// Resolves the station of a listener request from its receipt, never from the caller. A receipt whose station is
    /// no longer listed, or now has another origin, counts as unknown (lost track), so no station is asked.
    /// </summary>
    /// <param name="requestId">The request id from the route.</param>
    /// <param name="userId">The caller's user id.</param>
    /// <param name="station">The receipt's station; set only for <see cref="ReceiptOwnership.Owner"/>.</param>
    /// <returns>The caller's standing with the request.</returns>
    public ReceiptOwnership ResolveReceipt(string? requestId, Guid userId, out StationSource? station)
    {
        station = null;
        switch (_receipts.Check(requestId ?? string.Empty, userId, out var key))
        {
            case ReceiptOwnership.OtherUser:
                _logger.LogDebug("Request status refused: request {RequestId} belongs to another user", requestId);
                return ReceiptOwnership.OtherUser;
            case ReceiptOwnership.Owner when key is not null:
                station = SubWaveStations().FirstOrDefault(s => s.Key == key);
                if (station is null)
                {
                    _logger.LogDebug("Request status: the station of request {RequestId} is no longer listed", requestId);
                    return ReceiptOwnership.Unknown;
                }

                return ReceiptOwnership.Owner;
            default:
                return ReceiptOwnership.Unknown;
        }
    }

    private static List<StationSource> Listed(PluginConfiguration config)
        => StationSources.Publish(config).Where(s => s.Station.Kind == StationKind.SubWave && s.Key is not null).ToList();
}
