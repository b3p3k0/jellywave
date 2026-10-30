using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// Admin endpoints used by the plugin configuration page.
/// </summary>
[ApiController]
[Route("Jellywave")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces("application/json")]
public class JellywaveController : ControllerBase
{
    /// <summary>
    /// Status line text when the primary SUB/WAVE station has no base URL, or there is no SUB/WAVE station at all (a list
    /// of plain streams only: they have no now-playing feed). Card L7: it named "the station URL" before.
    /// </summary>
    internal const string NotConfiguredStatus = "no SUB/WAVE station URL configured";

    /// <summary>Status line prefix when the primary SUB/WAVE station's base URL is set but refused; the reason follows.</summary>
    internal const string RefusedStatusPrefix = "the SUB/WAVE station URL is set but not allowed: ";

    /// <summary>Status line prefix when the SUB/WAVE station is not listed for another reason; the fixed reason follows.</summary>
    private const string NotListedStatusPrefix = "the SUB/WAVE station is not listed: ";

    /// <summary>Warning while the plugin runs on defaults because its configuration file could not be loaded.</summary>
    /// <remarks>Whether the unreadable file was kept, and as what, is <see cref="HealthResponse.ConfigurationUnreadableCopy"/>.</remarks>
    internal const string DefaultsWarning = "the configuration file could not be loaded at startup, so Jellywave runs on default settings and writes nothing on its own (see the server log); blocks on the old tile name may not apply until it is fixed, and saving this page writes a new file";

    /// <summary>
    /// Warning while the plugin runs on defaults and could not copy the unreadable configuration file aside (card R1):
    /// every save is refused (<see cref="Plugin.SaveRefusedReason"/>) until the copy works.
    /// </summary>
    internal const string PreservationWarning = "the configuration file could not be loaded at startup and could not be copied aside, so Jellywave runs on default settings and refuses every save until a copy works; check that Jellyfin can read the settings file and write to its folder (see the server log); blocks on the old tile name may not apply until it is fixed";

    /// <summary>
    /// Warning while a fresh install runs on the starter stations because its first configuration could not be saved
    /// (card L9). There was no file and no old tile, so it says nothing about blocks.
    /// </summary>
    internal const string UnsavedWarning = "the new configuration could not be saved at startup, so Jellywave runs on the starter stations in memory; check that the configuration folder is writable (see the server log), and saving this page writes the file again";

    /// <summary>The <c>Error</c> of <c>GET /Jellywave/Viewers</c> when the count threw (card L13).</summary>
    internal const string ViewersFailedError = "the count failed; see the server log";

