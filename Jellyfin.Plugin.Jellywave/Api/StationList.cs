using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// The station page's view of the listed stations (cards L5, L8): ids, names, kinds, item ids, image tags and the header
/// fields (notes; a plain stream's genre, website and format), never a station or a stream or station URL. The website is
/// the one link, read through <see cref="StationText.Website"/> (the website rule). <see cref="JellywaveWebController"/> takes this instead of <see cref="StationResolver"/>, so the
/// only way the controller can reach a station's URL is the station <see cref="StationRouteAttribute"/> resolved.
/// </summary>
public sealed class StationList
{
    private const string HlsLabel = "HLS";

    private readonly StationResolver _stations;
    private readonly LiveItemFinder _finder;
    private readonly NowPlayingState _state;
    private readonly LiveItemArtwork _artwork;
    private readonly IImageProcessor _imageProcessor;

    /// <summary>
    /// Initializes a new instance of the <see cref="StationList"/> class.
    /// </summary>
    /// <param name="stations">The listed stations.</param>
    /// <param name="finder">The live item finder.</param>
    /// <param name="state">The shared now-playing state (which origin a station's state is under).</param>
    /// <param name="artwork">The SUB/WAVE items' cover keeper, read only (which origin's cover an item shows).</param>
    /// <param name="imageProcessor">The image processor, for image tags.</param>
    public StationList(StationResolver stations, LiveItemFinder finder, NowPlayingState state, LiveItemArtwork artwork, IImageProcessor imageProcessor)
    {
        _stations = stations;
        _finder = finder;
        _state = state;
        _artwork = artwork;
        _imageProcessor = imageProcessor;
    }

    /// <summary>
    /// Lists every listed station in grid order, both kinds (card L8). A SUB/WAVE entry carries its notes; a plain stream's
    /// also its genre, website and format line source (codec label and bitrate).
    /// </summary>
    /// <param name="channel">The channel the access filter resolved.</param>
    /// <returns>One entry per station.</returns>
    public IReadOnlyList<StationListEntry> Entries(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var entries = new List<StationListEntry>();
        foreach (var station in _stations.ListedStations())
        {
            var definition = station.Station;
            var item = _finder.FindLiveItem(channel, definition.Id);
            var entry = new StationListEntry
            {
                Id = definition.Id,
                Name = JellywaveChannel.StationName(definition),
                ItemId = item?.Id,
                Notes = StationText.Notes(definition)
            };

            if (definition.Kind == StationKind.SubWave && station.Key is { } key)
            {
                entry.Kind = StationListEntry.KindSubWave;
                entry.ImageTag = item is null ? null : ImageTag(item, key, _state.Snapshot(key));
            }
            else
            {
                entry.Kind = StationListEntry.KindStream;
                entry.ImageTag = item is null ? null : LogoTag(item);
                entry.Genre = StationText.Genre(definition);
                entry.Website = StationText.Website(definition);
                entry.Codec = station.IsHls ? HlsLabel : station.Codec.ToUpperInvariant();
                entry.BitrateKbps = definition.BitrateKbps > 0 ? definition.BitrateKbps : null;
            }

            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// The item's Primary image tag, withheld while the item may still show another origin's cover: after a base URL
    /// change the item carries the previous origin's cover until the poll's write phase replaces it, which can be the whole
    /// fetch phase after the new origin's feed was heard (card L4). No tag while either says so. The logo has no origin
    /// and never counts.
    /// </summary>
    /// <param name="item">The station's live item.</param>
    /// <param name="key">The station's key (id and current origin).</param>
    /// <param name="heard">The station's now-playing snapshot under that key.</param>
    /// <returns>The tag, or null.</returns>
    public string? ImageTag(BaseItem item, StationKey key, NowPlayingSnapshot heard)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(heard);
        var otherCover = _artwork.AppliedOrigin(key.StationId) is { } applied && !string.Equals(applied, key.Origin, StringComparison.Ordinal);
        var image = heard.OtherOrigin || otherCover ? null : item.GetImageInfo(ImageType.Primary, 0);
        return image is null ? null : _imageProcessor.GetImageCacheTag(item, image);
    }

    /// <summary>
    /// A plain stream item's Primary image tag (its logo, or the embedded one): a logo has no origin, so nothing is withheld.
    /// </summary>
    private string? LogoTag(BaseItem item)
    {
        var image = item.GetImageInfo(ImageType.Primary, 0);
        return image is null ? null : _imageProcessor.GetImageCacheTag(item, image);
    }
}
