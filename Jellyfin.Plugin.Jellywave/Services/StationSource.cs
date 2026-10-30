using System;
using Jellyfin.Plugin.Jellywave.Configuration;

namespace Jellyfin.Plugin.Jellywave.Services;

/// <summary>
/// A station that passed its rules and is listed as an item (card L2): the URLs and the format its media source is built
/// from. Made only by <see cref="StationSources.Publish"/>.
/// </summary>
public sealed class StationSource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StationSource"/> class.
    /// </summary>
    /// <param name="station">The station.</param>
    /// <param name="rank">The station's position among the listed items, from 0.</param>
    /// <param name="serverUrl">The URL the server reads (ffmpeg input).</param>
    /// <param name="codec">The normalized audio codec.</param>
    /// <param name="container">The normalized container.</param>
    /// <param name="isHls">Whether the stream is an HLS playlist.</param>
    /// <param name="baseUrl">The parsed base URL of a SUB/WAVE station; null for a plain stream.</param>
    internal StationSource(StationDefinition station, int rank, Uri serverUrl, string codec, string container, bool isHls, Uri? baseUrl)
    {
        Station = station;
        Rank = rank;
        ServerUrl = serverUrl;
        Codec = codec;
        Container = container;
        IsHls = isHls;
        BaseUrl = baseUrl;
        Key = baseUrl is null ? null : StationKey.Of(station.Id, baseUrl);
    }

    /// <summary>Gets the station.</summary>
    public StationDefinition Station { get; }

    /// <summary>Gets the station's position among the listed items, from 0 (the grid order).</summary>
    public int Rank { get; }

    /// <summary>
    /// Gets the URL the Jellyfin server reads: the media source's path, which ffmpeg opens to relay the station (every
    /// station is relayed since card L10), and the address the title sampler reads.
    /// </summary>
    public Uri ServerUrl { get; }

    /// <summary>Gets the audio codec, lowercase (<c>mp3</c> or <c>aac</c>).</summary>
    public string Codec { get; }

    /// <summary>Gets the container, lowercase (<c>mp3</c>, <c>aac</c> or <c>hls</c>).</summary>
    public string Container { get; }

    /// <summary>Gets a value indicating whether the stream is an HLS playlist.</summary>
    public bool IsHls { get; }

    /// <summary>Gets the parsed base URL of a SUB/WAVE station (card 14 rules); null for a plain stream.</summary>
    public Uri? BaseUrl { get; }

    /// <summary>
    /// Gets the key everything learned from a SUB/WAVE station is kept under (card L4); null for a plain stream, which
    /// the poller does not poll.
    /// </summary>
    public StationKey? Key { get; }
}
