using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// Samples the current title of each plain stream someone is playing (card L6) and keeps it in <see cref="StationTitles"/>
/// for the station page's "Now:" line. Every <see cref="SampleInterval"/> it asks Jellyfin's sessions which items play
/// (<see cref="IPlayingItems"/>); nothing playing means no request at all. Only the sessions of users who can see the tile
/// count (card L14 fix round 2, as for the SUB/WAVE stations: <see cref="StationAccess.SessionUserGate(Channel)"/>), so a
/// user blocked from the tile cannot make the server sample a stream by reporting playback of its item. For each listed
/// plain stream whose live item plays, it reads one metadata block from the stream's server URL through
/// <see cref="StationClient.SampleIcyTitleAsync"/> (the no-redirect client, 8 s, the <see cref="IcyMetadata"/> bounds), at
/// most <see cref="MaxConcurrentSamples"/> at a time. An HLS stream is never requested: it carries no ICY titles. With
/// "Show what's playing" (<see cref="PluginConfiguration.NowPlayingEnabled"/>) off nothing is sampled and every station
/// reads <see cref="StationTitles.StateOff"/>. A failing stream warns once per streak, like the now-playing poller. Never
/// the URL in a log line or in the state.
/// </summary>
public sealed class TitleSampler : IHostedService, IDisposable
{
    /// <summary>Most streams sampled at once; each sample is one short connection to its stream.</summary>
    internal const int MaxConcurrentSamples = 4;

    /// <summary>A station's title state is dropped when nobody has played it for this many sample intervals.</summary>
    internal const int IdleIntervalsKept = 3;

    /// <summary>Category of a stream that ended or broke off before its metadata block.</summary>
    private const string BrokeOff = "the stream ended or broke off before its title";

    /// <summary>Wait between samples of a played stream: a title changes every few minutes, the page asks every few seconds.</summary>
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(15);

    /// <summary>Wait between checks of <see cref="IServerApplicationHost.CoreStartupHasCompleted"/> before the first round.</summary>
    private static readonly TimeSpan StartupProbeDelay = TimeSpan.FromSeconds(2);

    /// <summary>How long StopAsync waits for the loop to finish.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly StationClient _client;
    private readonly StationTitles _titles;
    private readonly LiveItemFinder _finder;
    private readonly IPlayingItems _playing;
    private readonly Func<Channel, Func<Guid, bool>> _sessionUserGate;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<TitleSampler> _logger;
    private readonly Func<PluginConfiguration?> _configuration;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _stopping = new();
    private readonly WarningStreaks _failing = new();
    private Task? _loop;

