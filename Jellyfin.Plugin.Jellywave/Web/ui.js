/* Jellywave station page: injected into jellyfin-web by the File Transformation plugin.
   Registered as a jellyfin-web client plugin (window.JellywaveUi, listed in config.json) so the
   plugin manager hands it the playback manager. Everything talks to the Jellyfin server only.
   What it does: (1) while a listed station's item is the current item (playing or paused), turns the tile's own
   library page into that station's page by inserting a header for it above the item grid; the header follows
   playback (another station: its header; stop or anything else: no header) and has no picker (card L5b,
   HI 2026-10-28). A SUB/WAVE station's header is live (feed, likes, lyrics, schedule, requests); a plain stream's
   is drawn from the station list: logo, name, genre, notes, format and website (card L8), and asks only for the
   "Now:" line, the title the server last sampled from the stream (card L6, GET Jellywave/Title);
   (2) while a SUB/WAVE station's item plays, repaints the playing bar's thumbnail and text, that item's card in any
   visible grid, and the browser media session from that station's feed; while a plain stream plays under its header,
   the bar's text and the media session from its sampled title;
   (3) card L11 (HI 2026-10-29): Stop instead of Pause for stations. A live stream has nothing to resume, so while a
   listed station's item is the current item the playing bar shows Stop and no play/pause, and a pause of that item
   from anywhere becomes a stop. Every other item keeps Jellyfin's pause. */
