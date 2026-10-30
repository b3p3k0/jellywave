using System;
using Jellyfin.Plugin.Jellywave.Api;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Card S-b: every plugin member the station page controller (<see cref="JellywaveWebController"/>, its async state
/// machines and lambdas) and <see cref="StationList"/> may reference, each with the reason it is safe. Checked by
/// <see cref="PluginCalls"/> in <see cref="StationRouteIlTests"/>: an unlisted member fails the test, each listed one is
/// followed through the plugin's code and may reach only the sinks its line names, and an entry nothing uses fails too.
/// The deny rules in <see cref="StationRouteIlTests"/> still apply on top (a listed type's members can still be forbidden
/// there, as <see cref="StationSource"/>'s are outside the four openers).
/// </summary>
internal static class StationRouteAllowList
{
    /// <summary>
    /// Data types: what the framework builds and reads for the routes (bound parameters, answers, parsed station JSON),
    /// and what the scanned code reads through instance auto-property accessors (proven by IL: they touch nothing but
    /// their own backing field) or the parameterless constructor. A hand-written or static member the scanned code calls
    /// still needs a line in <see cref="Members"/>. Each data type is walked whole (every constructor, the static
    /// constructor, every method and accessor) and may reach its own members and only the sinks its line names.
    /// </summary>
    internal static readonly (Type Type, string Why, string[] Reaches)[] DataTypes =
    [
        // What the routes answer with: built here, serialized to the page.
        (typeof(StationsResponse), "the station list route's answer", []),
        (typeof(StationListEntry), "one row of the station list, built by StationList (its own rule limits what goes in)", []),
        (typeof(FeedResponse), "the feed route's answer", []),
        (typeof(LikeOutcome), "the like routes' answer", []),
        (typeof(LyricsResponse), "the lyrics route's answer", []),
        (typeof(RequestOutcome), "the request routes' answer", []),
        (typeof(ScheduleResponse), "the schedule route's answer", []),
        (typeof(ScheduleBlock), "one block of today's schedule", []),
        (typeof(TitleResponse), "the title route's answer: state and title text, no URL (card L6)", []),

        // What the caller sent (bound by MVC; the station id in them is read by the filter, not here).
        (typeof(LikeSubmission), "the like body: the song id", []),
        (typeof(RequestSubmission), "the request body: text and name", []),

        // What a station answered: parsed JSON shapes and replies, station-supplied text and numbers, no configuration.
        (typeof(StationReply<>), "a station call's status code and parsed body", []),
        (typeof(StationCall<>), "card R2a: a station call's outcome through StationCalls: refused here (status and wait), no answer, or the station's answer", []),
        (typeof(StationImage), "an image a station sent: bytes and MIME type", []),
        (typeof(NowPlayingFeed), "the station's now-playing JSON", []),
        (typeof(ContextInfo), "now-playing JSON: show context", []),
        (typeof(ContextShowInfo), "now-playing JSON: the active show", []),
        (typeof(ShowHandoverInfo), "now-playing JSON: the next show handover", []),
        (typeof(NextShowInfo), "now-playing JSON: the next show", []),
        (typeof(ShowInfo), "now-playing JSON: the show on air", []),
        (typeof(DjInfo), "now-playing JSON: the DJ", []),
        (typeof(ListenersInfo), "now-playing JSON: listener count", []),
        (typeof(TrackInfo), "now-playing JSON: the track", []),
        (typeof(StationState), "the station's state JSON: history, upcoming, lyrics flag", []),
        (typeof(QueueTrack), "state JSON: one track in history or upcoming", []),
        (typeof(LyricsFlag), "state JSON: whether lyrics are on", []),
        (typeof(LikeStatus), "like JSON", []),
        (typeof(LyricsPayload), "lyrics JSON", []),
        (typeof(LyricLine), "lyrics JSON: one line", []),
        (typeof(RequestReceipt), "request JSON: the station's receipt", []),
        (typeof(RequestStatus), "request status JSON", []),
        (typeof(RequestTrack), "request status JSON: the track", []),
        (typeof(ScheduleInfo), "schedule JSON", []),
        (typeof(ScheduleShow), "schedule JSON: one show", []),
        (typeof(SchedulePersona), "schedule JSON: one persona", []),

        // The admin WebUi route's answer.
        (typeof(Jellyfin.Plugin.Jellywave.Web.WebUiStatus), "the injection status the admin WebUi route answers with", []),

        // The poller's view of a station, looked up by the filter's station key.
        (typeof(NowPlayingSnapshot), "the poller's snapshot of one station's state", []),
        (typeof(TitleSnapshot), "the title sampler's copy of one plain stream's state, looked up by the filter's plain stream id (card L6)", []),

        // The title route's station (card L6): the plain-stream filter leaves the id alone, never a URL.
        (typeof(PlainStation), "the plain-stream filter's station: its id only", []),

        // A station itself: these accessors are limited further by the deny rules (StationRouteIlTests).
        (typeof(StationSource), "the filter's station; opened only in CallerStation/BaseUrlOf/KeyOf/NameOf, and in StationList only for key, codec and HLS. It holds its definition and builds its key from the definition's id", ["StationDefinition", "StationDefinition.get_Id"]),
        (typeof(StationKey), "a station's id and origin; Origin is read only in StationList.ImageTag", []),
        (typeof(StationDefinition), "read only by StationList, and only its id, kind and bitrate (StationList rule)", []),
    ];

    /// <summary>
    /// Members: the key (<c>Type.Name(ParameterTypes)</c>), why the scanned types may call it, and the sinks it may reach
    /// (configuration, resolver, a station, a service locator, the item bag, reflection) with the reason in the text. A sink
    /// it reaches that is not named here fails the test; a named sink it no longer reaches fails too.
    /// </summary>
    internal static readonly (string Member, string Why, string[] Reaches)[] Members =
    [
        // The station list (StationList is scanned on its own; the controller's calls into it are not walked).
        ("StationList.Entries(Channel)", "the station list route's rows; StationList is scanned by its own rule", []),
        ("StationList.ImageTag(BaseItem, StationKey, NowPlayingSnapshot)", "the feed's image tag for the filter's station key; scanned by the StationList rule", []),
        ("StationResolver.ListedStations()", "StationList's one source of stations; the StationList rule limits what it reads from them", ["StationResolver", "PluginConfiguration", "StationSources", "StationSource", "StationSource.*", "StationDefinition", "StationDefinition.*"]),

        // Station text, read from the definition the caller already holds.
        ("JellywaveChannel.StationName(StationDefinition)", "a station's display name from the definition it is handed (NameOf, StationList); card L11: a blank plain stream's name is its stream URL's host, which its item name in every Jellyfin client already shows", ["StationDefinition.get_Name", "StationDefinition.get_Kind", "StationDefinition.get_StreamUrl"]),
        ("StationText.Genre(StationDefinition)", "a plain stream's genre, cut to the limit, from the definition it is handed", ["StationDefinition.get_Genre"]),
        ("StationText.Notes(StationDefinition)", "a station's notes, cut to the limit, from the definition it is handed", ["StationDefinition.get_Notes"]),
        ("StationText.Website(StationDefinition)", "a plain stream's website through the website rule, from the definition it is handed", ["StationDefinition.get_Website"]),

        // Calls to the filter's station, card R2a: only through StationCalls (the caller's budgets, the shared limiter, the
        // caches), each with the filter's key and base URL from KeyOf/BaseUrlOf; none reaches another station.
        ("StationCalls.GetAvatarAsync(Guid, StationKey, Uri, String, CancellationToken)", "the DJ portrait from the filter's station", []),
        ("StationCalls.GetLikeAsync(Guid, StationKey, Uri, String, CancellationToken)", "like status from the filter's station, kept per the song the poller heard there", []),
        ("StationCalls.PostLikeAsync(Guid, StationKey, Uri, String, CancellationToken)", "a like sent to the filter's station", []),
        ("StationCalls.GetLyricsAsync(Guid, StationKey, Uri, String, CancellationToken)", "lyrics from the filter's station", []),
        ("StationCalls.SubmitRequestAsync(Guid, StationKey, Uri, String, String, Func<Boolean>, CancellationToken)", "a listener request sent to the filter's station; the callback reserves the caller's receipt slot once the budget has room", []),
        ("StationCalls.GetRequestStatusAsync(Guid, StationKey, Uri, String, String, CancellationToken)", "a request's status from the receipt's station, by the station's id behind the caller's token", []),
        ("StationClient.IsValidId(String)", "a pure check on an id's characters and length", []),
        ("ScheduleCache.GetAsync(String, Uri, CancellationToken)", "the schedule of the filter's station, cached by its id and base URL", []),
        ("StationImage.OpenStream()", "a read-only stream over the image bytes", []),

        // State kept per station, looked up by the filter's key or the resolved channel.
        ("NowPlayingState.Snapshot(StationKey)", "the poller's state for that key", []),
        ("StationTitles.Snapshot(String)", "the title sampler's state for the filter's plain stream id; reads the store, never samples (card L6)", []),
        ("RequestReceipts.Reserve(Guid)", "card R2a: a receipt slot for the caller before the request is sent; returns our own random token, holds no station", []),
        ("RequestReceipts.Complete(String, Guid, StationKey, String)", "ties an accepted request's station id to the caller's token and the filter's station key", []),
        ("RequestReceipts.Cancel(String)", "gives back a slot the station did not fill", []),
        ("RequestReceipts.StationRequestIdOf(String, Guid, StationKey)", "the station's request id behind the caller's own token, for the receipt's station key only", []),
        ("LiveItemFinder.FindLiveItem(Channel, String)", "the live item of a station id in the filter's channel", []),
        ("LiveItemArtwork.AppliedOrigin(String)", "which origin's cover a station's item shows (StationList.ImageTag)", []),

        // Fixed values.
        ("RequestOutcome.LostTrack(String)", "the fixed 'lost track' answer", []),
        ("WebUiStartupTask.get_Status()", "the injection status for the admin WebUi route, set at startup from the web client's files", []),
        ("WebUiTransforms.AssetVersion()", "the embedded assets' content hash for the ETag", []),
    ];
}
