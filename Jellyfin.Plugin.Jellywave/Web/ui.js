/* Jellywave station page: injected into jellyfin-web by the File Transformation plugin.
   Registered as a jellyfin-web client plugin (window.JellywaveUi, listed in config.json) so the
   plugin manager hands it the playback manager. Everything talks to the Jellyfin server only. */
(function () {
    'use strict';

    var POLL_MS = 5000;
    var state = { feed: null, deps: null, timer: null, panel: null, lastTag: null };

    function api() { return window.ApiClient || null; }

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
        return !!(item && item.Id && item.Id.replace(/-/g, '') === String(state.feed.ItemId).replace(/-/g, ''));
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
        var img = document.querySelector('.nowPlayingBar .nowPlayingImage');
        var url = imageUrl(state.feed, 140);
        if (img && url && state.feed.ImageTag !== state.lastTag) {
            img.style.backgroundImage = 'url("' + url + '")';
            state.lastTag = state.feed.ImageTag;
        }
        var text = document.querySelector('.nowPlayingBar .nowPlayingBarText');
        if (text) {
            var lines = text.querySelectorAll('a');
            if (lines[0] && state.feed.TrackLine) { lines[0].innerText = state.feed.TrackLine; }
            if (lines[1]) { lines[1].innerText = state.feed.StationName + (state.feed.Show ? ' · ' + state.feed.Show : ''); }
        }
        try {
            if ('mediaSession' in navigator && window.MediaMetadata) {
                navigator.mediaSession.metadata = new window.MediaMetadata({
                    title: state.feed.Title || state.feed.StationName,
                    artist: state.feed.Artist || state.feed.StationName,
                    album: state.feed.Album || state.feed.Show || '',
                    artwork: url ? [{ src: imageUrl(state.feed, 512), sizes: '512x512' }] : []
                });
            }
        } catch (e) { /* not supported here */ }
    }

    function render() {
        var p = state.panel;
        var f = state.feed;
        if (!p) { return; }
        var cover = p.querySelector('.jellywave-cover');
        var url = imageUrl(f, 600);
        cover.style.backgroundImage = url ? 'url("' + url + '")' : '';
        p.querySelector('.jellywave-title').textContent = f ? (f.Title || (f.Online === false ? 'Off air' : f.StationName)) : 'Loading…';
        p.querySelector('.jellywave-sub').textContent = f ? [f.Artist, f.Album].filter(Boolean).join(' · ') : '';
        var meta = [];
        if (f && f.Dj) { meta.push('DJ ' + f.Dj + (f.DjTagline ? ' — ' + f.DjTagline : '')); }
        if (f && f.Show) { meta.push('On air: ' + f.Show); }
        if (f && f.NextShow) { meta.push('Up next: ' + f.NextShow + (f.NextShowPresenter ? ' with ' + f.NextShowPresenter : '') + (f.NextShowStartsAt ? ' at ' + f.NextShowStartsAt : '')); }
        if (f && typeof f.Listeners === 'number') { meta.push(f.Listeners + ' listening'); }
        p.querySelector('.jellywave-meta').innerHTML = meta.map(function (m) { return '<div></div>'; }).join('');
        var metaDivs = p.querySelector('.jellywave-meta').children;
        meta.forEach(function (m, i) { metaDivs[i].textContent = m; });
        var playing = isOurs();
        p.querySelector('.jellywave-play').style.display = playing ? 'none' : '';
        p.querySelector('.jellywave-stop').style.display = playing ? '' : 'none';
        p.querySelector('.jellywave-status').textContent = f && f.FetchedUtc ? '' : 'The server has not heard from the station yet.';
    }

    function tick() {
        fetchFeed().then(function () {
            render();
            refreshBar();
            if (!state.panel && !isOurs()) { clearInterval(state.timer); state.timer = null; }
        });
    }

    function ensureTimer() {
        if (!state.timer) { state.timer = setInterval(tick, POLL_MS); }
    }

    function closePanel() {
        if (state.panel) { state.panel.remove(); state.panel = null; }
    }

    function openPanel() {
        closePanel();
        var panel = document.createElement('div');
        panel.className = 'jellywave-panel';
        panel.innerHTML =
            '<div class="jellywave-card" role="dialog">' +
            '<button type="button" class="jellywave-close" aria-label="Close">×</button>' +
            '<div class="jellywave-cover"></div>' +
            '<p class="jellywave-title"></p><p class="jellywave-sub"></p><div class="jellywave-meta"></div>' +
            '<div class="jellywave-actions">' +
            '<button is="emby-button" type="button" class="raised button-submit emby-button jellywave-play"><span>Play</span></button>' +
            '<button is="emby-button" type="button" class="raised emby-button jellywave-stop"><span>Stop</span></button>' +
            '</div><div class="jellywave-status"></div></div>';
        panel.addEventListener('click', function (e) { if (e.target === panel) { closePanel(); } });
        panel.querySelector('.jellywave-close').addEventListener('click', closePanel);
        panel.querySelector('.jellywave-play').addEventListener('click', function () { play(); setTimeout(render, 800); });
        panel.querySelector('.jellywave-stop').addEventListener('click', function () { stop(); setTimeout(render, 300); });
        document.body.appendChild(panel);
        state.panel = panel;
        render();
        tick();
        ensureTimer();
    }

    function ensureDrawerLink() {
        var container = document.querySelector('.customMenuOptions');
        if (!container || container.querySelector('.jellywave-link')) { return; }
        var link = document.createElement('a');
        link.className = 'navMenuOption lnkMediaFolder jellywave-link';
        link.href = '#';
        link.innerHTML = '<span class="material-icons navMenuOptionIcon radio" aria-hidden="true"></span><span class="navMenuOptionText"></span>';
        link.querySelector('.navMenuOptionText').textContent = (state.feed && state.feed.StationName) || 'Radio';
        link.addEventListener('click', function (e) {
            e.preventDefault();
            var mask = document.querySelector('.navdrawer-mask, .mainDrawer-mask');
            if (mask) { mask.click(); }
            openPanel();
        });
        container.appendChild(link);
        if (!state.feed) { fetchFeed().then(function (f) { if (f) { link.querySelector('.navMenuOptionText').textContent = f.StationName; } }); }
    }

    function boot(deps) {
        state.deps = deps;
        new MutationObserver(ensureDrawerLink).observe(document.body, { childList: true, subtree: true });
        ensureDrawerLink();
        if (deps && deps.events && deps.playbackManager) {
            deps.events.on(deps.playbackManager, 'playbackstart', function () { fetchFeed().then(function () { if (isOurs()) { ensureTimer(); } }); });
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