    private readonly StationClient _stationClient;
    private readonly AccessGranter _accessGranter;
    private readonly NowPlayingState _state;
    private readonly PollerHealth _health;
    private readonly LiveItemArtwork _artwork;
    private readonly StationLogos _logos;
    private readonly LiveItemFinder _finder;
    private readonly StationTitles _titles;
    private readonly StationCheck _check;
    private readonly ILogger<JellywaveController> _logger;
    private readonly Func<PluginConfiguration?> _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellywaveController"/> class.
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="accessGranter">The access granter.</param>
    /// <param name="state">The shared now-playing state.</param>
    /// <param name="health">The poller health snapshot.</param>
    /// <param name="artwork">The SUB/WAVE items' cover keeper.</param>
    /// <param name="logos">The plain streams' logo keeper.</param>
    /// <param name="finder">The live item finder.</param>
    /// <param name="titles">The plain streams' title state, for the last read's outcome (card L13).</param>
    /// <param name="check">"Reload status": every SUB/WAVE station asked once (card L14).</param>
    /// <param name="logger">The logger.</param>
    public JellywaveController(StationClient stationClient, AccessGranter accessGranter, NowPlayingState state, PollerHealth health, LiveItemArtwork artwork, StationLogos logos, LiveItemFinder finder, StationTitles titles, StationCheck check, ILogger<JellywaveController> logger)
        : this(stationClient, accessGranter, state, health, artwork, logos, finder, logger, () => Plugin.Instance?.Configuration, titles, check)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JellywaveController"/> class with an explicit configuration source and
    /// an empty title state (tests).
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="accessGranter">The access granter.</param>
    /// <param name="state">The shared now-playing state.</param>
    /// <param name="health">The poller health snapshot.</param>
    /// <param name="artwork">The SUB/WAVE items' cover keeper.</param>
    /// <param name="logos">The plain streams' logo keeper.</param>
    /// <param name="finder">The live item finder.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">Returns the current configuration, or null when the plugin is not ready.</param>
    internal JellywaveController(StationClient stationClient, AccessGranter accessGranter, NowPlayingState state, PollerHealth health, LiveItemArtwork artwork, StationLogos logos, LiveItemFinder finder, ILogger<JellywaveController> logger, Func<PluginConfiguration?> configuration)
        : this(stationClient, accessGranter, state, health, artwork, logos, finder, logger, configuration, new StationTitles())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JellywaveController"/> class with an explicit configuration source (tests).
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="accessGranter">The access granter.</param>
    /// <param name="state">The shared now-playing state.</param>
    /// <param name="health">The poller health snapshot.</param>
    /// <param name="artwork">The SUB/WAVE items' cover keeper.</param>
    /// <param name="logos">The plain streams' logo keeper.</param>
    /// <param name="finder">The live item finder.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">Returns the current configuration, or null when the plugin is not ready.</param>
    /// <param name="titles">The plain streams' title state.</param>
    internal JellywaveController(StationClient stationClient, AccessGranter accessGranter, NowPlayingState state, PollerHealth health, LiveItemArtwork artwork, StationLogos logos, LiveItemFinder finder, ILogger<JellywaveController> logger, Func<PluginConfiguration?> configuration, StationTitles titles)
        : this(stationClient, accessGranter, state, health, artwork, logos, finder, logger, configuration, titles, new StationCheck(stationClient, health, NullLogger<StationCheck>.Instance))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JellywaveController"/> class with an explicit configuration source and
    /// check (tests).
    /// </summary>
    /// <param name="stationClient">The station client.</param>
    /// <param name="accessGranter">The access granter.</param>
    /// <param name="state">The shared now-playing state.</param>
    /// <param name="health">The poller health snapshot.</param>
    /// <param name="artwork">The SUB/WAVE items' cover keeper.</param>
    /// <param name="logos">The plain streams' logo keeper.</param>
    /// <param name="finder">The live item finder.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">Returns the current configuration, or null when the plugin is not ready.</param>
    /// <param name="titles">The plain streams' title state.</param>
    /// <param name="check">"Reload status": every SUB/WAVE station asked once.</param>
    internal JellywaveController(StationClient stationClient, AccessGranter accessGranter, NowPlayingState state, PollerHealth health, LiveItemArtwork artwork, StationLogos logos, LiveItemFinder finder, ILogger<JellywaveController> logger, Func<PluginConfiguration?> configuration, StationTitles titles, StationCheck check)
    {
        _configuration = configuration;
        _stationClient = stationClient;
        _accessGranter = accessGranter;
        _state = state;
        _health = health;
        _artwork = artwork;
        _logos = logos;
        _finder = finder;
        _titles = titles;
        _check = check;
        _logger = logger;
    }