(function () {
    'use strict';

    var POLL_MS = 5000;
    var FEED_RETRY_MS = 15000;
    /* 403: this user may not see the tile (blocked, not enabled) or is an API key. Ask rarely. */
    var FEED_FORBIDDEN_RETRY_MS = 300000;
    /* The station list is read again at most this often while the page shows or a station plays. */
    var STATIONS_REFRESH_MS = 60000;
    var HTTP_FORBIDDEN = 403;
    /* 404: no channel entity yet, its name collides, or the station is no longer listed. Keeps the short retry. */
    var HTTP_NOT_FOUND = 404;
    /* Card R2b: this server refuses a station call before it asks the station with 429 (over the user's budget) or 503
       (too many calls in flight, or the request receipts are full), each with Retry-After in whole seconds: nothing reached
       the station. The page waits that long (RETRY_AFTER_DEFAULT_S when the header is missing or unreadable, clamped to
       RETRY_AFTER_MIN_S..RETRY_AFTER_MAX_S, the longest the server sends being the hour of the request budget) and asks
       again by itself only for what it polls (like status, lyrics, portrait, request status); a song request is never
       sent again by itself. A wait of RETRY_MINUTES_FROM_S or more is said in minutes. Fix round: a poll refused again in a
       row waits at least twice its previous wait, up to RETRY_BACKOFF_MAX_S (a busy 503 says 2 s, which would otherwise
       be every 2 s for as long as a station holds its slots); an answer, another song or portrait, or a reset starts
       afresh. Retry-After itself is never shortened. */
    var HTTP_TOO_MANY = 429;
    var HTTP_BUSY = 503;
    var RETRY_AFTER_DEFAULT_S = 10;
    var RETRY_AFTER_MIN_S = 1;
    var RETRY_AFTER_MAX_S = 3600;
    var RETRY_MINUTES_FROM_S = 120;
    var RETRY_BACKOFF_MAX_S = 60;
    /* A request's status is asked every REQUEST_POLL_MS, at most REQUEST_POLL_TRIES more times. */
    var REQUEST_POLL_MS = 3000;
    var REQUEST_POLL_TRIES = 30;
    /* A song request without a clear outcome: no answer at all (a network failure), or an error that is not this server's
       refusal (another 5xx: the server or a proxy answered, but the station may have had it). */
    var REQUEST_NO_ANSWER_MESSAGE = 'Could not reach the server. The request may have been sent; it is not sent again by itself.';
    var REQUEST_UNCLEAR_MESSAGE = 'No clear answer from the server. The request may have reached the station; it is not sent again by itself.';
    /* DOM changes are handled at most once per window, through one pending timer. */
    var DOM_DEBOUNCE_MS = 250;
    var PENDING_LYRICS_RETRY_MS = 8000;
    var SCHEDULE_REFRESH_MS = 300000;
    /* Card L14: the server asks a SUB/WAVE station only while someone listens or has its page open, so an idle station's
       first feed has nothing heard yet; that request wakes the server, which asks the station within a second or two.
       Until the feed says the server heard the station, it is asked again after FEED_WAKE_MS, at most FEED_WAKE_TRIES
       times per station, so the live track shows before the next tick. */
    var FEED_WAKE_MS = 2000;
    var FEED_WAKE_TRIES = 3;
    var NO_ACCESS_MESSAGE = 'You don\'t have access to this station.';
    /* The station list's kinds (card L8). Only a station the server calls SUB/WAVE gets the live header and the
       SUB/WAVE routes; any other kind (a plain stream, or a kind this script does not know) gets the plain header,
       which asks the server nothing, so an unknown kind never sends a station route a guessed station. */
    var KIND_SUBWAVE = 'SubWave';
    var KIND_STREAM = 'Stream';
    /* A website link is used only when it is this shape: http or https, an ASCII host (the server serves an
       international host as punycode, xn--…) or an IPv6 literal, an optional port, then printable ASCII only (no
       whitespace, control character or userinfo), at most WEBSITE_MAX characters. The server applies the same rule;
       this is the page's own check before the value becomes an href. Group 1 is the host (and port) for the link text. */
    var WEBSITE_RULE = /^https?:\/\/((?:[a-z0-9.-]+|\[[0-9a-f:.]+\])(?::[0-9]+)?)(?:[\/?#][\u0021-\u007e]*)?$/i;
    var WEBSITE_MAX = 2048;
    /* Card L6: a plain stream's "Now:" line. Until the server has a title it reads NOW_QUERYING, for TITLE_WAIT_MS from
       playback start; then NOW_DELAYED, as it does while the server's samples fail. A stream that sends no titles reads
       NOW_NOT_SUPPLIED at once (an HLS stream too, without asking). The title is asked when the header is built and then
       on every tick (POLL_MS; the server samples every 15 s), one request at a time, only for a station the list calls a
       plain stream, and only while its header exists or, once the header has left the document, while this page keeps
       the bar painted with its title (RA R2: the bar would otherwise freeze on an old title). */
    var TITLE_WAIT_MS = 30000;
    var NOW_LABEL = 'Now:';
    var NOW_QUERYING = 'Querying upstream\u2026';
    var NOW_DELAYED = 'Delayed response: is the stream up?';
    var NOW_NOT_SUPPLIED = 'Not supplied by the station';
    /* The title route's states (TitleResponse.State). */
    var TITLE_STATE_TITLE = 'Title';
    var TITLE_STATE_NONE = 'NoTitles';
    var TITLE_STATE_FAILING = 'Failing';
    /* The server's "Show what's playing" is off (HI 2026-10-28): no samples; the page hides the line. */
    var TITLE_STATE_OFF = 'Off';
    /* The list's format label of an HLS stream, which never carries ICY titles: its line reads NOW_NOT_SUPPLIED at once
       and its title is not asked. */
    var HLS_LABEL = 'HLS';
    /* ICY carries one string, usually "Artist - Title": the playing bar splits it at the first separator. */
    var TITLE_SEPARATOR = ' - ';
    /* Card L11, Stop instead of Pause. A pause of the current station item becomes a stop once it has settled for
       PAUSE_SETTLE_MS and the same station is still the current item of the same player, still paused; a pause that is
       part of a switch to another item (jellyfin-web 12.3 stops the old item first) never stops anything. */
    var PAUSE_SETTLE_MS = 500;
    /* On document.body while a listed station's item is the current item (playing or, for the settle, paused). */
    var STATION_BODY_CLASS = 'jellywave-station-current';
    /* Our own Stop in the bar's right-hand group, for when jellyfin-web hides the centre group (see BAR_CSS). */
    var BAR_STOP_CLASS = 'jellywave-barstop';
    /* jellyfin-web 12.3's playing bar (14245.*.chunk.js and .css): the centre group holds previous, play/pause, Stop
       (wired to playbackManager.stop) and next; the right group a second play/pause. Its CSS hides the centre Stop at
       widths up to 80em and the whole centre group up to 56em, where the right play/pause is the bar's only control
       (layout-mobile: phones and the Android app). On a browser it calls mobile the bar also sets `hide` on the centre
       group at every width (the Android app on a tablet, or a phone in landscape, wider than 56em). While a station is
       current: every play/pause goes, jellyfin-web's own Stop shows wherever its centre group does, and our Stop shows
       in the right group wherever the centre group does not: below 56em, or when the group has `hide`. */
    var BAR_CSS =
        '.nowPlayingBar .' + BAR_STOP_CLASS + '{display:none!important}' +
        'body.' + STATION_BODY_CLASS + ' .nowPlayingBar .playPauseButton{display:none!important}' +
        'body.' + STATION_BODY_CLASS + ' .nowPlayingBar .nowPlayingBarCenter .stopButton{display:inline-flex!important}' +
        'body.' + STATION_BODY_CLASS + ' .nowPlayingBar .nowPlayingBarCenter.hide~.nowPlayingBarRight .' + BAR_STOP_CLASS +
        '{display:inline-flex!important}' +
        '@media (max-width:56em){body.' + STATION_BODY_CLASS + ' .nowPlayingBar .nowPlayingBarRight .' + BAR_STOP_CLASS +
        '{display:inline-flex!important}}';

    /* plainId: the plain stream whose title this page painted into the bar during this playback (card L6). */
    function freshPlaying() { return { stationId: null, feed: null, barTag: null, cardTag: null, promise: null, promiseFor: null, plainId: null }; }

    /* sessionKey: serverId|userId the state belongs to; sessionEpoch: bumped when it changes.
       selected: the station the header shows, which is the station playing now (null: none, so no header);
       selectedPlain: whether that station is a plain stream (its header asks the server nothing but the list);
       the station state belongs to sessionKey|selected (stationKey) and epoch is bumped on every reset of it (another
       session or another station), so a reply to a request sent before the reset is dropped. playing: what the
       playing bar is repainted from (the station playing now). Card R2b: likeNotice is the heart's "try again" text after
       a refused like ({ songId, text }), shown on its title and in the line under the header's actions; lyricsWaitUntil is
       when refused lyrics may be asked again; retries holds the timers of every retry after a refusal and of the request
       status poll, all cleared on a reset; backoff holds, per polled kind, the last wait after a refusal and what it was
       for ({ key, seconds }). */
    var state = {
        deps: null, timer: null, domTimer: null, booted: false,
        sessionKey: null, sessionEpoch: 0, stationKey: null, epoch: 0,
        stations: null, channelId: null, channelName: '', stationsAt: 0, stationsFailedAt: 0, stationsRetryMs: FEED_RETRY_MS, stationsPromise: null, relistedAt: 0, retryTimer: null,
        selected: null, selectedPlain: false, header: null,
        feed: null, feedFailedAt: 0, feedRetryMs: FEED_RETRY_MS, feedPromise: null, feedUnheard: false, wakeTries: 0, wakeTimer: null,
        like: null, likeSongId: null, likeBusy: false, likeNotice: null, lyrics: null, lyricsSongId: null, lyricsWaitUntil: 0,
        schedule: null, scheduleAt: 0, scheduleBusy: false, retries: [], backoff: {},
        title: null, titlePromise: null, titleSince: 0, titleTimer: null,
        playing: freshPlaying(),
        boundPlayer: null
    };
    /* Live collections, so the DOM-change check is a length read instead of a document query. */
    var listContainers = document.getElementsByClassName('itemsContainer');
    var drawerSlots = document.getElementsByClassName('customMenuOptions');

    function api() { return window.ApiClient || null; }
    /* ApiClient rejects with the fetch Response on an HTTP error; a network failure has no status. */
    function isForbidden(err) { return !!err && err.status === HTTP_FORBIDDEN; }
    function isNotFound(err) { return !!err && err.status === HTTP_NOT_FOUND; }
    function isRefusal(err) { return !!err && (err.status === HTTP_TOO_MANY || err.status === HTTP_BUSY); }

    /* Card R2b: the seconds a refusal asks the page to wait, from the Response's Retry-After (delta seconds, as this
       server sends; an HTTP date, as a proxy may), clamped; RETRY_AFTER_DEFAULT_S when it is missing or unreadable. */
    function retryAfterSeconds(err) {
        var raw = null;
        try { raw = err && err.headers && typeof err.headers.get === 'function' ? err.headers.get('Retry-After') : null; } catch (e) { raw = null; }
        var text = raw === null || raw === undefined ? '' : String(raw).trim();
        var seconds = NaN;
        if (/^\d+$/.test(text)) { seconds = parseInt(text, 10); }
        else if (/[a-z]/i.test(text)) { var at = Date.parse(text); if (!isNaN(at)) { seconds = Math.ceil((at - Date.now()) / 1000); } }
        if (!isFinite(seconds)) { return RETRY_AFTER_DEFAULT_S; }
        return Math.min(RETRY_AFTER_MAX_S, Math.max(RETRY_AFTER_MIN_S, seconds));
    }

    function waitText(seconds) {
        return seconds >= RETRY_MINUTES_FROM_S ? Math.ceil(seconds / 60) + ' min' : seconds + ' s';
    }

    /* What a refusal says: why, then when (next: "Try again in" where the user acted, "Asking again in" or "Checking again in"
       where the page asks by itself). */
    function refusedText(err, seconds, next) {
        return (err.status === HTTP_TOO_MANY ? 'Too many requests.' : 'The server is busy.') + ' ' + (next || 'Try again in') + ' ' + waitText(seconds) + '.';
    }

    /* Fix round: the wait before a poll of kind (like, lyrics, portrait, request) refused again, for the same thing (key: the
       song, the portrait URL, the request): Retry-After, but at least twice the previous wait, that doubling capped at
       RETRY_BACKOFF_MAX_S. calmed(kind) forgets it after an answer. */
    function backoffSeconds(kind, key, err) {
        var last = state.backoff[kind];
        var previous = last && last.key === key ? last.seconds : 0;
        var seconds = Math.max(retryAfterSeconds(err), Math.min(previous * 2, RETRY_BACKOFF_MAX_S));
        state.backoff[kind] = { key: key, seconds: seconds };
        return seconds;
    }

    function calmed(kind) { state.backoff[kind] = null; }

    /* Card R2b: runs fn after ms for the session and station state it was armed in (epoch), never for another: a reset
       clears every such timer, and the session is checked again when it fires (a sign-out or another user between ticks). */
    function later(epoch, fn, ms) {
        var id = setTimeout(function () {
            state.retries = state.retries.filter(function (t) { return t !== id; });
            if (!checkSession() || !isCurrent(epoch)) { return; }
            try { fn(); } catch (e) { console.warn('[Jellywave] retry failed', e); }
        }, ms);
        state.retries.push(id);
        return id;
    }

    function norm(id) { return id ? String(id).replace(/-/g, '').toLowerCase() : ''; }
    function isCurrent(epoch) { return epoch === state.epoch; }

    /* Who the state belongs to: server id plus user id, or null when nobody is signed in (no access token). */
    function sessionKey() {
        var client = api();
        if (!client || !client.accessToken || !client.accessToken()) { return null; }
        var server = client.serverId ? client.serverId() : '';
        var user = client.getCurrentUserId ? client.getCurrentUserId() : '';
        return (server || '') + '|' + norm(user);
    }

    /* A sign-out, another user or another server: drop everything the previous session saw, the station list
       included. Returns whether someone is signed in. */
    function checkSession() {
        var key = sessionKey();
        if (key === state.sessionKey) { return key !== null; }
        state.sessionKey = key;
        state.sessionEpoch += 1;
        state.stations = null;   // with the channel id below: deliberate defence in depth (no header without both)
        state.channelId = null;
        state.channelName = '';
        state.stationsAt = 0;
        state.stationsFailedAt = 0;
        state.stationsRetryMs = FEED_RETRY_MS;
        state.stationsPromise = null;
        state.relistedAt = 0;
        state.playing = freshPlaying();
        forgetPage();
        return key !== null;
    }

    /* Another station (or session): the feed, like, lyrics and schedule state go, and replies to requests sent
       for the previous station are dropped (epoch). */
    function resetStation() {
        state.epoch += 1;
        state.stationKey = state.sessionKey !== null && state.selected ? state.sessionKey + '|' + state.selected : null;
        state.feed = null;
        state.feedFailedAt = 0;
        state.feedRetryMs = FEED_RETRY_MS;
        state.feedPromise = null;
        state.like = null;
        state.likeSongId = null;
        state.likeBusy = false;
        state.likeNotice = null;
        state.lyrics = null;
        state.lyricsSongId = null;
        state.lyricsWaitUntil = 0;
        // Card R2b: a retry armed for the previous station or session never runs.
        state.retries.forEach(function (id) { clearTimeout(id); });
        state.retries = [];
        state.backoff = {};
        state.schedule = null;
        state.scheduleAt = 0;
        state.scheduleBusy = false;
        state.feedUnheard = false;
        state.wakeTries = 0;
        clearTimeout(state.wakeTimer);
        state.wakeTimer = null;
        // Card L6: the plain stream's title starts afresh; its 30 s wait counts from now, the station's playback start.
        state.title = null;
        state.titlePromise = null;
        state.titleSince = Date.now();
        clearTimeout(state.titleTimer);
        state.titleTimer = null;
    }

    function setStation(id, plain) {
        if (id === state.selected && plain === state.selectedPlain) { return; }
        state.selected = id;
        state.selectedPlain = plain;
        resetStation();
    }

    function isPlain(station) { return !!station && station.Kind !== KIND_SUBWAVE; }

    function stationById(id) {
        var list = state.stations || [];
        for (var i = 0; i < list.length; i++) { if (id && list[i].Id === id) { return list[i]; } }
        return null;
    }

    /* The item playing now, normalized, or ''. currentItem(player) throws when no player is active;
       getCurrentPlayer() returns null then. After a stop the player stays current and its stream info can
       linger; isPlaying() is the truth. A paused item counts as playing: in jellyfin-web 12.3 isPlaying(player)
       is the player's own isPlaying() when it has one (Chromecast: an item is loaded) and otherwise
       currentSrc() != null (the HTML audio and video players), which a pause keeps and only a stop or the
       end clears. */
    function playingItemId() {
        var pm = state.deps && state.deps.playbackManager;
        if (!pm) { return ''; }
        try {
            var player = pm.getCurrentPlayer ? pm.getCurrentPlayer() : null;
            if (!player) { return ''; }
            if (pm.isPlaying && !pm.isPlaying(player)) { return ''; }
            var item = pm.currentItem(player);
            return item && item.Id ? norm(item.Id) : '';
        } catch (e) {
            return '';
        }
    }

    /* The listed station whose item is playing now, either kind, or null (nothing, another library, an unlisted item). */
    function playingStation() {
        var id = playingItemId();
        var list = state.stations || [];
        if (!id) { return null; }
        for (var i = 0; i < list.length; i++) {
            if (list[i].ItemId && norm(list[i].ItemId) === id) { return list[i]; }
        }
        return null;
    }

    /* The listed SUB/WAVE station playing now, or null: the one whose feed repaints the bar. */
    function playingSubWave() {
        var station = playingStation();
        return station && !isPlain(station) ? station : null;
    }

    function isPlaying(stationId) {
        var station = playingSubWave();
        return !!station && station.Id === stationId;
    }

    /* The header follows playback: its station is the station playing now, or none. A change (another station, or the
       same id now of another kind) resets the station state (the epoch drops replies still on their way for the
       previous station) and removes the old header. */
    function followPlayback() {
        var station = playingStation();
        var id = station ? station.Id : null;
        var plain = isPlain(station);
        markBar(station);
        if (id === state.selected && plain === state.selectedPlain) { return; }
        setStation(id, plain);
        removeHeaders();
    }

    function detach(el) {
        if (el && el.parentNode) { el.parentNode.removeChild(el); }
    }

    /* The portrait is a blob URL; revoke it when its header goes. */
    function removeHeader(header) {
        var avatar = header.querySelector('.jellywave-dj-avatar');
        if (avatar && avatar.src && avatar.src.indexOf('blob:') === 0) { URL.revokeObjectURL(avatar.src); avatar.removeAttribute('src'); }
        detach(header);
    }

    /* Every header goes, including one in a cached view that state.header no longer holds. */
    function removeHeaders() {
        if (state.header) { removeHeader(state.header); }
        state.header = null;
        var headers = document.querySelectorAll('.jellywave-header');
        for (var i = 0; i < headers.length; i++) { removeHeader(headers[i]); }
    }

    /* Refused by the server (403, 404), no SUB/WAVE station listed, or a new session: drop what an earlier answer
       showed, so a blocked user sees nothing. */
    function forgetPage() {
        state.selected = null;
        state.selectedPlain = false;
        resetStation();
        removeHeaders();
        markBar(playingStation());
        var links = document.querySelectorAll('.jellywave-link');
        for (var j = 0; j < links.length; j++) { detach(links[j]); }
    }

    /* A 403 or 404 for the list (or a 403 for a feed): the header goes, and the list is not asked again before the back-off.
       One pending retry then runs a page pass when the back-off ends, so an idle page (no DOM change, nothing playing)
       comes back once allowed; a page refused again retries once per back-off, a bounded rate. */
    function refused(err) {
        state.stations = null;
        state.channelId = null;
        state.stationsFailedAt = Date.now();
        state.stationsRetryMs = isForbidden(err) ? FEED_FORBIDDEN_RETRY_MS : FEED_RETRY_MS;
        forgetPage();
        clearTimeout(state.retryTimer);
        state.retryTimer = setTimeout(function () {
            state.retryTimer = null;
            try { onDomChange(document); } catch (e) { console.warn('[Jellywave] page check failed', e); }
        }, state.stationsRetryMs);
    }

    /* A 404 for the header's feed: its station most likely left the list (disabled, removed, refused). The header goes
       and the list is read again at once, skipping the back-off, so the header comes back at once if the station is
       still listed and still plays, and stays away if it left; only the list's own 403 or 404 refuses the page. A
       second feed 404 within FEED_RETRY_MS (the list still names a station whose feed is gone) is a refusal, so the
       two never loop. */
    function stationGone(err) {
        if (state.relistedAt && Date.now() - state.relistedAt < FEED_RETRY_MS) { refused(err); return; }
        state.relistedAt = Date.now();
        forgetPage();
        state.stations = null;
        state.stationsFailedAt = 0;
        fetchStations(true);
    }

    /* GET Jellywave/Stations: the channel and its listed stations (ids, names, kinds, item ids, header fields; never a
       stream URL; a plain stream's website is checked again before it becomes a link). */
    function fetchStations(force) {
        var client = api();
        if (!client || !checkSession()) { return Promise.resolve(null); }
        if (state.stationsPromise) { return state.stationsPromise; }
        if (state.stationsFailedAt && Date.now() - state.stationsFailedAt < state.stationsRetryMs) { return Promise.resolve(null); }
        if (!force && state.stations && Date.now() - state.stationsAt < STATIONS_REFRESH_MS) { return Promise.resolve(state.stations); }
        var sessionEpoch = state.sessionEpoch;
        var request = client.getJSON(client.getUrl('Jellywave/Stations')).then(function (r) {
            if (sessionEpoch !== state.sessionEpoch) { return null; }
            state.stationsPromise = null;
            state.stationsFailedAt = 0;
            state.stationsAt = Date.now();
            applyStations(r);
            return state.stations;
        }, function (err) {
            if (sessionEpoch !== state.sessionEpoch) { return null; }
            state.stationsPromise = null;
            if (isForbidden(err) || isNotFound(err)) { refused(err); } else { state.stationsFailedAt = Date.now(); state.stationsRetryMs = FEED_RETRY_MS; }
            return null;
        });
        state.stationsPromise = request;
        return request;
    }

    function applyStations(r) {
        var list = r && r.Stations && r.Stations.length ? r.Stations : [];
        state.channelId = r && r.ChannelId ? r.ChannelId : null;
        state.channelName = r && r.ChannelName ? String(r.ChannelName) : '';
        state.stations = list;
        if (!list.length || !state.channelId) { forgetPage(); return; }   // nothing listed: no header, no drawer link
        // The playing station left the list: ensureHeader's followPlayback removes its header.
        ensureHeader(document);
        ensureDrawerLink();
    }

    /* GET Jellywave/Feed for the header's station. A reply for another station or session is dropped. */
    function fetchFeed() {
        var client = api();
        if (!client || !checkSession() || !state.selected || state.selectedPlain) { return Promise.resolve(null); }
        if (state.feedPromise) { return state.feedPromise; }
        if (state.feedFailedAt && Date.now() - state.feedFailedAt < state.feedRetryMs) { return Promise.resolve(null); }
        var epoch = state.epoch;
        var stationId = state.selected;
        var request = client.getJSON(client.getUrl('Jellywave/Feed', { stationId: stationId })).then(function (feed) {
            if (!isCurrent(epoch)) { return null; }
            state.feedPromise = null;
            if (!feed || feed.StationId !== stationId) { return null; }
            state.feed = feed;
            state.feedFailedAt = 0;
            return feed;
        }, function (err) {
            if (!isCurrent(epoch)) { return null; }
            state.feedPromise = null;
            if (isForbidden(err)) { refused(err); return null; }
            if (isNotFound(err)) { stationGone(err); return null; }
            state.feedFailedAt = Date.now();
            state.feedRetryMs = FEED_RETRY_MS;
            return null;
        });
        state.feedPromise = request;
        return request;
    }

    /* The playing station's feed when it is not the header's (or there is no header). Dropped when playback moved
       to another item, or the session changed, while it was on its way. */
    function fetchPlayingFeed(stationId) {
        var client = api();
        if (!client || !checkSession()) { return Promise.resolve(null); }
        var playing = state.playing;
        if (playing.promise && playing.promiseFor === stationId) { return playing.promise; }
        var sessionEpoch = state.sessionEpoch;
        var request = client.getJSON(client.getUrl('Jellywave/Feed', { stationId: stationId })).then(function (feed) {
            if (sessionEpoch !== state.sessionEpoch || state.playing !== playing) { return null; }
            playing.promise = null;
            // Checked here on arrival and again in repaint before painting: deliberate defence in depth.
            return feed && feed.StationId === stationId && isPlaying(stationId) ? feed : null;
        }, function () {
            if (state.playing === playing) { playing.promise = null; }
            return null;
        });
        playing.promise = request;
        playing.promiseFor = stationId;
        return request;
    }

    /* Whether the header's station is one the list calls a plain stream (card L6): only then is its title asked. A missing
       or unknown kind gets the plain header but asks nothing (card L8), so a guessed station never reaches a route. */
    function selectedIsStream() {
        var station = state.selectedPlain ? stationById(state.selected) : null;
        return !!station && station.Kind === KIND_STREAM;
    }

    function selectedIsHls() {
        var station = selectedIsStream() ? stationById(state.selected) : null;
        return !!station && station.Codec === HLS_LABEL;
    }

    /* Whether the bar follows a plain stream without its header (RA R2): the header's plain stream still plays and this
       page painted its title into the bar during this playback. */
    function barFollowsPlain() {
        var station = playingStation();
        return !!station && state.selectedPlain && station.Id === state.selected && selectedIsStream() && state.playing.plainId === station.Id;
    }

    /* GET Jellywave/Title for the plain stream the header shows (card L6), one request at a time. A reply for another
       station, session or playback is dropped. A 404 means the stream left the list (or is no longer plain): the list
       is read again at once, as for a SUB/WAVE feed. */
    function fetchTitle() {
        var client = api();
        if (!client || !checkSession() || !state.selected || !selectedIsStream() || selectedIsHls()) { return Promise.resolve(null); }
        if (!state.header && !barFollowsPlain()) { return Promise.resolve(null); }
        if (state.titlePromise) { return state.titlePromise; }
        var epoch = state.epoch;
        var stationId = state.selected;
        var request = client.getJSON(client.getUrl('Jellywave/Title', { stationId: stationId })).then(function (r) {
            if (!isCurrent(epoch)) { return null; }
            state.titlePromise = null;
            if (!r || r.StationId !== stationId) { return null; }
            state.title = r;
            return r;
        }, function (err) {
            if (!isCurrent(epoch)) { return null; }
            state.titlePromise = null;
            if (isForbidden(err)) { refused(err); return null; }
            if (isNotFound(err)) { stationGone(err); return null; }
            return null;   // the server did not answer: the line keeps what it shows (and the 30 s rule)
        });
        state.titlePromise = request;
        return request;
    }

    /* Asks for the title (unless one request is on its way) and, when an answer for the header's station arrives, redraws the line and the bar. */
    function pollTitle() {
        var epoch = state.epoch;
        fetchTitle().then(function (r) {
            if (!r || !isCurrent(epoch)) { return; }
            try { render(); } catch (e) { console.warn('[Jellywave] render failed', e); }
            try { refreshPlainBar(); } catch (e) { console.warn('[Jellywave] bar refresh failed', e); }
        });
    }

    /* The "Now:" line's text: the title as sent, the not-supplied text, or, while the server has no title yet (or its
       samples fail), the querying text for TITLE_WAIT_MS from playback start and the delayed text after. */
    function nowText() {
        var t = state.title;
        if (selectedIsHls()) { return NOW_NOT_SUPPLIED; }
        if (t && t.State === TITLE_STATE_TITLE && t.Title) { return String(t.Title); }
        if (t && t.State === TITLE_STATE_NONE) { return NOW_NOT_SUPPLIED; }
        if (t && t.State === TITLE_STATE_FAILING) { return NOW_DELAYED; }
        return Date.now() - state.titleSince >= TITLE_WAIT_MS ? NOW_DELAYED : NOW_QUERYING;
    }

    /* Splits an ICY title at its first " - " into artist and title; without one (or with an empty side) the whole string
       is the title. */
    function splitTitle(text) {
        var at = text.indexOf(TITLE_SEPARATOR);
        var artist = at > 0 ? text.slice(0, at).trim() : '';
        var title = at > 0 ? text.slice(at + TITLE_SEPARATOR.length).trim() : '';
        return artist && title ? { artist: artist, title: title } : { artist: '', title: text };
    }

    function imageUrl(feed, size) {
        var client = api();
        if (!client || !feed || !feed.ItemId || !feed.ImageTag) { return null; }
        return client.getImageUrl(feed.ItemId, { type: 'Primary', tag: feed.ImageTag, maxWidth: size, maxHeight: size });
    }

    function stop() {
        var pm = state.deps && state.deps.playbackManager;
        if (pm) { pm.stop(); }
    }

    /* ---- Card L11: Stop instead of Pause ---- */

    /* The bar's CSS, once per page (boot runs once). */
    function injectBarStyle() {
        var style = document.createElement('style');
        style.setAttribute('data-jellywave', 'bar');
        style.textContent = BAR_CSS;
        (document.head || document.body).appendChild(style);
    }

    /* The body class follows the current station item (station: playingStation(), or null); while it is set, the bar's
       right group gets our Stop once (jellyfin-web builds the bar once and keeps it). */
    function markBar(station) {
        var body = document.body;
        if (!body) { return; }
        if (station) { body.classList.add(STATION_BODY_CLASS); ensureBarStop(); } else { body.classList.remove(STATION_BODY_CLASS); }
    }

    function ensureBarStop() {
        var right = document.querySelector('.nowPlayingBar .nowPlayingBarRight');
        if (!right || right.querySelector('.' + BAR_STOP_CLASS)) { return; }
        var button = document.createElement('button');
        button.setAttribute('type', 'button');
        button.setAttribute('title', 'Stop');
        button.setAttribute('aria-label', 'Stop');
        button.className = 'paper-icon-button-light mediaButton ' + BAR_STOP_CLASS;
        var icon = document.createElement('span');
        icon.className = 'material-icons stop';
        icon.setAttribute('aria-hidden', 'true');
        button.appendChild(icon);
        button.addEventListener('click', stop);
        var playPause = right.querySelector('.playPauseButton');
        right.insertBefore(button, playPause && playPause.parentNode === right ? playPause : null);
    }

    /* A player's pause. jellyfin-web 12.3's playbackManager does not pass a player's pause on (it reports progress from
       it), so the script listens on the current player itself, as the playing bar does. The station is taken now: during
       a switch jellyfin-web has already dropped the old item (streamInfo) when the media element's pause arrives, so
       there is none, and a switch that is slower than that finds another item, or a playing one, after the settle. */
    function onPlayerPause() {
        var pm = state.deps && state.deps.playbackManager;
        var station = playingStation();
        if (!pm || !station) { return; }   // not a station (music, video): Jellyfin's pause, untouched
        var stationId = station.Id;
        var player = pm.getCurrentPlayer ? pm.getCurrentPlayer() : null;
        setTimeout(function () {
            try {
                if (!pm.getCurrentPlayer || pm.getCurrentPlayer() !== player) { return; }
                var now = playingStation();
                if (!now || now.Id !== stationId || !pm.paused || !pm.paused(player)) { return; }
                pm.stop(player);
            } catch (e) { console.warn('[Jellywave] stop after pause failed', e); }
        }, PAUSE_SETTLE_MS);
    }

    /* Listens for pause on the current player only; a new player (a cast target, the first playback) is bound on
       playerchange and on playbackstart. */
    function bindPlayer() {
        var deps = state.deps;
        var pm = deps && deps.playbackManager;
        var events = deps && deps.events;
        if (!pm || !events) { return; }
        var player = null;
        try { player = pm.getCurrentPlayer ? pm.getCurrentPlayer() : null; } catch (e) { player = null; }
        if (player === state.boundPlayer) { return; }
        if (state.boundPlayer && events.off) { events.off(state.boundPlayer, 'pause', onPlayerPause); }
        state.boundPlayer = player;
        if (player) { events.on(player, 'pause', onPlayerPause); }
    }

    /* While a SUB/WAVE station's item plays, repaint from that station's feed (the header's own feed when the
       header shows it, else one fetched for the bar). A plain stream playing: its sampled title, when its header has
       one (card L6, refreshPlainBar); anything else playing: nothing, the bar keeps Jellyfin's own text. */
    function repaintPlaying() {
        var station = playingSubWave();
        if (!station) { refreshPlainBar(); return; }
        var id = station.Id;
        if (state.playing.stationId !== id) { state.playing = freshPlaying(); state.playing.stationId = id; }
        if (id === state.selected && state.feed && state.feed.StationId === id) { repaint(id, state.feed); return; }
        fetchPlayingFeed(id).then(function (feed) {
            if (!feed) { return; }
            try { repaint(id, feed); } catch (e) { console.warn('[Jellywave] bar refresh failed', e); }
        });
    }

    function repaint(id, feed) {
        // Deliberate defence in depth with fetchPlayingFeed's check on arrival.
        if (!isPlaying(id) || !feed || feed.StationId !== id || state.playing.stationId !== id) { return; }
        state.playing.feed = feed;
        refreshBar(feed);
        refreshCards(feed);
    }

    /* The playing bar copies the item when playback starts and never re-reads it; repaint its thumbnail and text.
       Best effort: it only touches elements that exist. */
    function refreshBar(f) {
        var img = document.querySelector('.nowPlayingBar .nowPlayingImage');
        var url = imageUrl(f, 140);
        if (img && url && f.ImageTag !== state.playing.barTag) {
            img.style.backgroundImage = 'url("' + url + '")';
            state.playing.barTag = f.ImageTag;
        }
        var text = document.querySelector('.nowPlayingBar .nowPlayingBarText');
        if (text) {
            var lines = text.querySelectorAll('a');
            if (lines[0] && f.TrackLine) { lines[0].innerText = f.TrackLine; }
            if (lines[1]) { lines[1].innerText = f.StationName + (f.Show ? ' · ' + f.Show : ''); }
        }
        try {
            if ('mediaSession' in navigator && window.MediaMetadata) {
                navigator.mediaSession.metadata = new window.MediaMetadata({
                    title: f.Title || f.StationName,
                    artist: f.Artist || f.StationName,
                    album: f.Album || f.Show || '',
                    artwork: url ? [{ src: imageUrl(f, 512), sizes: '512x512' }] : []
                });
            }
        } catch (e) { /* not supported here */ }
    }

    /* While a plain stream plays under its header and the server has its title (card L6): the bar's first line is
       "Artist – Title" (split at the first " - ", else the whole string), its second the station's name, and the media
       session the same; like a SUB/WAVE station's. No title (yet, any more, or none sent): the bar keeps what it shows. */
    function refreshPlainBar() {
        var station = playingStation();
        var t = state.title;
        if (!station || !isPlain(station) || station.Id !== state.selected || !state.selectedPlain) { return; }
        if (!state.header && state.playing.plainId !== station.Id) { return; }   // no header and never painted: Jellyfin's own
        if (!t || t.State !== TITLE_STATE_TITLE || !t.Title || t.StationId !== station.Id) { return; }
        var parts = splitTitle(String(t.Title));
        var name = station.Name ? String(station.Name) : '';
        var text = document.querySelector('.nowPlayingBar .nowPlayingBarText');
        if (text) {
            var lines = text.querySelectorAll('a');
            if (lines[0]) { lines[0].innerText = parts.artist ? parts.artist + ' \u2013 ' + parts.title : parts.title; }
            if (lines[1]) { lines[1].innerText = name; }
        }
        state.playing.plainId = station.Id;
        try {
            if ('mediaSession' in navigator && window.MediaMetadata) {
                navigator.mediaSession.metadata = new window.MediaMetadata({ title: parts.title, artist: parts.artist || name, album: '', artwork: [] });
            }
        } catch (e) { /* not supported here */ }
    }

    /* Cards in a visible grid keep the image they were built with; repaint the playing station's card when its cover changes. */
    function refreshCards(f) {
        if (!f.ItemId || !f.ImageTag) { return; }
        var signature = norm(f.ItemId) + '|' + f.ImageTag;
        if (signature === state.playing.cardTag) { return; }
        var url = imageUrl(f, 400);
        if (!url) { return; }
        var touched = false;
        document.querySelectorAll('.card').forEach(function (card) {
            if (norm(card.getAttribute('data-id')) !== norm(f.ItemId)) { return; }
            card.querySelectorAll('.cardImage, .cardImageContainer').forEach(function (el) {
                if (el.style.backgroundImage || el.classList.contains('cardImage')) {
                    el.style.backgroundImage = 'url("' + url + '")';
                    touched = true;
                }
            });
        });
        if (touched) { state.playing.cardTag = signature; }
    }

    /* Likes: status is per track; the station counts this server as one caller, so "liked" is shared. */
    /* A reply is kept only while the feed still shows the track it was asked for: a slow answer for track A
       arriving after track B started must not put A's id behind the heart. */
    function fetchLike() {
        var client = api();
        if (!client || state.selectedPlain || !state.feed || !state.feed.SongId) { state.like = null; state.likeSongId = null; return; }
        if (state.likeSongId === state.feed.SongId) { return; }
        var songId = state.feed.SongId;
        var epoch = state.epoch;
        state.likeSongId = songId;
        state.like = null;   // the previous track's heart goes until this track's status arrives
        function stale() { return !isCurrent(epoch) || state.likeSongId !== songId || !state.feed || state.feed.SongId !== songId; }
        client.getJSON(client.getUrl('Jellywave/Like', { stationId: state.selected })).then(function (r) {
            if (stale()) { return; }
            if (r && r.SongId && r.SongId !== songId) { return; }   // the station moved on during the round trip; the next feed brings that track
            calmed('like');
            state.like = r;
            renderLike();
        }).catch(function (err) {
            if (stale()) { return; }
            if (isRefusal(err)) {
                // Card R2b: refused before the station was asked. likeSongId stays, so no tick asks sooner; this song's status
                // is asked again after the wait (another song by then asks its own).
                later(epoch, function () {
                    if (state.likeSongId === songId) { state.likeSongId = null; fetchLike(); }
                }, backoffSeconds('like', songId, err) * 1000);
                return;
            }
            state.like = null;
            state.likeSongId = null;
        });
    }

    function renderLike() {
        var h = state.header;
        if (!h) { return; }
        var btn = h.querySelector('.jellywave-like');
        var like = state.like;
        var shown = !!like && !!like.Enabled && !!like.SongId;
        var notice = shown && state.likeNotice && state.likeNotice.songId === like.SongId ? state.likeNotice.text : '';
        // Fix round: also as a line under the actions, for touch, TV and the Android app, where a title never shows.
        showText(h.querySelector('.jellywave-like-note'), notice);
        if (!shown) { btn.style.display = 'none'; return; }
        btn.style.display = '';
        btn.disabled = state.likeBusy;
        btn.querySelector('.jellywave-like-icon').textContent = like.Liked ? 'favorite' : 'favorite_border';
        btn.querySelector('.jellywave-like-count').textContent = like.Count ? String(like.Count) : '';
        btn.title = notice || (like.Message ? like.Message : (like.Liked ? 'Liked' : 'Like this track'));
    }

    function sendLike() {
        var client = api();
        if (!client || !state.like || !state.like.SongId || state.likeBusy || !state.selected || state.selectedPlain) { return; }
        var songId = state.like.SongId;
        var epoch = state.epoch;
        state.likeBusy = true;
        renderLike();
        client.ajax({
            type: 'POST', url: client.getUrl('Jellywave/Like'), dataType: 'json', contentType: 'application/json',
            data: JSON.stringify({ SongId: songId, StationId: state.selected })
        }).then(function (r) {
            // The track or the station moved on (or the session changed) while the like was on its way: the reply is not about the heart shown now.
            if (!isCurrent(epoch) || !state.like || state.like.SongId !== songId) { return; }
            if (!r || !r.Enabled) { return; }
            if (r.SongId && r.SongId !== songId) { return; }
            state.likeNotice = null;   // card R2b: the station answered; an earlier "try again" no longer applies
            if (r.Message && !r.SongId) { state.like.Message = r.Message; return; }   // refusal: keep heart and count
            state.like = r;
        }).catch(function (err) {
            // Keep the old status. Card R2b: a refusal (nothing reached the station) says when to try again on the heart's
            // title until the wait is over; the like is not sent again by itself.
            if (!isCurrent(epoch) || !isRefusal(err)) { return; }
            var seconds = retryAfterSeconds(err);
            var notice = { songId: songId, text: refusedText(err, seconds) };
            state.likeNotice = notice;
            later(epoch, function () {
                if (state.likeNotice !== notice) { return; }
                state.likeNotice = null;
                renderLike();
            }, seconds * 1000);
        }).then(function () {
            if (!isCurrent(epoch)) { return; }   // the reset already cleared likeBusy; another station or session may have its own like in flight
            state.likeBusy = false;
            renderLike();
        });
    }

    /* Lyrics: one fetch per track while the panel is open; the station only serves the track on air. */
    function fetchLyrics(force) {
        var h = state.header;
        var client = api();
        if (!h || !client || !state.feed || state.selectedPlain) { return; }
        var panel = h.querySelector('.jellywave-lyrics');
        if (!state.feed.LyricsEnabled || !state.feed.SongId) { panel.style.display = 'none'; return; }
        panel.style.display = '';
        if (!panel.open) { return; }
        if (!force && state.lyricsSongId === state.feed.SongId) { return; }
        // Card R2b: refused for this song a moment ago; reopening the panel does not ask before the wait is over (its retry does).
        if (state.lyricsSongId === state.feed.SongId && Date.now() < state.lyricsWaitUntil) { return; }
        var songId = state.feed.SongId;
        var epoch = state.epoch;
        state.lyricsSongId = songId;
        state.lyrics = null;
        renderLyrics();
        client.getJSON(client.getUrl('Jellywave/Lyrics', { songId: songId, stationId: state.selected })).then(function (r) {
            if (!isCurrent(epoch) || state.lyricsSongId !== songId) { return; }
            calmed('lyrics');
            state.lyrics = r;
            renderLyrics();
            if (r && r.Status === 'pending') { later(epoch, function () { if (state.lyricsSongId === songId) { state.lyricsSongId = null; fetchLyrics(); } }, PENDING_LYRICS_RETRY_MS); }
        }).catch(function (err) {
            if (!isCurrent(epoch) || state.lyricsSongId !== songId) { return; }
            if (isRefusal(err)) {
                // Card R2b: refused before the station was asked; asked again after the wait while the song stays on air.
                var seconds = backoffSeconds('lyrics', songId, err);
                state.lyrics = { Status: 'unavailable', Message: refusedText(err, seconds, 'Asking again in') };
                state.lyricsWaitUntil = Date.now() + seconds * 1000;
                renderLyrics();
                later(epoch, function () { if (state.lyricsSongId === songId) { state.lyricsSongId = null; fetchLyrics(); } }, seconds * 1000);
                return;
            }
            state.lyrics = { Status: 'unavailable', Message: 'Could not reach the server.' };
            renderLyrics();
        });
    }

    function renderLyrics() {
        var h = state.header;
        if (!h) { return; }
        var body = h.querySelector('.jellywave-lyrics-body');
        var l = state.lyrics;
        body.textContent = '';
        if (!l) { body.textContent = 'Loading…'; return; }
        if (l.Status !== 'ok') { body.textContent = l.Message || l.Status; return; }
        l.Lines.forEach(function (line) { var d = document.createElement('div'); d.textContent = line; body.appendChild(d); });
    }

    /* Today's programme in the station's time zone. Asked once when the header is built, so the panel shows only for a
       station that publishes one; then refetched every five minutes at most (the server caches it as long) while the
       panel is open, or while the last answer said there is none (so a schedule published later, or a failure that
       passes, brings the panel back). One request at a time. */
    function fetchSchedule() {
        var h = state.header;
        var client = api();
        if (!h || !client || !state.selected || state.selectedPlain) { return; }
        var panel = h.querySelector('.jellywave-schedule');
        var s = state.schedule;
        var fresh = s && Date.now() - state.scheduleAt < SCHEDULE_REFRESH_MS;
        if (fresh || state.scheduleBusy || (s && s.Available && !panel.open)) { renderSchedule(); return; }
        var epoch = state.epoch;
        state.scheduleBusy = true;
        client.getJSON(client.getUrl('Jellywave/Schedule', { stationId: state.selected })).then(function (r) {
            if (!isCurrent(epoch)) { return; }
            state.scheduleBusy = false;
            state.schedule = r || { Available: false };
            state.scheduleAt = Date.now();
            renderSchedule();
        }).catch(function () {
            if (!isCurrent(epoch)) { return; }
            state.scheduleBusy = false;
            state.schedule = { Available: false };
            state.scheduleAt = Date.now();
            renderSchedule();
        });
    }

    function hourLabel(hour) {
        var h12 = hour % 12 === 0 ? 12 : hour % 12;
        return h12 + (hour < 12 || hour === 24 ? ' am' : ' pm');
    }

    /* The panel shows only while the station publishes a schedule; none (or no answer yet, or a failure): hidden. */
    function renderSchedule() {
        var h = state.header;
        if (!h) { return; }
        var panel = h.querySelector('.jellywave-schedule');
        var body = h.querySelector('.jellywave-schedule-body');
        var s = state.schedule;
        panel.style.display = s && s.Available ? '' : 'none';
        var signature = JSON.stringify(s || null);
        if (body.getAttribute('data-signature') === signature) { return; }   // unchanged: leave the DOM alone
        body.setAttribute('data-signature', signature);
        body.textContent = '';
        if (!s || !s.Available) { return; }
        if (!s.Today || !s.Today.length) { body.textContent = (s.Day || 'Today') + ': default programming all day.'; return; }
        s.Today.forEach(function (b) {
            var row = document.createElement('div');
            row.className = 'jellywave-schedule-row' + (b.Now ? ' jellywave-schedule-now' : '');
            var when = document.createElement('span');
            when.className = 'jellywave-schedule-when';
            when.textContent = hourLabel(b.StartHour) + ' – ' + hourLabel(b.EndHour);
            var what = document.createElement('span');
            what.textContent = b.Show + (b.Host ? ' with ' + b.Host : '');
            row.appendChild(when);
            row.appendChild(what);
            body.appendChild(row);
        });
        if (s.Takeover) {
            var tk = document.createElement('div');
            tk.className = 'jellywave-schedule-row jellywave-schedule-now';
            tk.textContent = 'On air now: ' + s.Takeover + ' (takeover)';
            body.insertBefore(tk, body.firstChild);
        }
        var note = document.createElement('div');
        note.className = 'jellywave-schedule-note';
        note.textContent = (s.Day || '') + (s.Timezone ? ' · station time (' + s.Timezone + ')' : '');
        body.appendChild(note);
    }

    /* The website as a link, or null: the page's own check of the server's rule (http or https only, so a
       javascript: or data: value never becomes an href). */
    function safeWebsite(value) {
        var url = typeof value === 'string' ? value : '';
        var m = url.length <= WEBSITE_MAX ? WEBSITE_RULE.exec(url) : null;
        if (!m) { return null; }
        var host = m[1].toLowerCase().replace(/:\d+$/, '').replace(/^www\./, '');
        return { href: url, label: host + ' \u2197' };
    }

    function showText(el, text) {
        el.textContent = text || '';
        el.style.display = text ? '' : 'none';
    }

    /* A plain stream's header, from the station list alone (card L8): logo, name, genre, notes, format line and website. */
    function renderPlain(h) {
        var station = stationById(state.selected);
        if (!station) { return; }
        var url = imageUrl(station, 600);
        h.querySelector('.jellywave-cover').style.backgroundImage = url ? 'url("' + url + '")' : '';
        h.querySelector('.jellywave-title-text').textContent = station.Name || '';
        showText(h.querySelector('.jellywave-genre'), station.Genre ? String(station.Genre) : '');
        showText(h.querySelector('.jellywave-notes'), station.Notes ? String(station.Notes) : '');
        var format = [station.Codec, typeof station.BitrateKbps === 'number' && station.BitrateKbps > 0 ? station.BitrateKbps + ' kbps' : '']
            .filter(Boolean).map(String).join(' \u00b7 ');
        showText(h.querySelector('.jellywave-format-text'), format);
        renderNow(h);
        var link = h.querySelector('.jellywave-website');
        var site = safeWebsite(station.Website);
        if (site) {
            if (link.getAttribute('href') !== site.href) { link.setAttribute('href', site.href); }
            link.textContent = site.label;
            link.style.display = '';
        } else {
            link.removeAttribute('href');
            link.textContent = '';
            link.style.display = 'none';
        }
    }

    /* The plain header's "Now:" line (card L6), as text only; hidden for a station the list does not call a plain stream.
       While it waits, one timer redraws it when TITLE_WAIT_MS has passed, so the delayed text shows on time. */
    function renderNow(h) {
        var line = h.querySelector('.jellywave-now');
        if (!line) { return; }
        if (!selectedIsStream() || (state.title && state.title.State === TITLE_STATE_OFF)) { line.style.display = 'none'; return; }
        line.style.display = '';
        line.querySelector('.jellywave-now-text').textContent = nowText();
        var waiting = !selectedIsHls() && (!state.title || (state.title.State !== TITLE_STATE_TITLE && state.title.State !== TITLE_STATE_NONE && state.title.State !== TITLE_STATE_FAILING));
        var left = TITLE_WAIT_MS - (Date.now() - state.titleSince);
        if (waiting && left > 0 && !state.titleTimer) {
            var epoch = state.epoch;
            state.titleTimer = setTimeout(function () {
                state.titleTimer = null;
                if (isCurrent(epoch)) { try { render(); } catch (e) { console.warn('[Jellywave] render failed', e); } }
            }, left);
        }
    }

    function render() {
        var h = state.header;
        var f = state.feed && state.feed.StationId === state.selected ? state.feed : null;
        if (!h) { return; }
        if (state.selectedPlain) { renderPlain(h); return; }
        var station = stationById(state.selected);
        var name = (f && f.StationName) || (station && station.Name) || '';
        var url = imageUrl(f, 600);
        h.querySelector('.jellywave-cover').style.backgroundImage = url ? 'url("' + url + '")' : '';
        h.querySelector('.jellywave-title-text').textContent = f ? (f.Title || (f.Online === false ? 'Off air' : name)) : 'Loading…';
        h.querySelector('.jellywave-sub').textContent = f ? [f.Artist, f.Album].filter(Boolean).join(' · ') : '';
        showText(h.querySelector('.jellywave-notes'), station && station.Notes ? String(station.Notes) : '');
        var dj = h.querySelector('.jellywave-dj');
        if (f && f.Dj) {
            dj.style.display = '';
            dj.querySelector('.jellywave-dj-text').textContent = 'DJ ' + f.Dj + (f.DjTagline ? ' — ' + f.DjTagline : '');
            var avatar = dj.querySelector('.jellywave-dj-avatar');
            var client = api();
            var avatarUrl = f.DjAvatarId && client ? client.getUrl('Jellywave/Avatar/' + encodeURIComponent(f.DjAvatarId), { stationId: f.StationId }) : '';
            if (avatarUrl && avatar.getAttribute('data-src') !== avatarUrl) {
                avatar.setAttribute('data-src', avatarUrl);
                var avatarEpoch = state.epoch;
                // The endpoint needs the session token, which an <img> cannot send; fetch with it and show the bytes.
                client.ajax({ type: 'GET', url: avatarUrl }).then(function (r) {
                    // ApiClient.ajax resolves the raw Response unless dataType is json/text.
                    return r && typeof r.blob === 'function' ? r.blob() : r;
                }).then(function (blob) {
                    // After a reset or a refusal the header is gone; a blob URL made for it now would never be revoked.
                    if (!blob || !isCurrent(avatarEpoch) || !document.body.contains(avatar) || avatar.getAttribute('data-src') !== avatarUrl) { return; }
                    if (avatar.src && avatar.src.indexOf('blob:') === 0) { URL.revokeObjectURL(avatar.src); }
                    avatar.src = URL.createObjectURL(blob);
                    avatar.removeAttribute('data-failed');
                    avatar.removeAttribute('data-waiting');
                    avatar.style.display = '';
                    calmed('portrait');
                }).catch(function (err) {
                    // Card R2b: a refusal (nothing reached the station) is not a failed portrait: data-src stays, so no render
                    // asks sooner, and after the wait the same portrait is asked again if it is still the one to show.
                    // Fix round: until then the avatar is hidden, and the previous DJ's portrait goes (not next to this DJ's name).
                    if (isRefusal(err)) {
                        if (!isCurrent(avatarEpoch)) { return; }   // the header it was for is gone
                        if (avatar.getAttribute('data-src') === avatarUrl) {
                            if (avatar.src && avatar.src.indexOf('blob:') === 0) { URL.revokeObjectURL(avatar.src); avatar.removeAttribute('src'); }
                            avatar.setAttribute('data-waiting', '1');
                            avatar.style.display = 'none';
                        }
                        later(avatarEpoch, function () {
                            if (!document.body.contains(avatar) || avatar.getAttribute('data-src') !== avatarUrl) { return; }
                            avatar.removeAttribute('data-src');
                            render();
                        }, backoffSeconds('portrait', avatarUrl, err) * 1000);
                        return;
                    }
                    avatar.setAttribute('data-failed', '1');
                    avatar.style.display = 'none';
                });
            }
            avatar.style.display = avatarUrl && !avatar.getAttribute('data-failed') && !avatar.getAttribute('data-waiting') ? '' : 'none';
        } else {
            dj.style.display = 'none';
            var hidden = dj.querySelector('.jellywave-dj-avatar');
            if (hidden.src && hidden.src.indexOf('blob:') === 0) { URL.revokeObjectURL(hidden.src); hidden.removeAttribute('src'); hidden.removeAttribute('data-src'); }
        }
        var meta = [];
        if (f && f.Show) { meta.push('On air: ' + f.Show); }
        if (f && f.UpNext) { meta.push('Up next: ' + f.UpNext); }
        if (f && f.NextShow) { meta.push('Up next: ' + f.NextShow + (f.NextShowPresenter ? ' with ' + f.NextShowPresenter : '') + (f.NextShowStartsAt ? ' at ' + f.NextShowStartsAt : '')); }
        if (f && typeof f.Listeners === 'number') { meta.push(f.Listeners + ' listening'); }
        var metaEl = h.querySelector('.jellywave-meta');
        metaEl.textContent = '';
        meta.forEach(function (m) { var d = document.createElement('div'); d.textContent = m; metaEl.appendChild(d); });
        var recent = h.querySelector('.jellywave-recent');
        if (f && f.Recent && f.Recent.length) {
            recent.style.display = '';
            var ol = recent.querySelector('ol');
            ol.textContent = '';
            f.Recent.forEach(function (line) { var li = document.createElement('li'); li.textContent = line; ol.appendChild(li); });
        } else {
            recent.style.display = 'none';
        }
        renderLike();
        fetchLyrics(false);
        renderSchedule();
        h.querySelector('.jellywave-status').textContent = f && f.FetchedUtc ? '' : (f ? 'The server has not heard from the station yet.' : '');
    }

    function stopTimer() {
        if (state.timer) { clearInterval(state.timer); state.timer = null; }
    }

    function tick() {
        if (!checkSession()) { stopTimer(); return; }
        if (state.header && !document.body.contains(state.header)) { state.header = null; }
        fetchStations(false);
        // Playback moved without an event this script saw: the header follows (a header built here ticks itself).
        if (ensureHeader(document)) { return; }
        // Another SUB/WAVE station playing than the header's (or no header): its own feed, without waiting for the header's.
        var playing = playingSubWave();
        if (playing && (!state.header || playing.Id !== state.selected)) {
            try { repaintPlaying(); } catch (e) { console.warn('[Jellywave] bar refresh failed', e); }
        }
        var epoch = state.epoch;
        // A plain stream's header is drawn from the list, which fetchStations above keeps fresh, and asks only for its
        // title (card L6), which then repaints the line and the playing bar.
        if (state.header && state.selectedPlain) {
            try { render(); } catch (e) { console.warn('[Jellywave] render failed', e); }
            pollTitle();
        } else if (barFollowsPlain()) {
            pollTitle();   // RA R2: the header left the document; the bar it painted keeps following the title
        }
        var header = state.header && !state.selectedPlain ? fetchFeed().then(function () {
            // The header's station changed while the feed was on its way: the new header's own tick takes over.
            if (!isCurrent(epoch)) { return; }
            followWake(epoch);
            try { fetchLike(); } catch (e) { console.warn('[Jellywave] like status failed', e); }
            try { render(); } catch (e) { console.warn('[Jellywave] render failed', e); }
            try { fetchSchedule(); } catch (e) { console.warn('[Jellywave] schedule failed', e); }
            // The header's station playing: repaint from the feed just fetched.
            if (isPlaying(state.selected)) {
                try { repaintPlaying(); } catch (e) { console.warn('[Jellywave] bar refresh failed', e); }
            }
        }) : Promise.resolve();
        header.then(function () {
            if (!state.header && !playingSubWave() && !barFollowsPlain()) { stopTimer(); }
        });
    }

    /* Card L14: until the server has heard the header's station (an idle station's page just opened), ask its feed again
       soon (FEED_WAKE_MS, FEED_WAKE_TRIES). When the first heard feed arrives after an unheard one, the schedule is asked
       again too, so its "now" block and a takeover follow the live show. */
    function followWake(epoch) {
        var f = state.feed;
        if (!f) { return; }
        var heard = !!f.FetchedUtc;
        if (heard && state.feedUnheard) { state.scheduleAt = 0; }
        state.feedUnheard = !heard;
        if (heard || state.wakeTimer || state.wakeTries >= FEED_WAKE_TRIES) { return; }
        state.wakeTries += 1;
        state.wakeTimer = setTimeout(function () {
            state.wakeTimer = null;
            if (isCurrent(epoch) && state.header) { tick(); }
        }, FEED_WAKE_MS);
    }

    function ensureTimer() {
        if (!state.timer) { state.timer = setInterval(tick, POLL_MS); }
    }

    /* The first list container in root whose data-parentid is the channel; cached views keep other
       libraries' containers in the document, so the first one found is not necessarily ours. A document-wide
       search skips cached views (viewContainer.js hides them with .hide); a shown view is taken as it is. */
    function findChannelContainer(root, channelId) {
        var found = root.querySelectorAll('.itemsContainer[data-parentid]');
        for (var i = 0; i < found.length; i++) {
            if (norm(found[i].getAttribute('data-parentid')) !== norm(channelId)) { continue; }
            if (root === document && found[i].closest('.mainAnimatedPage.hide')) { continue; }
            return found[i];
        }
        return null;
    }

    /* The tile's library page: .itemsContainer carries data-parentid = channel id (legacy list controller, used by
       both layouts for channels; it sets the attribute before the view's viewshow). Insert the header above the
       toolbar row of that page, for the station playing now (a SUB/WAVE header or a plain stream's, card L8); none
       playing: no header, and any shown one goes. root is the shown view, or the document. Returns whether it built a header (it then
       ran a tick itself). */
    function ensureHeader(root) {
        followPlayback();
        if (!state.selected || !state.stations || !state.stations.length || !state.channelId) { return false; }
        var container = findChannelContainer(root || document, state.channelId);
        if (!container) { return false; }
        var page = container.closest('.page') || document;
        var existing = page.querySelector('.jellywave-header');
        var kind = state.selectedPlain ? KIND_STREAM : KIND_SUBWAVE;
        if (existing && existing.getAttribute('data-station') === state.selected && existing.getAttribute('data-kind') === kind) {
            // Back on a cached view: repaint its header, not the one built for another view since.
            if (existing !== state.header) { state.header = existing; render(); ensureTimer(); }
            return false;
        }
        // Deliberate defence in depth: followPlayback removes the old header on a change; this check alone would
        // also replace it.
        if (existing) { removeHeader(existing); }   // a cached view's header for another station
        var anchor = page.querySelector('.itemsViewSettingsContainer') || container;
        var stationId = state.selected;
        var header = document.createElement('div');
        header.setAttribute('data-station', stationId);
        header.setAttribute('data-kind', kind);
        if (state.selectedPlain) {
            header.className = 'jellywave-header jellywave-header-plain';
            header.innerHTML =
                '<div class="jellywave-cover"></div>' +
                '<div class="jellywave-body">' +
                '<p class="jellywave-title"><span class="jellywave-title-text"></span></p>' +
                '<p class="jellywave-genre" style="display:none"></p>' +
                '<p class="jellywave-notes" style="display:none"></p>' +
                '<p class="jellywave-now" style="display:none"><span class="jellywave-now-label"></span> <span class="jellywave-now-text"></span></p>' +
                '<div class="jellywave-format"><span class="jellywave-format-text"></span>' +
                '<a class="jellywave-website" target="_blank" rel="noopener noreferrer" style="display:none"></a></div>' +
                '<div class="jellywave-actions">' +
                '<button is="emby-button" type="button" class="raised emby-button jellywave-stop"><span class="material-icons stop" aria-hidden="true"></span><span>Stop</span></button>' +
                '</div></div>';   // a plain stream's header: no feed, likes, lyrics, schedule or requests; its title only (L6)
            header.querySelector('.jellywave-stop').addEventListener('click', stop);
            header.querySelector('.jellywave-now-label').textContent = NOW_LABEL;
            anchor.parentNode.insertBefore(header, anchor);
            state.header = header;
            render();
            ensureTimer();
            pollTitle();   // card L6: the title at once; every tick asks again
            return true;
        }
        header.className = 'jellywave-header';
        header.innerHTML =
            '<div class="jellywave-cover"></div>' +
            '<div class="jellywave-body">' +
            '<p class="jellywave-title"><span class="jellywave-title-text"></span>' +
            '<button type="button" class="jellywave-like" title="Like this track" style="display:none"><span class="material-icons jellywave-like-icon" aria-hidden="true">favorite_border</span><span class="jellywave-like-count"></span></button></p>' +
            '<p class="jellywave-sub"></p>' +
            '<p class="jellywave-notes" style="display:none"></p>' +
            '<div class="jellywave-dj" style="display:none"><img class="jellywave-dj-avatar" alt="" /><div class="jellywave-dj-text"></div></div>' +
            '<div class="jellywave-meta"></div>' +
            '<div class="jellywave-actions">' +
            '<button is="emby-button" type="button" class="raised emby-button jellywave-stop"><span class="material-icons stop" aria-hidden="true"></span><span>Stop</span></button>' +
            '</div><div class="jellywave-like-note" style="display:none"></div><div class="jellywave-status"></div></div>' +
            '<form class="jellywave-request">' +
            '<input type="text" class="jellywave-request-text" maxlength="280" placeholder="Request a song, or say hi to the DJ" autocomplete="off" />' +
            '<input type="text" class="jellywave-request-name" maxlength="40" placeholder="Your name" autocomplete="off" />' +
            '<button is="emby-button" type="submit" class="raised emby-button jellywave-request-send"><span>Send</span></button>' +
            '<div class="jellywave-request-result"></div>' +
            '<div class="jellywave-request-help">Requests go to the station\'s DJ and share the station\'s limits: a short wait between requests, one pending at a time and a few per hour for everyone on this server together, so a "still queued" or "try again later" answer may be about someone else\'s request.</div>' +
            '</form>' +
            '<div class="jellywave-panels">' +
            '<details class="jellywave-recent" style="display:none"><summary>Recently played</summary><ol></ol></details>' +
            '<details class="jellywave-schedule" style="display:none"><summary>Today on the station</summary><div class="jellywave-schedule-body"></div></details>' +
            '<details class="jellywave-lyrics" style="display:none"><summary>Lyrics</summary><div class="jellywave-lyrics-body"></div></details>' +
            '</div>';   // closes .jellywave-panels; the header element itself is the container
        header.querySelector('.jellywave-like').addEventListener('click', sendLike);
        header.querySelector('.jellywave-lyrics').addEventListener('toggle', function () { fetchLyrics(true); });
        header.querySelector('.jellywave-schedule').addEventListener('toggle', function () { fetchSchedule(); });
        // The header shows only while its station plays, so it has Stop and no Play; playbackstop then removes it.
        header.querySelector('.jellywave-stop').addEventListener('click', stop);
        wireRequests(header, stationId);
        anchor.parentNode.insertBefore(header, anchor);
        state.header = header;
        render();
        ensureTimer();
        tick();
        return true;
    }

    /* Listener requests: POST Jellywave/Request for this header's station, then poll Jellywave/Request/{id} (the
       server answers from the receipt's station) until the station resolves it. */
    function wireRequests(header, stationId) {
        var form = header.querySelector('.jellywave-request');
        var text = form.querySelector('.jellywave-request-text');
        var name = form.querySelector('.jellywave-request-name');
        var send = form.querySelector('.jellywave-request-send');
        var out = form.querySelector('.jellywave-request-result');
        var client = api();
        if (client && client.getCurrentUser) {
            client.getCurrentUser().then(function (u) { if (u && u.Name && !name.value) { name.value = u.Name; } }).catch(function () { /* fine */ });
        }
        var pollTimer = null;
        var epoch = state.epoch;   // the session and station this header was built for; its requests stop when either changes
        function sameSession() { return checkSession() && isCurrent(epoch); }
        function say(msg) { out.textContent = msg || ''; }
        function describe(r) {
            if (!r) { return 'No answer.'; }
            if (r.Status === 'pending') { return r.Message || 'Waiting for the DJ…'; }
            if (r.Status === 'resolved') {
                var bits = [];
                if (r.Ack) { bits.push(r.Ack); }
                if (r.Track) { bits.push('Queued: ' + r.Track + (typeof r.QueuePosition === 'number' ? ' (position ' + r.QueuePosition + ')' : '')); }
                if (!bits.length) { bits.push(r.Message || 'Done.'); }
                return bits.join(' ');
            }
            if (r.RetryAfterSeconds) { return (r.Message || 'Too soon.') + ' (' + r.RetryAfterSeconds + ' s)'; }
            return r.Message || r.Status;
        }
        function poll(id, tries) {
            clearTimeout(pollTimer);
            if (!sameSession() || !document.body.contains(out)) { return; }   // signed out, another user or station, or page left
            client.getJSON(client.getUrl('Jellywave/Request/' + encodeURIComponent(id))).then(function (r) {
                if (!isCurrent(epoch)) { return; }
                calmed('request');
                say(describe(r));
                if (r && r.Status === 'pending' && tries < REQUEST_POLL_TRIES) { pollTimer = later(epoch, function () { poll(id, tries + 1); }, REQUEST_POLL_MS); }
                else { send.disabled = false; }
            }).catch(function (err) {
                if (!isCurrent(epoch)) { return; }
                if (isRefusal(err) && tries < REQUEST_POLL_TRIES) {
                    // Card R2b: refused before the station was asked; the request is still on its way, so polling goes on
                    // after the wait (it counts as one of the tries) and Send stays busy.
                    var seconds = backoffSeconds('request', id, err);
                    say(refusedText(err, seconds, 'Checking again in'));
                    pollTimer = later(epoch, function () { poll(id, tries + 1); }, seconds * 1000);
                    return;
                }
                say(isForbidden(err) ? NO_ACCESS_MESSAGE : 'Lost contact while waiting.');
                send.disabled = false;
            });
        }
        form.addEventListener('submit', function (e) {
            e.preventDefault();
            var client2 = api();
            var value = text.value.trim();
            if (!client2 || !value || !sameSession()) { return; }
            send.disabled = true;
            say('Sending…');
            client2.ajax({
                type: 'POST', url: client2.getUrl('Jellywave/Request'), dataType: 'json', contentType: 'application/json',
                data: JSON.stringify({ Text: value, Name: name.value.trim(), StationId: stationId })
            }).then(function (r) {
                if (!isCurrent(epoch)) { return; }
                say(describe(r));
                if (r && r.Accepted && r.RequestId) { text.value = ''; poll(r.RequestId, 0); } else { send.disabled = false; }
            }).catch(function (err) {
                if (!isCurrent(epoch)) { return; }
                // Card R2b: the text stays in the box and Send is free again. A 429 or 503 is this server's refusal before
                // it asked the station, so nothing was sent: the listener may send it again after the wait. Anything else
                // with no clear answer (a network failure, a timeout, another 5xx) may have reached the station: it is
                // never sent again by itself, and the page says so (fix round: "could not reach" only when nothing answered).
                var status = err && err.status;
                if (isForbidden(err)) { say(NO_ACCESS_MESSAGE); }
                else if (isRefusal(err)) { say(refusedText(err, retryAfterSeconds(err))); }
                else if (!status) { say(REQUEST_NO_ANSWER_MESSAGE); }
                else if (status >= 500) { say(REQUEST_UNCLEAR_MESSAGE); }
                else { say('The server did not take the request.'); }
                send.disabled = false;
            });
        });
    }

    /* Legacy layout only: its drawer has a slot for custom links (.customMenuOptions); the modern layout has
       no user drawer and lists the tile in its top bar instead. The link opens the tile's library page. */
    function ensureDrawerLink() {
        var container = document.querySelector('.customMenuOptions');
        var channelId = state.channelId;
        if (!container || !channelId || !state.stations || !state.stations.length || container.querySelector('.jellywave-link')) { return; }
        var link = document.createElement('a');
        link.className = 'navMenuOption lnkMediaFolder jellywave-link';
        link.href = '#';
        link.innerHTML = '<span class="material-icons navMenuOptionIcon radio" aria-hidden="true"></span><span class="navMenuOptionText"></span>';
        link.querySelector('.navMenuOptionText').textContent = state.channelName || 'Radio';
        link.addEventListener('click', function (e) {
            e.preventDefault();
            var router = state.deps && state.deps.appRouter;
            var client = api();
            if (router && router.showItem && client) { router.showItem(String(channelId), client.serverId()); }
        });
        container.appendChild(link);
    }

    /* A view was shown, or the DOM settled: check the session, then add the header and the drawer link
       where their containers exist. The containers are checked through live collections first. */
    function onDomChange(root) {
        if (!checkSession()) { return; }
        if (!listContainers.length && !drawerSlots.length) { return; }
        if (!state.stations || !state.stations.length) {
            // The list's answer places the header itself (applyStations searches the whole document: the user may
            // have moved on while it was on its way, and that view's viewshow found the fetch in flight). An empty
            // list (plain streams only) is asked again after STATIONS_REFRESH_MS, so a station added later shows.
            if (!state.stationsPromise && (drawerSlots.length || document.querySelector('.itemsContainer[data-parentid]'))) { fetchStations(!state.stations); }
            return;
        }
        ensureHeader(root);
        ensureDrawerLink();
    }

    /* jellyfin-web dispatches a bubbling viewshow on every view it shows (viewManager.js for legacy views,
       Page.tsx for React ones); this is the main path for the header. */
    function onViewShow(e) {
        var view = e && e.target && e.target.querySelectorAll ? e.target : document;
        try { onDomChange(view); } catch (err) { console.warn('[Jellywave] view check failed', err); }
    }

    /* Fallback for what viewshow does not cover (the legacy drawer, late renders): at most one pass per
       DOM_DEBOUNCE_MS, through one pending timer, with no document query in the observer callback itself. */
    function onMutation() {
        if (state.domTimer) { return; }
        state.domTimer = setTimeout(function () {
            state.domTimer = null;
            try { onDomChange(document); } catch (err) { console.warn('[Jellywave] page check failed', err); }
        }, DOM_DEBOUNCE_MS);
    }

    function boot(deps) {
        if (state.booted) { return; }   // the plugin manager may construct the plugin twice; one observer, one set of listeners
        state.booted = true;
        state.deps = deps;
        document.addEventListener('viewshow', onViewShow);
        new MutationObserver(onMutation).observe(document.body, { childList: true, subtree: true });
        injectBarStyle();
        onDomChange(document);
        if (deps && deps.events && deps.playbackManager) {
            bindPlayer();
            deps.events.on(deps.playbackManager, 'playerchange', function () {
                try { bindPlayer(); } catch (e) { console.warn('[Jellywave] player binding failed', e); }
            });
            deps.events.on(deps.playbackManager, 'playbackstart', function () {
                try { bindPlayer(); } catch (e) { console.warn('[Jellywave] player binding failed', e); }
                // The list knows which items are stations, and their kind; a new station's item may be newer than our copy.
                fetchStations(true).then(function () {
                    var station = playingSubWave();
                    // Jellyfin rebuilt the bar from the new item: nothing on it is this page's any more (a plain stream's too).
                    state.playing = freshPlaying();
                    // Its header (on the tile's page), another station's header replaced, or the header gone.
                    var built = false;
                    try { built = ensureHeader(document); } catch (e) { console.warn('[Jellywave] header failed', e); }
                    if (station && !built) { ensureTimer(); tick(); }   // a header built just now ticked itself
                });
            });
            // jellyfin-web rebuilds the bar text and the media session from its snapshot on these events;
            // repaint right away instead of waiting for the next tick. (12.3's playbackManager itself fires playbackstop
            // only; pause, unpause and statechange come from players, so here they come from other versions, if at all.)
            ['pause', 'unpause', 'statechange', 'playbackstop'].forEach(function (name) {
                deps.events.on(deps.playbackManager, name, function () {
                    state.playing.barTag = null;
                    setTimeout(function () {
                        // A stop, or another item: the header follows (gone, or another station's).
                        try { if (checkSession()) { ensureHeader(document); } } catch (e) { console.warn('[Jellywave] header failed', e); }
                        try { repaintPlaying(); } catch (e) { /* best effort */ }
                        try { render(); } catch (e) { /* best effort */ }
                    }, 50);
                });
            });
        }
    }

    window.JellywaveUi = function () {
        return Promise.resolve(function JellywaveUiPlugin(deps) {
            this.name = 'Jellywave';
            this.id = 'jellywave';
            this.type = 'jellywave';
            boot(deps);
        });
    };
})();
