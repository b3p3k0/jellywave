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
    var state = { feed: null, deps: null, timer: null, header: null, lastTag: null, lastCardTag: null, feedFailedAt: 0, feedRetryMs: FEED_RETRY_MS, like: null, likeSongId: null, likeBusy: false, lyrics: null, lyricsSongId: null, schedule: null, scheduleAt: 0 };

    function api() { return window.ApiClient || null; }
    function norm(id) { return id ? String(id).replace(/-/g, '').toLowerCase() : ''; }

    function fetchFeed() {
        var client = api();
        if (!client || !client.accessToken || !client.accessToken()) { return Promise.resolve(null); }
        if (state.feedFailedAt && Date.now() - state.feedFailedAt < state.feedRetryMs) { return Promise.resolve(null); }
        return client.getJSON(client.getUrl('Jellywave/Feed')).then(function (feed) {
            state.feed = feed;
            state.feedFailedAt = 0;
            return feed;
        }).catch(function (err) {
            state.feedFailedAt = Date.now();
            state.feedRetryMs = FEED_RETRY_MS;
            // ApiClient rejects with the fetch Response on an HTTP error; a network failure has no status.
            if (err && err.status === HTTP_FORBIDDEN) {
                state.feedRetryMs = FEED_FORBIDDEN_RETRY_MS;
                forgetStation();
            }
            return null;
        });
    }

    /* Refused by the server: drop what an earlier answer showed, so a blocked user sees nothing. */
    function forgetStation() {
        state.feed = null;
        if (state.header && state.header.parentNode) { state.header.parentNode.removeChild(state.header); }
        state.header = null;
        var link = document.querySelector('.jellywave-link');
        if (link && link.parentNode) { link.parentNode.removeChild(link); }
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
    function fetchLike() {
        var client = api();
        if (!client || !state.feed || !state.feed.SongId) { state.like = null; return; }
        if (state.likeSongId === state.feed.SongId) { return; }
        state.likeSongId = state.feed.SongId;
        client.getJSON(client.getUrl('Jellywave/Like')).then(function (r) { state.like = r; renderLike(); }).catch(function () { state.like = null; state.likeSongId = null; });
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
        state.likeBusy = true;
        renderLike();
        client.ajax({
            type: 'POST', url: client.getUrl('Jellywave/Like'), dataType: 'json', contentType: 'application/json',
            data: JSON.stringify({ SongId: state.like.SongId })
        }).then(function (r) {
            if (!r || !r.Enabled) { return; }
            if (r.Message && !r.SongId) { state.like.Message = r.Message; return; }   // refusal: keep heart and count
            state.like = r;
        }).catch(function () { /* keep the old status */ }).then(function () { state.likeBusy = false; renderLike(); });
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
        state.lyricsSongId = songId;
        state.lyrics = null;
        renderLyrics();
        client.getJSON(client.getUrl('Jellywave/Lyrics', { songId: songId })).then(function (r) {
            if (state.lyricsSongId !== songId) { return; }
            state.lyrics = r;
            renderLyrics();
            if (r && r.Status === 'pending') { setTimeout(function () { if (state.lyricsSongId === songId) { state.lyricsSongId = null; fetchLyrics(); } }, 8000); }
        }).catch(function () { state.lyrics = { Status: 'unavailable', Message: 'Could not reach the server.' }; renderLyrics(); });
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
        client.getJSON(client.getUrl('Jellywave/Schedule')).then(function (r) {
            state.schedule = r;
            state.scheduleAt = Date.now();
            renderSchedule();
        }).catch(function () { state.schedule = { Available: false }; renderSchedule(); });
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
                // The endpoint needs the session token, which an <img> cannot send; fetch with it and show the bytes.
                client.ajax({ type: 'GET', url: avatarUrl }).then(function (r) {
                    // ApiClient.ajax resolves the raw Response unless dataType is json/text.
                    return r && typeof r.blob === 'function' ? r.blob() : r;
                }).then(function (blob) {
                    if (!blob || avatar.getAttribute('data-src') !== avatarUrl) { return; }
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

    /* The channel's library page: .itemsContainer carries data-parentid = channel id (legacy list controller,
       used by both layouts for channels). Insert the header above the toolbar row of that page. */
    function ensureHeader() {
        var f = state.feed;
        if (!f || !f.ChannelId) { return; }
        var container = document.querySelector('.itemsContainer[data-parentid]');
        if (!container || norm(container.getAttribute('data-parentid')) !== norm(f.ChannelId)) { return; }
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
            }).catch(function () { say('Lost contact while waiting.'); send.disabled = false; });
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
            }).catch(function () { say('Could not reach the server.'); send.disabled = false; });
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

    function onDomChange() {
        if (!state.feed) {
            if ((document.querySelector('.itemsContainer[data-parentid]') || document.querySelector('.customMenuOptions')) && !state.fetching) {
                state.fetching = true;
                fetchFeed().then(function () { state.fetching = false; ensureHeader(); ensureDrawerLink(); });
            }
            return;
        }
        ensureHeader();
        ensureDrawerLink();
    }

    function boot(deps) {
        state.deps = deps;
        new MutationObserver(onDomChange).observe(document.body, { childList: true, subtree: true });
        onDomChange();
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
