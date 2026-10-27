/* Jellywave station page: injected into jellyfin-web by the File Transformation plugin.
   Registered as a jellyfin-web client plugin (window.JellywaveUi, listed in config.json) so the
   plugin manager hands it the playback manager. Everything talks to the Jellyfin server only.
   What it does: (1) turns the channel's own library page into the station page by inserting a
   header above the item grid; (2) while the live item plays, repaints the playing bar's thumbnail
   and text, the item's card in any visible grid, and the browser media session. */
(function () {
    'use strict';

    var POLL_MS = 5000;
    var state = { feed: null, deps: null, timer: null, header: null, lastTag: null, lastCardTag: null };

    function api() { return window.ApiClient || null; }
    function norm(id) { return id ? String(id).replace(/-/g, '').toLowerCase() : ''; }

    function fetchFeed() {
        var client = api();
        if (!client || !client.accessToken || !client.accessToken()) { return Promise.resolve(null); }
        return client.getJSON(client.getUrl('Jellywave/Feed')).then(function (feed) {
            state.feed = feed;
            return feed;
        }).catch(function () { return null; });
    }

    function imageUrl(feed, size) {
        var client = api();
        if (!client || !feed || !feed.ItemId || !feed.ImageTag) { return null; }
        return client.getImageUrl(feed.ItemId, { type: 'Primary', tag: feed.ImageTag, maxWidth: size, maxHeight: size });
    }

    function isOurs() {
        var pm = state.deps && state.deps.playbackManager;
        if (!pm || !state.feed || !state.feed.ItemId) { return false; }
        var item = pm.currentItem ? pm.currentItem() : null;
        return !!(item && item.Id && norm(item.Id) === norm(state.feed.ItemId));
    }

    function play() {
        var pm = state.deps && state.deps.playbackManager;
        var client = api();
        if (!pm || !client || !state.feed || !state.feed.ItemId) { return; }
        pm.play({ ids: [state.feed.ItemId], serverId: client.serverId() });
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

    function render() {
        var h = state.header;
        var f = state.feed;
        if (!h) { return; }
        var url = imageUrl(f, 600);
        h.querySelector('.jellywave-cover').style.backgroundImage = url ? 'url("' + url + '")' : '';
        h.querySelector('.jellywave-title').textContent = f ? (f.Title || (f.Online === false ? 'Off air' : f.StationName)) : 'Loading…';
        h.querySelector('.jellywave-sub').textContent = f ? [f.Artist, f.Album].filter(Boolean).join(' · ') : '';
        var meta = [];
        if (f && f.Dj) { meta.push('DJ ' + f.Dj + (f.DjTagline ? ' — ' + f.DjTagline : '')); }
        if (f && f.Show) { meta.push('On air: ' + f.Show); }
        if (f && f.NextShow) { meta.push('Up next: ' + f.NextShow + (f.NextShowPresenter ? ' with ' + f.NextShowPresenter : '') + (f.NextShowStartsAt ? ' at ' + f.NextShowStartsAt : '')); }
        if (f && typeof f.Listeners === 'number') { meta.push(f.Listeners + ' listening'); }
        var metaEl = h.querySelector('.jellywave-meta');
        metaEl.innerHTML = '';
        meta.forEach(function (m) { var d = document.createElement('div'); d.textContent = m; metaEl.appendChild(d); });
        var playing = isOurs();
        h.querySelector('.jellywave-play').style.display = playing ? 'none' : '';
        h.querySelector('.jellywave-stop').style.display = playing ? '' : 'none';
        h.querySelector('.jellywave-status').textContent = f && f.FetchedUtc ? '' : 'The server has not heard from the station yet.';
    }

    function tick() {
        fetchFeed().then(function () {
            if (state.header && !document.body.contains(state.header)) { state.header = null; }
            render();
            refreshBar();
            refreshCards();
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
            '<p class="jellywave-title"></p><p class="jellywave-sub"></p><div class="jellywave-meta"></div>' +
            '<div class="jellywave-actions">' +
            '<button is="emby-button" type="button" class="raised button-submit emby-button jellywave-play"><span class="material-icons play_arrow" aria-hidden="true"></span><span>Play</span></button>' +
            '<button is="emby-button" type="button" class="raised emby-button jellywave-stop"><span class="material-icons stop" aria-hidden="true"></span><span>Stop</span></button>' +
            '</div><div class="jellywave-status"></div></div>';
        header.querySelector('.jellywave-play').addEventListener('click', function () { play(); setTimeout(render, 800); });
        header.querySelector('.jellywave-stop').addEventListener('click', function () { stop(); setTimeout(render, 300); });
        anchor.parentNode.insertBefore(header, anchor);
        state.header = header;
        render();
        ensureTimer();
    }

    function onDomChange() {
        if (!state.feed) {
            if (document.querySelector('.itemsContainer[data-parentid]') && !state.fetching) {
                state.fetching = true;
                fetchFeed().then(function () { state.fetching = false; ensureHeader(); });
            }
            return;
        }
        ensureHeader();
    }

    function boot(deps) {
        state.deps = deps;
        new MutationObserver(onDomChange).observe(document.body, { childList: true, subtree: true });
        onDomChange();
        if (deps && deps.events && deps.playbackManager) {
            deps.events.on(deps.playbackManager, 'playbackstart', function () {
                fetchFeed().then(function () { if (isOurs()) { state.lastTag = null; ensureTimer(); tick(); } });
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