    /// <summary>
    /// The now-playing poller's health, for the configuration page: for the primary SUB/WAVE station last success, last
    /// error and what its item carries, and every configured station's status (card L4), with a plain stream's last
    /// title read (card L13, from memory: nothing is read for it). No URL, no exception text.
    /// </summary>
    /// <returns>The snapshot.</returns>
    [HttpGet("Health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<HealthResponse> Health()
    {
        var config = _configuration();
        var plugin = Plugin.Instance;
        // Resolved live, so the collision status changes with a rename at once instead of after the next poll.
        var resolution = _finder.Resolve(config);
        var channel = resolution.Channel;
        var primary = config is null ? null : StationSources.Primary(config);
        var health = _health.Snapshot(primary?.Key);
        var candidate = config?.PrimaryCandidate();
        var refusal = StationUrls.BaseUrlRefusal(candidate?.BaseUrl);
        var configured = config is not null && StationUrls.TryGetBaseUrl(config, out _);
        var notListed = configured || refusal is not null || config is null || candidate is null || string.IsNullOrWhiteSpace(candidate.BaseUrl)
            ? null
            : StationSources.NotListedReason(config, candidate);
        return new HealthResponse
        {
            ConfigurationWarning = plugin?.PreservationFailed == true ? PreservationWarning
                : plugin?.RunningOnDefaults == true ? DefaultsWarning
                : plugin?.RunningUnsaved == true ? UnsavedWarning
                : null,
            ConfigurationUnsaved = plugin?.RunningUnsaved == true,
            ConfigurationUnreadableCopy = plugin?.RunningOnDefaults == true ? plugin.UnreadableCopy : null,
            ConfigurationSaveRefusal = plugin?.PreservationFailed == true ? Plugin.SaveRefusedReason : null,
            PollingEnabled = config?.NowPlayingEnabled == true,
            StationConfigured = configured,
            StationUrlRefusal = refusal,
            StationUrlStatus = configured ? null
                : refusal is not null ? RefusedStatusPrefix + refusal
                : notListed is not null ? NotListedStatusPrefix + notListed
                : NotConfiguredStatus,
            ChannelStatus = resolution.Collides ? ChannelResolution.CollisionStatus : null,
            Running = health.Running,
            LastFetchSeconds = health.LastFetchSeconds,
            Reachable = health.Reachable,
            LastSuccessUtc = health.LastSuccessUtc,
            LastErrorUtc = health.LastErrorUtc,
            LastError = health.LastError,
            IdleSinceUtc = health.IdleSinceUtc,
            Description = primary?.Key is { } key ? _state.Snapshot(key).Description : null,
            Cover = primary is null ? null : _artwork.Applied(primary.Station.Id),
            LiveItemExists = channel is not null && _finder.FindLiveItem(channel, primary?.Station.Id) is not null,
            Stations = config is null ? [] : StationsOf(config)
        };
    }

    /// <summary>
    /// The configuration page's "Reload status" (card L14): the poller asks only the SUB/WAVE stations someone listens to,
    /// so this asks every listed SUB/WAVE station for its feed once, on purpose, records each answer in the poll health
    /// (an idle station stays idle), then answers like <see cref="Health"/>. Nothing is asked with "Show what's playing" off,
    /// while another check runs or within <see cref="StationCheck.Cooldown"/> (10 s) of the last one; the answer is then the
    /// health as it stands.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The health after the check.</returns>
    [HttpPost("Health/Check")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<HealthResponse>> CheckHealth(CancellationToken cancellationToken)
    {
        await _check.CheckAsync(_configuration(), cancellationToken).ConfigureAwait(false);
        return Health();
    }

    /// <summary>
    /// Adds the tile once to every user who cannot see it yet (the config page's "Add the tile to every user now"
    /// button). Never removes access; nothing else runs it.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The counts, or <c>Error</c> set when nothing was done.</returns>
    [HttpPost("GrantAccess")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<GrantResult>> GrantAccess(CancellationToken cancellationToken)
    {
        try
        {
            return await _accessGranter.GrantAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Channel access pass failed");
            return new GrantResult { Error = "the pass failed; see the server log" };
        }
    }

    /// <summary>
    /// Card L13: how many users can see the tile (the grant button's rule) and the names of those among them who have
    /// "Allow audio playback that requires transcoding" off, for "Who can see it". Reads only; changes no policy.
    /// Admin-only like every route here: user names are not for other users.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The counts and names, or <c>Error</c> set when nothing was counted.</returns>
    [HttpGet("Viewers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<TileViewers>> Viewers(CancellationToken cancellationToken)
    {
        try
        {
            return await _accessGranter.ViewersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Counting who can see the tile failed");
            return new TileViewers { Error = ViewersFailedError };
        }
    }

    /// <summary>
    /// The rename notice's status: the earlier tile names and how many users still name one of them without the current
    /// channel id. Reads only. On a channel name collision the count is not taken (a carry-over would do nothing).
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("CarryAccess")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<CarryOverStatus> CarryAccessStatus()
    {
        if (_finder.Resolve(_configuration()).Collides)
        {
            return new CarryOverStatus { Error = ChannelResolution.CollisionStatus };
        }

        return _accessGranter.CarryOverStatus();
    }

    /// <summary>
    /// Carries access over from the earlier tile names to the current one, once (the rename notice's "Give them access to the renamed tile"
    /// button): the current channel id is added to every enabled list and every block list that names an earlier tile's
    /// id. Never removes anything; nothing else runs it.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The counts, or <c>Error</c> set when nothing was done.</returns>
    [HttpPost("CarryAccess")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<CarryOverResult>> CarryAccess(CancellationToken cancellationToken)
    {
        try
        {
            return await _accessGranter.CarryOverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Channel access carry-over failed");
            return new CarryOverResult { Error = "the carry-over failed; see the server log" };
        }
    }

    /// <summary>
    /// The configuration page's "Check" button (card L3): checks a stream address from the server and says what it is
    /// (MP3, AAC, HLS, a playlist, a redirect, a SUB/WAVE station) without following a redirect or reading an audio body.
    /// </summary>
    /// <param name="request">The address to check.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result; HTTP 200 whether or not a stream was found, with <c>Ok</c> and the lines telling which.</returns>
    [HttpPost("Probe")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StationProbeResult>> Probe([FromBody, Required] ProbeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await new StationProbe(_stationClient, _logger).ProbeAsync(request.Url, request.BaseUrl, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every configured station's status in grid order: listed or why not, and for a listed SUB/WAVE station its poll health
    /// and cover, for a listed plain stream its logo status and its last title read (card L13). A station refused as a duplicate gets no poll fields, so it
    /// never shows the health of the station that holds its id.
    /// </summary>
    private List<StationHealth> StationsOf(PluginConfiguration config)
    {
        var reasons = new Dictionary<StationDefinition, string>(ReferenceEqualityComparer.Instance);
        var listed = new Dictionary<StationDefinition, StationSource>(ReferenceEqualityComparer.Instance);
        foreach (var source in StationSources.Publish(config, (station, reason) => reasons[station] = reason))
        {
            listed[source.Station] = source;
        }

        var stations = new List<StationHealth>();
        foreach (var station in (config.Stations ?? []).Where(s => s is not null).OrderBy(s => s.SortOrder))
        {
            var entry = new StationHealth
            {
                Id = station.Id ?? string.Empty,
                Name = JellywaveChannel.StationName(station),
                Kind = station.Kind.ToString(),
                Enabled = station.Enabled,
                Listed = listed.ContainsKey(station),
                NotListedReason = !station.Enabled ? StationSources.NotListedDisabled : reasons.GetValueOrDefault(station)
            };

            if (listed.TryGetValue(station, out var source) && source.Key is { } key)
            {
                var health = _health.Snapshot(key);
                entry.Reachable = health.Reachable;
                entry.LastSuccessUtc = health.LastSuccessUtc;
                entry.LastErrorUtc = health.LastErrorUtc;
                entry.LastError = health.LastError;
                entry.IdleSinceUtc = health.IdleSinceUtc;
                entry.Cover = _artwork.Applied(key.StationId);
            }
            else if (source is not null)
            {
                entry.Logo = _logos.Status(station);
                if (_titles.LastRead(source.Station.Id, source.ServerUrl) is { } read)
                {
                    entry.LastReadOutcome = read.Outcome.ToString();
                    entry.LastReadUtc = read.AtUtc;
                }
            }

            stations.Add(entry);
        }

        return stations;
    }
}