    /// <summary>
    /// Initializes a new instance of the <see cref="TitleSampler"/> class (DI constructor).
    /// </summary>
    /// <param name="client">The station client.</param>
    /// <param name="titles">The title state the route reads.</param>
    /// <param name="finder">The live item finder (which item is which station's).</param>
    /// <param name="playing">The items Jellyfin's sessions play.</param>
    /// <param name="access">The station routes' tile gate, applied to the sessions' users.</param>
    /// <param name="applicationHost">The application host, for startup completion.</param>
    /// <param name="logger">The logger.</param>
    public TitleSampler(StationClient client, StationTitles titles, LiveItemFinder finder, IPlayingItems playing, StationAccess access, IServerApplicationHost applicationHost, ILogger<TitleSampler> logger)
        : this(client, titles, finder, playing, access.SessionUserGate, applicationHost, logger, () => Plugin.Instance?.Configuration, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TitleSampler"/> class with an explicit configuration source and clock
    /// (tests): every session's user can see the tile.
    /// </summary>
    /// <param name="client">The station client.</param>
    /// <param name="titles">The title state the route reads.</param>
    /// <param name="finder">The live item finder.</param>
    /// <param name="playing">The items Jellyfin's sessions play.</param>
    /// <param name="applicationHost">The application host, for startup completion.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">Returns the current configuration, or null when the plugin is not ready.</param>
    /// <param name="clock">The clock.</param>
    internal TitleSampler(StationClient client, StationTitles titles, LiveItemFinder finder, IPlayingItems playing, IServerApplicationHost applicationHost, ILogger<TitleSampler> logger, Func<PluginConfiguration?> configuration, TimeProvider clock)
        : this(client, titles, finder, playing, _ => _ => true, applicationHost, logger, configuration, clock)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TitleSampler"/> class with an explicit tile gate, configuration source
    /// and clock (tests).
    /// </summary>
    /// <param name="client">The station client.</param>
    /// <param name="titles">The title state the route reads.</param>
    /// <param name="finder">The live item finder.</param>
    /// <param name="playing">The items Jellyfin's sessions play.</param>
    /// <param name="sessionUserGate">Returns, for the resolved channel, whether a session's user can see the tile; the check may throw.</param>
    /// <param name="applicationHost">The application host, for startup completion.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration">Returns the current configuration, or null when the plugin is not ready.</param>
    /// <param name="clock">The clock.</param>
    internal TitleSampler(StationClient client, StationTitles titles, LiveItemFinder finder, IPlayingItems playing, Func<Channel, Func<Guid, bool>> sessionUserGate, IServerApplicationHost applicationHost, ILogger<TitleSampler> logger, Func<PluginConfiguration?> configuration, TimeProvider clock)
    {
        _client = client;
        _titles = titles;
        _finder = finder;
        _playing = playing;
        _sessionUserGate = sessionUserGate;
        _applicationHost = applicationHost;
        _logger = logger;
        _configuration = configuration;
        _clock = clock;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = Task.Run(() => LoopAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await Task.WhenAny(_loop, Task.Delay(StopTimeout, cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Host gave up waiting; the loop observes _stopping and ends on its own.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Dispose();
    }

    /// <summary>
    /// Runs one round: drops the state of streams nobody plays, then samples every listed plain stream whose item plays.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SampleOnceAsync(CancellationToken cancellationToken)
    {
        var config = _configuration();
        var now = _clock.GetUtcNow().UtcDateTime;
        var streams = config is null ? [] : StationSources.Publish(config).Where(s => s.Key is null && s.Station.Kind == StationKind.Stream).ToList();
        var listed = streams.Select(s => s.Station.Id).ToHashSet(StringComparer.Ordinal);
        _titles.Expire(listed, now - (SampleInterval * IdleIntervalsKept));
        _titles.KeepReads(ReadSources(streams));
        _failing.Retain(listed);
        if (config is not null)
        {
            // HI 2026-10-28: "Show what's playing" covers plain-stream titles too; off, nothing is sampled at all.
            _titles.SetEnabled(config.NowPlayingEnabled);
        }

        if (streams.Count == 0 || !config!.NowPlayingEnabled)
        {
            return;
        }

        if (_playing.PlayingItemIds().Count == 0)
        {
            return;
        }

        var played = PlayedStreams(config!, streams, cancellationToken);
        foreach (var source in played)
        {
            _titles.Played(source.Station.Id, now);
        }

        var options = new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentSamples, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(played, options, async (source, token) => await SampleAsync(source, token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <summary>
    /// The listed plain streams whose live item a session of a user who can see the tile plays. The items are looked up only
    /// when something plays; a failing lookup (a library lock during a scan, or a user lookup) samples nothing this round.
    /// </summary>
    private List<StationSource> PlayedStreams(PluginConfiguration config, List<StationSource> streams, CancellationToken cancellationToken)
    {
        try
        {
            var resolution = _finder.Resolve(config);
            if (resolution.Collides || resolution.Channel is not Channel channel)
            {
                return [];
            }

            var playing = _playing.PlayingItemIds(_sessionUserGate(channel));
            if (playing.Count == 0)
            {
                return [];
            }

            return streams.Where(s => _finder.FindLiveItem(channel, s.Station.Id) is { } item && playing.Contains(item.Id)).ToList();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Item lookup before the title samples failed; no stream is sampled this round");
            return [];
        }
    }

    /// <summary>
    /// Samples one stream and records the outcome. Throws only on cancellation: whatever else goes wrong is this stream's.
    /// </summary>
    private async Task SampleAsync(StationSource source, CancellationToken cancellationToken)
    {
        var station = source.Station;
        if (source.IsHls)
        {
            // An HLS playlist carries no ICY metadata: nothing is requested.
            _titles.Record(station.Id, StationTitles.StateNoTitles, null, _clock.GetUtcNow().UtcDateTime);
            return;
        }

        // Card L13: set once the stream answered with a success status and its headers, so a read that fails after that
        // (a metadata block slower than the budget, a stream that breaks off) still says the stream is up.
        var answered = false;
        try
        {
            var sample = await _client.SampleIcyTitleAsync(source.ServerUrl, () => answered = true, cancellationToken).ConfigureAwait(false);
            var title = sample.HasMetadata ? StationText.Clean(sample.Title, IcyMetadata.MaxTitleLength) : null;
            var answeredUtc = _clock.GetUtcNow().UtcDateTime;
            _titles.Record(station.Id, title is null ? StationTitles.StateNoTitles : StationTitles.StateTitle, title, answeredUtc);
            _titles.RecordRead(station.Id, source.ServerUrl, TitleReadOutcome.Answered, answeredUtc);
            if (_failing.End(station.Id))
            {
                _logger.LogInformation("Title sampling of station {Name} works again", JellywaveChannel.StationName(station));
            }

            if (!sample.HasMetadata)
            {
                _logger.LogDebug("Station {Name} sends no usable icy-metaint; it shows no title", JellywaveChannel.StationName(station));
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && (StationFailures.IsStationFailure(ex) || ex is IOException or OperationCanceledException))
        {
            var failedUtc = _clock.GetUtcNow().UtcDateTime;
            _titles.Record(station.Id, StationTitles.StateFailing, null, failedUtc);
            _titles.RecordRead(station.Id, source.ServerUrl, ReadOutcome(ex, answered), failedUtc);
            var category = ex is IOException ? BrokeOff : StationFailures.Category(ex);
            if (_failing.Begin(station.Id, category))
            {
                _logger.LogWarning("Title sampling of station {Name} failed: {Reason}; its page shows no title until it answers", JellywaveChannel.StationName(station), category);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A bug must not stop the other streams; this one shows the delayed text until it is fixed.
            var failedUtc = _clock.GetUtcNow().UtcDateTime;
            _titles.Record(station.Id, StationTitles.StateFailing, null, failedUtc);
            _titles.RecordRead(station.Id, source.ServerUrl, TitleReadOutcome.NoAnswer, failedUtc);
            if (_failing.Begin(station.Id, StationFailures.Unexpected))
            {
                _logger.LogError(ex, "Title sampling of station {Name} failed unexpectedly", JellywaveChannel.StationName(station));
            }
        }
    }

    /// <summary>
    /// How a failed read ended (card L13): a redirect is <see cref="TitleReadOutcome.Moved"/> (the relay follows it, the
    /// sampler does not); a failure after a success status and its headers is <see cref="TitleReadOutcome.Answered"/>
    /// (the stream is up, its title was slow or broke off); anything else is <see cref="TitleReadOutcome.NoAnswer"/>.
    /// </summary>
    private static TitleReadOutcome ReadOutcome(Exception ex, bool answered)
        => ex is StationRedirectException ? TitleReadOutcome.Moved
            : answered ? TitleReadOutcome.Answered
            : TitleReadOutcome.NoAnswer;

    /// <summary>The listed plain streams' ids and stream URLs, for <see cref="StationTitles.KeepReads"/>.</summary>
    private static Dictionary<string, Uri> ReadSources(List<StationSource> streams)
    {
        var sources = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var source in streams)
        {
            sources.TryAdd(source.Station.Id, source.ServerUrl);
        }

        return sources;
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!_applicationHost.CoreStartupHasCompleted)
            {
                await Task.Delay(StartupProbeDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogInformation("Title sampler started");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SampleOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must survive anything; the next round may succeed.
                _logger.LogError(ex, "Title sampling round failed unexpectedly");
            }

            try
            {
                await Task.Delay(SampleInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Title sampler stopped");
    }
}
