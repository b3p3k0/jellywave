/* Jellywave station page: injected into jellyfin-web by the File Transformation plugin.
   Registered as a jellyfin-web client plugin (window.JellywaveUi, listed in config.json) so the
   plugin manager hands it the playback manager. Everything talks to the Jellyfin server only.
   What it does: (1) turns the channel's own library page into the station page by inserting a
   header above the item grid; (2) while the live item plays, repaints the playing bar's thumbnail
   and text, the item's card in any visible grid, and the browser media session. */
(function () {
    'use strict';

    var POLL_MS = 5000;
    var FEED_RETRY_MS = 15000;
    /* 403: this user may not see the channel (blocked, not enabled) or is an API key. Ask rarely. */
    var FEED_FORBIDDEN_RETRY_MS = 300000;
    var HTTP_FORBIDDEN = 403;
    /* 404: no channel entity yet, or its name collides (fresh install, rename). Keeps the short retry. */
    var HTTP_NOT_FOUND = 404;
    /* DOM changes are handled at most once per window, through one pending timer. */
    var DOM_DEBOUNCE_MS = 250;
    var NO_ACCESS_MESSAGE = 'You don\'t have access to this station.';
    /* sessionKey: serverId|userId the state belongs to; epoch: bumped on every reset, so a reply to a request
       sent before the reset is dropped. */
    var state = { feed: null, deps: null, timer: null, header: null, lastTag: null, lastCardTag: null, feedFailedAt: 0, feedRetryMs: FEED_RETRY_MS, like: null, likeSongId: null, likeBusy: false, lyrics: null, lyricsSongId: null, schedule: null, scheduleAt: 0, fetching: false, sessionKey: null, epoch: 0, domTimer: null, booted: false };
    /* Live collections, so the DOM-change check is a length read instead of a document query. */
    var listContainers = document.getElementsByClassName('itemsContainer');
    var drawerSlots = document.getElementsByClassName('customMenuOptions');

    function api() { return window.ApiClient || null; }
    /* ApiClient rejects with the fetch Response on an HTTP error; a network failure has no status. */
    function isForbidden(err) { return !!err && err.status === HTTP_FORBIDDEN; }
    function isNotFound(err) { return !!err && err.status === HTTP_NOT_FOUND; }
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

    /* A sign-out, another user or another server: drop everything the previous session saw.
       Returns whether someone is signed in. */
    function checkSession() {
        var key = sessionKey();
        if (key === state.sessionKey) { return key !== null; }
        forgetStation();
        state.sessionKey = key;
        state.epoch += 1;
        state.like = null;
        state.likeSongId = null;
        state.likeBusy = false;
        state.lyrics = null;
        state.lyricsSongId = null;
        state.schedule = null;
        state.scheduleAt = 0;
        state.feedFailedAt = 0;
        state.feedRetryMs = FEED_RETRY_MS;
        state.lastTag = null;
        state.lastCardTag = null;
        state.fetching = false;
        return key !== null;
    }

    function fetchFeed() {
        var client = api();
        if (!client || !checkSession()) { return Promise.resolve(null); }
        if (state.feedFailedAt && Date.now() - state.feedFailedAt < state.feedRetryMs) { return Promise.resolve(null); }
        var epoch = state.epoch;
        return client.getJSON(client.getUrl('Jellywave/Feed')).then(function (feed) {
            if (!isCurrent(epoch)) { return null; }
            state.feed = feed;
            state.feedFailedAt = 0;
            return feed;
        }).catch(function (err) {
            if (!isCurrent(epoch)) { return null; }
            state.feedFailedAt = Date.now();
            state.feedRetryMs = FEED_RETRY_MS;
            if (isForbidden(err)) { state.feedRetryMs = FEED_FORBIDDEN_RETRY_MS; }
            if (isForbidden(err) || isNotFound(err)) { forgetStation(); }
            return null;
        });
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

    /* Refused by the server (403, 404) or a new session: drop what an earlier answer showed, so a blocked
       user sees nothing. Every header goes, including one in a cached view that state.header no longer holds. */
    function forgetStation() {
        state.feed = null;
        if (state.header) { removeHeader(state.header); }
        state.header = null;
        var headers = document.querySelectorAll('.jellywave-header');
        for (var i = 0; i < headers.length; i++) { removeHeader(headers[i]); }
        var links = document.querySelectorAll('.jellywave-link');
        for (var j = 0; j < links.length; j++) { detach(links[j]); }
    }

    function imageUrl(feed, size) {
        var client = api();
        if (!client || !feed || !feed.ItemId || !feed.ImageTag) { return null; }
        return client.getImageUrl(feed.ItemId, { type: 'Primary', tag: feed.ImageTag, maxWidth: size, maxHeight: size });
    }

    function isOurs() {
        var pm = state.deps && state.deps.playbackManager;
        if (!pm || !state.feed || !state.feed.ItemId) { return false; }
        try {
            // currentItem(player) throws when no player is active; getCurrentPlayer() returns null then.
            var player = pm.getCurrentPlayer ? pm.getCurrentPlayer() : null;
            if (!player) { return false; }
            // After a stop the player stays current and its stream info can linger; isPlaying() is the truth.
            if (pm.isPlaying && !pm.isPlaying(player)) { return false; }
            var item = pm.currentItem(player);
            return !!(item && item.Id && norm(item.Id) === norm(state.feed.ItemId));
        } catch (e) {
            return false;
        }
    }

    function play() {
        var pm = state.deps && state.deps.playbackManager;
        var client = api();
        if (!pm || !client || !state.feed || !state.feed.ItemId) { return; }
        var result = pm.play({ ids: [state.feed.ItemId], serverId: client.serverId() });
        if (result && result.catch) { result.catch(function (e) { console.warn('[Jellywave] play failed', e); }); }
    }

    function stop() {
        var pm = state.deps && state.deps.playbackManager;
        if (pm) { pm.stop(); }
    }

    /* The playing bar copies the item when playback starts and never re-reads it; while our item plays,
       repaint its thumbnail and text from the feed. Best effort: it only touches elements that exist. */
    function refreshBar() {
        if (!isOurs() || !state.feed) { return; }
        var f = state.feed;
        var img = document.querySelector('.nowPlayingBar .nowPlayingImage');
        var url = imageUrl(f, 140);
        if (img && url && f.ImageTag !== state.lastTag) {
            img.style.backgroundImage = 'url("' + url + '")';
            state.lastTag = f.ImageTag;
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

    /* Cards in a visible grid keep the image they were built with; repaint ours when the cover changes. */
    function refreshCards() {
        var f = state.feed;
        if (!f || !f.ItemId || !f.ImageTag || f.ImageTag === state.lastCardTag) { return; }
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
        if (touched) { state.lastCardTag = f.ImageTag; }
    }

    /* Likes: status is per track; the station counts this server as one caller, so "liked" is shared. */
    /* A reply is kept only while the feed still shows the track it was asked for: a slow answer for track A
       arriving after track B started must not put A's id behind the heart. */
    function fetchLike() {
        var client = api();
        if (!client || !state.feed || !state.feed.SongId) { state.like = null; state.likeSongId = null; return; }
        if (state.likeSongId === state.feed.SongId) { return; }
        var songId = state.feed.SongId;
        var epoch = state.epoch;
        state.likeSongId = songId;
        state.like = null;   // the previous track's heart goes until this track's status arrives
        function stale() { return !isCurrent(epoch) || state.likeSongId !== songId || !state.feed || state.feed.SongId !== songId; }
        client.getJSON(client.getUrl('Jellywave/Like')).then(function (r) {
            if (stale()) { return; }
            state.like = r;
            renderLike();
        }).catch(function () {
            if (stale()) { return; }
            state.like = null;
            state.likeSongId = null;
        });
    }

    function renderLike() {
        var h = state.header;
        if (!h) { return; }
        var btn = h.querySelector('.jellywave-like');
        var like = state.like;
        if (!like || !like.Enabled || !like.SongId) { btn.style.display = 'none'; return; }
        btn.style.display = '';
        btn.disabled = state.likeBusy;
        btn.querySelector('.jellywave-like-icon').textContent = like.Liked ? 'favorite' : 'favorite_border';
        btn.querySelector('.jellywave-like-count').textContent = like.Count ? String(like.Count) : '';
        btn.title = like.Message ? like.Message : (like.Liked ? 'Liked' : 'Like this track');
    }

    function sendLike() {
        var client = api();
        if (!client || !state.like || !state.like.SongId || state.likeBusy) { return; }
        var songId = state.like.SongId;
        var epoch = state.epoch;
        state.likeBusy = true;
        renderLike();
        client.ajax({
            type: 'POST', url: client.getUrl('Jellywave/Like'), dataType: 'json', contentType: 'application/json',
            data: JSON.stringify({ SongId: songId })
        }).then(function (r) {
            // The track moved on (or the session changed) while the like was on its way: the reply is not about the heart shown now.
            if (!isCurrent(epoch) || !state.like || state.like.SongId !== songId) { return; }
            if (!r || !r.Enabled) { return; }
            if (r.SongId && r.SongId !== songId) { return; }
            if (r.Message && !r.SongId) { state.like.Message = r.Message; return; }   // refusal: keep heart and count
            state.like = r;
        }).catch(function () { /* keep the old status */ }).then(function () {
            if (!isCurrent(epoch)) { return; }   // the reset already cleared likeBusy; a new session may have its own like in flight
            state.likeBusy = false;
            renderLike();
        });
    }

    /* Lyrics: one fetch per track while the panel is open; the station only serves the track on air. */
    function fetchLyrics(force) {
        var h = state.header;
        var client = api();
        if (!h || !client || !state.feed) { return; }
        var panel = h.querySelector('.jellywave-lyrics');
        if (!state.feed.LyricsEnabled || !state.feed.SongId) { panel.style.display = 'none'; return; }
        panel.style.display = '';
        if (!panel.open) { return; }
        if (!force && state.lyricsSongId === state.feed.SongId) { return; }
        var songId = state.feed.SongId;
        var epoch = state.epoch;
        state.lyricsSongId = songId;
        state.lyrics = null;
        renderLyrics();
        client.getJSON(client.getUrl('Jellywave/Lyrics', { songId: songId })).then(function (r) {
            if (!isCurrent(epoch) || state.lyricsSongId !== songId) { return; }
            state.lyrics = r;
            renderLyrics();
            if (r && r.Status === 'pending') { setTimeout(function () { if (isCurrent(epoch) && state.lyricsSongId === songId) { state.lyricsSongId = null; fetchLyrics(); } }, 8000); }
        }).catch(function () {
            if (!isCurrent(epoch) || state.lyricsSongId !== songId) { return; }
            state.lyrics = { Status: 'unavailable', Message: 'Could not reach the server.' };
            renderLyrics();
        });
    }

    function renderLyrics() {
        var h = state.header;
        if (!h) { return; }
        var body = h.querySelector('.jellywave-lyrics-body');
        var l = state.lyrics;
        body.innerHTML = '';
        if (!l) { body.textContent = 'Loading…'; return; }
        if (l.Status !== 'ok') { body.textContent = l.Message || l.Status; return; }
        l.Lines.forEach(function (line) { var d = document.createElement('div'); d.textContent = line; body.appendChild(d); });
    }

    /* Today's programme in the station's time zone; refetched every five minutes at most (the server caches it as long). */
    function fetchSchedule() {
        var h = state.header;
        var client = api();
        if (!h || !client) { return; }
        var panel = h.querySelector('.jellywave-schedule');
        if (!panel.open) { return; }
        if (state.schedule && Date.now() - state.scheduleAt < 300000) { renderSchedule(); return; }
        var epoch = state.epoch;
        client.getJSON(client.getUrl('Jellywave/Schedule')).then(function (r) {
            if (!isCurrent(epoch)) { return; }
            state.schedule = r;
            state.scheduleAt = Date.now();
            renderSchedule();
        }).catch(function () {
            if (!isCurrent(epoch)) { return; }
            state.schedule = { Available: false };
            renderSchedule();
        });
    }

    function hourLabel(hour) {
        var h12 = hour % 12 === 0 ? 12 : hour % 12;
        return h12 + (hour < 12 || hour === 24 ? ' am' : ' pm');
    }

    function renderSchedule() {
        var h = state.header;
        if (!h) { return; }
        var body = h.querySelector('.jellywave-schedule-body');
        var s = state.schedule;
        var signature = JSON.stringify(s || null);
        if (body.getAttribute('data-signature') === signature) { return; }   // unchanged: leave the DOM alone
        body.setAttribute('data-signature', signature);
        body.innerHTML = '';
        if (!s || !s.Available) { body.textContent = 'No schedule published.'; return; }
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

    function render() {
        var h = state.header;
        var f = state.feed;
        if (!h) { return; }
        var url = imageUrl(f, 600);
        h.querySelector('.jellywave-cover').style.backgroundImage = url ? 'url("' + url + '")' : '';
        h.querySelector('.jellywave-title-text').textContent = f ? (f.Title || (f.Online === false ? 'Off air' : f.StationName)) : 'Loading…';
        h.querySelector('.jellywave-sub').textContent = f ? [f.Artist, f.Album].filter(Boolean).join(' · ') : '';
        var dj = h.querySelector('.jellywave-dj');
        if (f && f.Dj) {
            dj.style.display = '';
            dj.querySelector('.jellywave-dj-text').textContent = 'DJ ' + f.Dj + (f.DjTagline ? ' — ' + f.DjTagline : '');
            var avatar = dj.querySelector('.jellywave-dj-avatar');
            var client = api();
            var avatarUrl = f.DjAvatarId && client ? client.getUrl('Jellywave/Avatar/' + encodeURIComponent(f.DjAvatarId)) : '';
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
                }).catch(function () { avatar.setAttribute('data-failed', '1'); avatar.style.display = 'none'; });
            }
            avatar.style.display = avatarUrl && !avatar.getAttribute('data-failed') ? '' : 'none';
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
        metaEl.innerHTML = '';
        meta.forEach(function (m) { var d = document.createElement('div'); d.textContent = m; metaEl.appendChild(d); });
        var recent = h.querySelector('.jellywave-recent');
        if (f && f.Recent && f.Recent.length) {
            recent.style.display = '';
            var ol = recent.querySelector('ol');
            ol.innerHTML = '';
            f.Recent.forEach(function (line) { var li = document.createElement('li'); li.textContent = line; ol.appendChild(li); });
        } else {
            recent.style.display = 'none';
        }
        renderLike();
        fetchLyrics(false);
        var sched = h.querySelector('.jellywave-schedule');
        if (sched) { sched.style.display = ''; }
        var playing = isOurs();
        h.querySelector('.jellywave-play').style.display = playing ? 'none' : '';
        h.querySelector('.jellywave-stop').style.display = playing ? '' : 'none';
        h.querySelector('.jellywave-status').textContent = f && f.FetchedUtc ? '' : 'The server has not heard from the station yet.';
    }

    function tick() {
        fetchFeed().then(function () {
            if (state.header && !document.body.contains(state.header)) { state.header = null; }
            try { fetchLike(); } catch (e) { console.warn('[Jellywave] like status failed', e); }
            try { render(); } catch (e) { console.warn('[Jellywave] render failed', e); }
            try { fetchSchedule(); } catch (e) { console.warn('[Jellywave] schedule failed', e); }
            try { refreshBar(); } catch (e) { console.warn('[Jellywave] bar refresh failed', e); }
            try { refreshCards(); } catch (e) { console.warn('[Jellywave] card refresh failed', e); }
            if (!state.header && !isOurs()) { clearInterval(state.timer); state.timer = null; }
        });
    }

    function ensureTimer() {
        if (!state.timer) { state.timer = setInterval(tick, POLL_MS); }
    }

    /* The first list container in root whose data-parentid is the channel; cached views keep other
       libraries' containers in the document, so the first one found is not necessarily ours. */
    function findChannelContainer(root, channelId) {
        var found = root.querySelectorAll('.itemsContainer[data-parentid]');
        for (var i = 0; i < found.length; i++) {
            if (norm(found[i].getAttribute('data-parentid')) === norm(channelId)) { return found[i]; }
        }
        return null;
    }

    /* The channel's library page: .itemsContainer carries data-parentid = channel id (legacy list controller,
       used by both layouts for channels; it sets the attribute before the view's viewshow). Insert the header
       above the toolbar row of that page. root is the shown view, or the document. */
    function ensureHeader(root) {
        var f = state.feed;
        if (!f || !f.ChannelId) { return; }
        var container = findChannelContainer(root || document, f.ChannelId);
        if (!container) { return; }
        var page = container.closest('.page') || document;
        if (page.querySelector('.jellywave-header')) { return; }
        var anchor = page.querySelector('.itemsViewSettingsContainer') || container;
        var header = document.createElement('div');
        header.className = 'jellywave-header';
        header.innerHTML =
            '<div class="jellywave-cover"></div>' +
            '<div class="jellywave-body">' +
            '<p class="jellywave-title"><span class="jellywave-title-text"></span>' +
            '<button type="button" class="jellywave-like" title="Like this track" style="display:none"><span class="material-icons jellywave-like-icon" aria-hidden="true">favorite_border</span><span class="jellywave-like-count"></span></button></p>' +
            '<p class="jellywave-sub"></p>' +
            '<div class="jellywave-dj" style="display:none"><img class="jellywave-dj-avatar" alt="" /><div class="jellywave-dj-text"></div></div>' +
            '<div class="jellywave-meta"></div>' +
            '<div class="jellywave-actions">' +
            '<button is="emby-button" type="button" class="raised button-submit emby-button jellywave-play"><span class="material-icons play_arrow" aria-hidden="true"></span><span>Play</span></button>' +
            '<button is="emby-button" type="button" class="raised emby-button jellywave-stop"><span class="material-icons stop" aria-hidden="true"></span><span>Stop</span></button>' +
            '</div><div class="jellywave-status"></div></div>' +
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
        header.querySelector('.jellywave-play').addEventListener('click', function () { play(); setTimeout(render, 800); });
        header.querySelector('.jellywave-stop').addEventListener('click', function () { stop(); setTimeout(render, 300); });
        wireRequests(header);
        anchor.parentNode.insertBefore(header, anchor);
        state.header = header;
        render();
        ensureTimer();
    }

    /* Listener requests: POST Jellywave/Request, then poll Jellywave/Request/{id} until the station resolves it. */
    function wireRequests(header) {
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
            if (!document.body.contains(out)) { return; }   // page left; stop polling
            client.getJSON(client.getUrl('Jellywave/Request/' + encodeURIComponent(id))).then(function (r) {
                say(describe(r));
                if (r && r.Status === 'pending' && tries < 30) { pollTimer = setTimeout(function () { poll(id, tries + 1); }, 3000); }
                else { send.disabled = false; }
            }).catch(function (err) { say(isForbidden(err) ? NO_ACCESS_MESSAGE : 'Lost contact while waiting.'); send.disabled = false; });
        }
        form.addEventListener('submit', function (e) {
            e.preventDefault();
            var client2 = api();
            var value = text.value.trim();
            if (!client2 || !value) { return; }
            send.disabled = true;
            say('Sending…');
            client2.ajax({
                type: 'POST', url: client2.getUrl('Jellywave/Request'), dataType: 'json', contentType: 'application/json',
                data: JSON.stringify({ Text: value, Name: name.value.trim() })
            }).then(function (r) {
                say(describe(r));
                if (r && r.Accepted && r.RequestId) { text.value = ''; poll(r.RequestId, 0); } else { send.disabled = false; }
            }).catch(function (err) { say(isForbidden(err) ? NO_ACCESS_MESSAGE : 'Could not reach the server.'); send.disabled = false; });
        });
    }

    /* Legacy layout only: its drawer has a slot for custom links (.customMenuOptions); the modern layout has
       no user drawer and lists the channel in its top bar instead. The link opens the channel's library page. */
    function ensureDrawerLink() {
        var container = document.querySelector('.customMenuOptions');
        var f = state.feed;
        if (!container || !f || !f.ChannelId || container.querySelector('.jellywave-link')) { return; }
        var link = document.createElement('a');
        link.className = 'navMenuOption lnkMediaFolder jellywave-link';
        link.href = '#';
        link.innerHTML = '<span class="material-icons navMenuOptionIcon radio" aria-hidden="true"></span><span class="navMenuOptionText"></span>';
        link.querySelector('.navMenuOptionText').textContent = f.StationName || 'Radio';
        link.addEventListener('click', function (e) {
            e.preventDefault();
            var router = state.deps && state.deps.appRouter;
            var client = api();
            if (router && router.showItem && client) { router.showItem(String(f.ChannelId), client.serverId()); }
        });
        container.appendChild(link);
    }

    /* A view was shown, or the DOM settled: check the session, then add the header and the drawer link
       where their containers exist. The containers are checked through live collections first. */
    function onDomChange(root) {
        if (!checkSession()) { return; }
        if (!listContainers.length && !drawerSlots.length) { return; }
        if (!state.feed) {
            if (!state.fetching && (drawerSlots.length || document.querySelector('.itemsContainer[data-parentid]'))) {
                var epoch = state.epoch;
                state.fetching = true;
                fetchFeed().then(function () {
                    if (!isCurrent(epoch)) { return; }   // the reset cleared fetching; this answer belongs to the old session
                    state.fetching = false;
                    ensureHeader(root);
                    ensureDrawerLink();
                });
            }
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
        onDomChange(document);
        if (deps && deps.events && deps.playbackManager) {
            deps.events.on(deps.playbackManager, 'playbackstart', function () {
                fetchFeed().then(function () { if (isOurs()) { state.lastTag = null; ensureTimer(); tick(); } });
            });
            // jellyfin-web rebuilds the bar text and the media session from its snapshot on these events;
            // repaint right away instead of waiting for the next tick.
            ['pause', 'unpause', 'statechange', 'playbackstop'].forEach(function (name) {
                deps.events.on(deps.playbackManager, name, function () {
                    state.lastTag = null;
                    setTimeout(function () {
                        try { refreshBar(); } catch (e) { /* best effort */ }
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
