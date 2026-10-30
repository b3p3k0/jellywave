// Page tests for Web/ui.js, the station page (cards 18, L5, L5b, L8, L6 and L14). Plain node, no dependencies:
//   node Jellyfin.Plugin.Jellywave.Tests/js/stationPage.test.js [path/to/ui.js]
// Runs the script against a small fake DOM (a parser for the header's fixed markup and a selector engine for the
// selectors the script uses), a fake ApiClient whose requests stay pending until a scenario answers them, a fake
// playbackManager and events, fake timers and a fake clock. Exits 1 when any check fails.
'use strict';
const fs = require('fs');
const path = require('path');

const scriptPath = process.argv[2] || path.join(__dirname, '..', '..', 'Jellyfin.Plugin.Jellywave', 'Web', 'ui.js');
const source = fs.readFileSync(scriptPath, 'utf8');
const realSetImmediate = setImmediate;

let failures = 0;
let passes = 0;
function check(condition, name) {
  if (condition) { passes++; console.log('ok   ' + name); } else { failures++; console.log('FAIL ' + name); }
}

// ---------------------------------------------------------------- fake DOM
const VOID = new Set(['img', 'input', 'br']);

function parseSelector(group) {
  return group.split(',').map(s => s.trim()).filter(Boolean).map(complex => complex.split(/\s+/).map(compound => {
    const m = compound.match(/^([a-zA-Z][\w-]*)?((?:\.[\w-]+)*)((?:\[[^\]]+\])*)$/);
    if (!m) { throw new Error('unsupported selector ' + group); }
    const classes = (m[2] || '').split('.').filter(Boolean);
    const attrs = [...(m[3] || '').matchAll(/\[([\w-]+)(?:="([^"]*)")?\]/g)].map(a => [a[1], a[2]]);
    return { tag: m[1] ? m[1].toLowerCase() : null, classes, attrs };
  }));
}

function matchCompound(el, c) {
  if (!el || el.tagName === '#text' || el.tagName === '#document') { return false; }
  if (c.tag && el.tagName !== c.tag) { return false; }
  if (!c.classes.every(k => el.classList.contains(k))) { return false; }
  return c.attrs.every(([k, v]) => el.getAttribute(k) !== null && (v === undefined || el.getAttribute(k) === v));
}

function matchComplex(el, parts) {
  if (!matchCompound(el, parts[parts.length - 1])) { return false; }
  let i = parts.length - 2;
  let node = el.parentNode;
  while (i >= 0 && node) { if (matchCompound(node, parts[i])) { i--; } node = node.parentNode; }
  return i < 0;
}

class Node {
  constructor(env, tag) {
    this.env = env; this.tagName = tag; this.children = []; this.parentNode = null; this.attrs = {}; this.style = {};
    this.listeners = {}; this._text = ''; this.disabled = false; this.open = false; this.title = ''; this.src = ''; this.href = '';
    if (tag !== 'select') { this.value = ''; }
    const self = this;
    this.classList = {
      contains: k => (self.attrs.class || '').split(/\s+/).includes(k),
      add: k => { if (!self.classList.contains(k)) { self.attrs.class = ((self.attrs.class || '') + ' ' + k).trim(); } },
      remove: k => { self.attrs.class = (self.attrs.class || '').split(/\s+/).filter(x => x && x !== k).join(' '); }
    };
  }
  get className() { return this.attrs.class || ''; }
  set className(v) { this.attrs.class = String(v); }
  getAttribute(k) { return Object.prototype.hasOwnProperty.call(this.attrs, k) ? this.attrs[k] : null; }
  setAttribute(k, v) { this.attrs[k] = String(v); }
  removeAttribute(k) { delete this.attrs[k]; if (k === 'src') { this.src = ''; } }
  get textContent() { return this.tagName === '#text' ? this._text : this.children.map(c => c.textContent).join(''); }
  set textContent(v) { this.children.forEach(c => { c.parentNode = null; }); this.children = []; if (String(v) !== '') { this.appendChild(this.env.text(String(v))); } }
  get innerText() { return this.textContent; }
  set innerText(v) { this.textContent = v; }
  set innerHTML(v) {
    this.env.innerHTMLs.push(String(v));
    this.textContent = '';
    parseInto(this, String(v));
  }
  get innerHTML() { throw new Error('innerHTML read'); }
  get firstChild() { return this.children[0] || null; }
  get options() { return this.children.filter(c => c.tagName === 'option'); }
  get value() {
    if (this.tagName !== 'select') { return this._value; }
    const opts = this.options;
    if (this._value === undefined) { return opts[0] ? opts[0].value : ''; }
    return opts.some(o => o.value === this._value) ? this._value : '';
  }
  set value(v) { this._value = String(v); }
  appendChild(c) { if (c.parentNode) { c.parentNode.removeChild(c); } this.children.push(c); c.parentNode = this; this.env.mutated(); return c; }
  insertBefore(c, ref) {
    if (c.parentNode) { c.parentNode.removeChild(c); }
    const i = ref ? this.children.indexOf(ref) : -1;
    if (i < 0) { this.children.push(c); } else { this.children.splice(i, 0, c); }
    c.parentNode = this; this.env.mutated(); return c;
  }
  removeChild(c) { const i = this.children.indexOf(c); if (i >= 0) { this.children.splice(i, 1); c.parentNode = null; this.env.mutated(); } return c; }
  contains(n) { while (n) { if (n === this) { return true; } n = n.parentNode; } return false; }
  descendants() { return this.children.flatMap(c => [c, ...c.descendants()]); }
  querySelectorAll(sel) { const groups = parseSelector(sel); return this.descendants().filter(el => groups.some(g => matchComplex(el, g))); }
  querySelector(sel) { return this.querySelectorAll(sel)[0] || null; }
  closest(sel) { const groups = parseSelector(sel); let n = this; while (n) { if (groups.some(g => matchComplex(n, g))) { return n; } n = n.parentNode; } return null; }
  addEventListener(t, f) { (this.listeners[t] = this.listeners[t] || []).push(f); }
  dispatch(t, e) { const ev = Object.assign({ target: this, preventDefault() {} }, e || {}); (this.listeners[t] || []).slice().forEach(f => f(ev)); }
  click() { if (!this.disabled) { this.dispatch('click'); } }
  focus() { this.env.focused = this; }
}

function parseInto(root, html) {
  const stack = [root];
  for (const m of html.matchAll(/<\/([a-zA-Z][\w-]*)\s*>|<([a-zA-Z][\w-]*)((?:\s+[\w-]+(?:="[^"]*")?)*)\s*(\/?)>|([^<]+)/g)) {
    const top = stack[stack.length - 1];
    if (m[1]) {
      const tag = m[1].toLowerCase();
      while (stack.length > 1 && stack.pop().tagName !== tag) { /* unwind */ }
    } else if (m[2]) {
      const el = root.env.create(m[2].toLowerCase());
      for (const a of (m[3] || '').matchAll(/([\w-]+)(?:="([^"]*)")?/g)) {
        const value = a[2] === undefined ? '' : a[2];
        if (a[1] === 'style') {
          value.split(';').filter(Boolean).forEach(d => { const [k, v] = d.split(':'); el.style[k.trim().replace(/-([a-z])/g, (_, c) => c.toUpperCase())] = v.trim(); });
        } else { el.attrs[a[1]] = value; }
      }
      top.appendChild(el);
      if (!m[4] && !VOID.has(el.tagName)) { stack.push(el); }
    } else if (m[5]) {
      top.appendChild(root.env.text(m[5]));
    }
  }
}

// ---------------------------------------------------------------- environment
const settle = async () => { for (let i = 0; i < 10; i++) { await new Promise(r => realSetImmediate(r)); } };

const baseModel = () => ({
  channelId: 'chan-1',
  channelName: 'Live Radio',
  stations: [
    { Id: 'live', Name: 'Live One', Kind: 'SubWave', ItemId: 'item-live', ImageTag: 'tl' },
    { Id: 'two', Name: 'Station Two', Kind: 'SubWave', ItemId: 'item-two', ImageTag: 'tt' }
  ],
  missingFeeds: new Set()
});

function feedOf(model, id) {
  const st = model.stations.find(s => s.Id === id);
  // Card L14: a station in model.unheard is one the server has not asked yet (idle until this page woke it).
  if (model.unheard && model.unheard.has(id)) {
    return { StationId: id, StationName: st ? st.Name : id, ChannelId: model.channelId, ItemId: st ? st.ItemId : null, ImageTag: 'cover-' + id, Recent: [], LyricsEnabled: false, FetchedUtc: null };
  }
  return {
    StationId: id, StationName: st ? st.Name : id, ChannelId: model.channelId, ItemId: st ? st.ItemId : null, ImageTag: 'cover-' + id,
    Title: 'Title ' + id, Artist: 'Artist ' + id, Album: 'Album ' + id, TrackLine: 'Artist ' + id + ' – Title ' + id, SongId: (model.songs && model.songs[id]) || 'song-' + id,
    Dj: (model.djs && model.djs[id]) || 'DJ ' + id, DjAvatarId: (model.avatars && model.avatars[id]) || 'dj_' + id, Show: 'Show ' + id, Recent: model.noRecent ? [] : ['Old ' + id], LyricsEnabled: !model.lyricsOff,
    FetchedUtc: '2026-10-27T00:00:00Z', Online: true
  };
}

async function open(options) {
  const opts = options || {};
  const env = {
    now: 1000000, timers: [], timerSeq: 0, requests: [], innerHTMLs: [], warnings: [], events: {}, observers: [], revoked: [],
    focused: null, blobs: 0, mutations: 0, model: opts.model || baseModel(),
    server: 'srv', user: opts.user || 'user-a', token: 'tok', storageUses: 0, observing: true
  };
  // observing = false: the script's MutationObserver hears nothing (not even the script's own DOM changes), so a
  // scenario proves a path that does not lean on the DOM-change fallback.
  env.mutated = () => { env.mutations++; if (env.observing) { env.observers.forEach(cb => cb([])); } };
  env.create = tag => new Node(env, tag);
  env.text = t => { const n = new Node(env, '#text'); n._text = t; return n; };
  const make = (tag, cls, attrs) => { const el = env.create(tag); if (cls) { el.className = cls; } Object.assign(el.attrs, attrs || {}); return el; };

  const document = new Node(env, '#document');
  const head = make('head');
  const body = make('body');
  document.appendChild(head);
  document.appendChild(body);
  document.head = head;
  document.body = body;
  document.createElement = tag => env.create(String(tag).toLowerCase());
  document.getElementsByClassName = name => ({ get length() { return document.querySelectorAll('.' + name).length; } });
  env.document = document;

  // The channel's library page (shown), another library's cached page (hidden), and the playing bar.
  const view = make('div', 'mainAnimatedPage page libraryPage');
  view.appendChild(make('div', 'itemsViewSettingsContainer'));
  const grid = make('div', 'itemsContainer', { 'data-parentid': env.model.channelId });
  ['item-live', 'item-two', 'item-plain'].forEach(id => {
    const card = make('div', 'card', { 'data-id': id });
    const image = make('div', 'cardImageContainer');
    image.style.backgroundImage = 'url("original-' + id + '")';
    card.appendChild(image);
    grid.appendChild(card);
  });
  view.appendChild(grid);
  const cached = make('div', 'mainAnimatedPage page hide');
  cached.appendChild(make('div', 'itemsContainer', { 'data-parentid': 'other-library' }));
  const bar = make('div', 'nowPlayingBar');
  const barImage = make('div', 'nowPlayingImage');
  const barText = make('div', 'nowPlayingBarText');
  barText.appendChild(make('a'));
  barText.appendChild(make('a'));
  bar.appendChild(barImage);
  bar.appendChild(barText);
  // jellyfin-web 12.3's bar groups (14245.*.chunk.js): the centre with play/pause and Stop, the right with play/pause.
  const barCenter = make('div', 'nowPlayingBarCenter');
  ['previousTrackButton', 'playPauseButton', 'stopButton', 'nextTrackButton'].forEach(c => barCenter.appendChild(make('button', c + ' mediaButton')));
  const barRight = make('div', 'nowPlayingBarRight');
  ['muteButton', 'playPauseButton', 'nextTrackButton'].forEach(c => barRight.appendChild(make('button', c + ' mediaButton')));
  bar.appendChild(barCenter);
  bar.appendChild(barRight);
  if (opts.drawer) { body.appendChild(make('div', 'customMenuOptions')); }
  if (!opts.noView) { body.appendChild(view); }
  body.appendChild(cached);
  body.appendChild(bar);
  Object.assign(env, { view, grid, bar, barImage, barText, barCenter, barRight });
  // Another view of the tile's library page (jellyfin-web keeps a cached copy of a view; hidden ones carry .hide).
  env.tileView = hidden => {
    const v = make('div', 'mainAnimatedPage page libraryPage' + (hidden ? ' hide' : ''));
    v.appendChild(make('div', 'itemsViewSettingsContainer'));
    v.appendChild(make('div', 'itemsContainer', { 'data-parentid': env.model.channelId }));
    return v;
  };

  // Fake clock and timers (the script's own; the harness waits with the real setImmediate).
  Date.now = () => env.now;
  const addTimer = (fn, ms, every) => { const id = ++env.timerSeq; env.timers.push({ id, at: env.now + (ms || 0), fn, every }); return id; };
  const clear = id => { env.timers = env.timers.filter(t => t.id !== id); };
  env.advance = async ms => {
    const end = env.now + ms;
    for (;;) {
      const due = env.timers.filter(t => t.at <= end).sort((a, b) => a.at - b.at || a.id - b.id)[0];
      if (!due) { break; }
      env.now = Math.max(env.now, due.at);
      if (due.every) { due.at += due.every; } else { clear(due.id); }
      due.fn();
      await settle();
    }
    env.now = end;
    await settle();
  };

  // Fake ApiClient: every request stays pending until the scenario answers it.
  const query = params => {
    const keys = Object.keys(params || {}).filter(k => params[k] !== null && params[k] !== undefined && params[k] !== '');
    return keys.length ? '?' + keys.map(k => k + '=' + encodeURIComponent(params[k])).join('&') : '';
  };
  const request = (method, url, data) => new Promise((resolve, reject) => env.requests.push({ method, url, data: data ? JSON.parse(data) : null, resolve, reject, done: false }));
  const client = {
    serverId: () => env.server,
    getCurrentUserId: () => env.user,
    accessToken: () => env.token,
    getUrl: (p, params) => p + query(params),
    getJSON: url => request('GET', url),
    ajax: o => request(o.type, o.url, o.data),
    getImageUrl: (id, o) => 'img/' + id + '?tag=' + o.tag + '&size=' + o.maxWidth,
    getCurrentUser: () => Promise.resolve({ Name: 'kevin' })
  };
  // jellyfin-web 12.3: after a stop the player stays current and currentItem may linger, but isPlaying(player) is
  // false; a pause keeps isPlaying(player) true (the HTML players' currentSrc() survives a pause) and paused() true.
  // A stop fires playbackstop first and clears afterwards (the media helper triggers "stopped" before it clears
  // _currentSrc; playbackManager fires playbackstop before it drops the stream info and player), so a listener that
  // looked at once would still see the item playing.
  const pm = {
    current: opts.playing || null, isPlayingNow: true, pausedNow: false, plays: [], stops: 0, stoppedPlayers: [], player: { name: 'html' },
    getCurrentPlayer() { return this.current ? this.player : null; },
    isPlaying() { return this.isPlayingNow; },
    paused() { return this.pausedNow; },
    currentItem() { return this.current ? { Id: this.current } : null; },
    play(o) { this.plays.push(o); return Promise.resolve(); },
    stop(player) { this.stops++; this.stoppedPlayers.push(player); (env.events.playbackstop || []).forEach(f => f()); this.isPlayingNow = false; this.pausedNow = false; }
  };
  // Events by target: env.events holds the playbackManager's (by name), env.targetHandlers everything else (players).
  // jellyfin-web 12.3's playbackManager fires playbackstart, playbackstop and playerchange itself, but a player's pause
  // only on the player (it turns it into a progress report), so a pause is fired on the player (env.pause).
  env.targetHandlers = [];
  const events = {
    on(target, name, fn) {
      if (target === pm) { (env.events[name] = env.events[name] || []).push(fn); } else { env.targetHandlers.push({ target, name, fn }); }
    },
    off(target, name, fn) {
      if (target === pm) { env.events[name] = (env.events[name] || []).filter(f => f !== fn); }
      else { env.targetHandlers = env.targetHandlers.filter(h => !(h.target === target && h.name === name && h.fn === fn)); }
    }
  };
  env.fireOn = (target, name) => env.targetHandlers.filter(h => h.target === target && h.name === name).forEach(h => h.fn({ type: name }));
  const window = {
    ApiClient: client,
    MediaMetadata: class { constructor(o) { Object.assign(this, o); } }
  };
  Object.defineProperty(window, 'localStorage', { get() { env.storageUses++; return { getItem: () => null, setItem: () => {} }; } });
  const navigator = { mediaSession: { metadata: null } };
  const URLs = { createObjectURL: () => 'blob:' + (++env.blobs), revokeObjectURL: u => env.revoked.push(u) };
  class MutationObserver { constructor(cb) { env.observers.push(cb); } observe() {} }
  const consoleProxy = { warn: (...a) => env.warnings.push(a.map(String).join(' ')), log: () => {}, error: (...a) => env.warnings.push(a.map(String).join(' ')) };
  Object.assign(env, { client, pm, window, navigator, events });

  new Function('window', 'document', 'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'navigator', 'MutationObserver', 'URL', 'console', source)(   // eslint-disable-line no-new-func
    window, document, (fn, ms) => addTimer(fn, ms), clear, (fn, ms) => addTimer(fn, ms, ms), clear, navigator, MutationObserver, URLs, consoleProxy);
  env.boot = async () => { const Plugin = await window.JellywaveUi(); env.plugin = new Plugin({ playbackManager: pm, events, appRouter: { showItem() {} } }); await settle(); };
  if (!opts.noBoot) { await env.boot(); }

  // Scenario helpers.
  env.pending = prefix => env.requests.filter(r => !r.done && (!prefix || r.url.startsWith(prefix)));
  env.urls = () => env.requests.map(r => r.method + ' ' + r.url);
  env.answer = async (prefix, body) => {
    const r = env.pending(prefix)[0];
    if (!r) { throw new Error('no pending request for ' + prefix + ' in ' + JSON.stringify(env.urls())); }
    r.done = true; r.resolve(body); await settle(); return r;
  };
  // ApiClient rejects with the fetch Response on an HTTP error (jellyfin-apiclient: Promise.reject(response) when
  // status >= 400) and with the fetch error on a network failure, which has no status. headers: the Response's headers
  // by name (card R2b: Retry-After), read case-insensitively through headers.get as on a real Response.
  env.response = (status, headers) => ({
    status, ok: false,
    headers: { get: name => { const k = Object.keys(headers || {}).find(h => h.toLowerCase() === String(name).toLowerCase()); return k === undefined ? null : String(headers[k]); } }
  });
  env.fail = async (prefix, status, headers) => {
    const r = env.pending(prefix)[0];
    if (!r) { throw new Error('no pending request for ' + prefix); }
    r.done = true; r.reject(status ? env.response(status, headers) : new TypeError('network')); await settle(); return r;
  };
  // Answers every pending request from the model, until nothing is pending (or only what `keep` names).
  env.serve = async keep => {
    for (let round = 0; round < 20; round++) {
      const open = env.pending().filter(r => !(keep && keep(r)));
      if (!open.length) { return; }
      for (const r of open) {
        r.done = true;
        const u = new URL(r.url, 'http://x/');
        const sid = u.searchParams.get('stationId');
        const m = env.model;
        const subWave = id => m.stations.some(s => s.Id === id && s.Kind !== 'Stream');
        // Card L6: the title route answers for a listed plain stream only (404 otherwise), from model.titles.
        if (u.pathname === '/Jellywave/Title') {
          const st = m.stations.find(s => s.Id === sid);
          if (!st || st.Kind !== 'Stream') { r.reject({ status: 404 }); }
          else if (m.titleFails) { r.reject(m.titleFails === 'network' ? new TypeError('network') : { status: m.titleFails }); }
          else { r.resolve(Object.assign({ StationId: sid, State: 'Waiting', Title: null }, (m.titles || {})[sid] || {})); }
          continue;
        }
        // The SUB/WAVE station routes refuse a plain stream's id (404), as StationResolver does (card L8).
        if (u.pathname !== '/Jellywave/Stations' && sid !== null && !subWave(sid)) { r.reject({ status: 404 }); continue; }
        if (u.pathname === '/Jellywave/Stations') { r.resolve({ ChannelId: m.channelId, ChannelName: m.channelName, Stations: m.stations }); }
        else if (u.pathname === '/Jellywave/Feed') {
          if (!subWave(sid) || m.missingFeeds.has(sid)) { r.reject({ status: 404 }); } else { r.resolve(feedOf(m, sid)); }
        } else if (u.pathname === '/Jellywave/Like' && r.method === 'GET') { r.resolve(m.likeOff ? { Enabled: false } : { Enabled: true, SongId: feedOf(m, sid).SongId, Liked: false, Count: 1 }); }
        else if (u.pathname === '/Jellywave/Like') { r.resolve({ Enabled: true, SongId: r.data.SongId, Liked: true, Count: 2 }); }
        else if (u.pathname === '/Jellywave/Schedule') {
          if (m.scheduleFails) { r.reject({ status: 500 }); }
          else { r.resolve(m.noSchedule ? { Available: false } : { Available: true, Day: 'Monday', Timezone: 'UTC', Today: [{ StartHour: 0, EndHour: 24, Show: 'Grid ' + sid, Now: true }] }); }
        }
        else if (u.pathname === '/Jellywave/Lyrics') { r.resolve(m.lyricsPending ? { Status: 'pending', Message: 'Still looking…' } : { Status: 'ok', Lines: ['words of ' + sid] }); }
        else if (u.pathname.startsWith('/Jellywave/Avatar/')) { r.resolve({ blob: () => Promise.resolve({ size: 1 }) }); }
        else if (u.pathname === '/Jellywave/Request' && r.method === 'POST') { r.resolve(m.requestAnswer || { Accepted: true, RequestId: 'req-' + (r.data.StationId || 'primary'), Status: 'pending', Message: 'Sent.' }); }
        else if (u.pathname.startsWith('/Jellywave/Request/')) { r.resolve(m.requestPending ? { Status: 'pending' } : { Status: 'resolved', Ack: 'ack for ' + u.pathname.split('/').pop() }); }
        else { r.reject({ status: 500 }); }
      }
      await settle();
    }
  };
  env.headers = () => document.querySelectorAll('.jellywave-header');
  env.header = () => env.headers()[0] || null;
  env.$ = sel => (env.header() ? env.header().querySelector(sel) : null);
  env.shown = el => !!el && el.style.display !== 'none';
  // Playback as jellyfin-web reports it: the item becomes current, then playbackstart (no DOM change from outside).
  env.play = async itemId => { pm.current = itemId; pm.isPlayingNow = true; pm.pausedNow = false; (env.events.playbackstart || []).forEach(f => f()); await settle(); };
  env.pause = async () => { pm.pausedNow = true; env.fireOn(pm.player, 'pause'); await settle(); };
  env.stop = async () => { (env.events.playbackstop || []).forEach(f => f()); pm.isPlayingNow = false; pm.pausedNow = false; await settle(); };
  env.emit = async name => { (env.events[name] || []).forEach(f => f()); await settle(); };
  env.viewshow = async target => { document.dispatch('viewshow', { target: target || view }); await settle(); };
  env.card = id => document.querySelector('.card[data-id="' + id + '"] .cardImageContainer');
  return env;
}

// Card L8: a listed plain stream next to the two SUB/WAVE stations, as GET Jellywave/Stations lists it.
const plainEntry = () => ({
  Id: 'plain', Kind: 'Stream', Name: 'Plain <b>FM</b>', ItemId: 'item-plain', ImageTag: 'tp', Genre: 'Eclectic · <i>Seattle</i>',
  Notes: 'Listener-supported, <img src=x onerror=alert(1)> 90.3 FM.', Website: 'https://www.plain.example/about?x=1', Codec: 'MP3', BitrateKbps: 128
});
function plainModel() { const m = baseModel(); m.stations.push(plainEntry()); return m; }
// Every request a SUB/WAVE header makes for its station; a plain stream's header makes none of them.
const stationRoute = u => /^Jellywave\/(Feed|Like|Lyrics|Schedule|Avatar|Request)/.test(u);

function plainModelOpts() { return { model: plainModel() }; }

function threeModel() { const m = baseModel(); m.stations.push({ Id: 'three', Name: 'Three', Kind: 'SubWave', ItemId: 'item-three', ImageTag: 't3' }); return m; }

const scenarios = [];
const scenario = (name, fn) => scenarios.push([name, fn]);

// ---------------------------------------------------------------- scenarios
// The header follows playback (card L5b, HI 2026-10-28): none while idle or while an unlisted item plays; the playing
// station's header on the tile's page (a SUB/WAVE station's, or since card L8 a plain stream's, drawn from the list).
// The transitions below are driven by playback events and the 5 s tick only; no scenario nudges the DOM to get there
// (p.mutated() appears only where a DOM pass is the point).
async function playStation(p, itemId) { await p.play(itemId); await p.serve(); }

scenario('idle: no header, the list and the drawer link only, no polling', async () => {
  const p = await open({ drawer: true });
  check(p.urls().join() === 'GET Jellywave/Stations', 'boot asks for the station list first (' + p.urls().join() + ')');
  await p.serve();
  check(p.headers().length === 0, 'nothing playing: no header');
  check(p.document.querySelectorAll('.jellywave-link').length === 1, 'the drawer link still shows');
  check(p.document.querySelectorAll('select').length === 0, 'no picker anywhere');
  const count = p.requests.length;
  await p.advance(30000);
  check(p.requests.length === count && p.headers().length === 0, 'an idle page asks nothing and stays without a header');
  check(p.storageUses === 0, 'nothing is read from or written to browser storage');
  check(p.warnings.length === 0, 'no warnings (' + p.warnings.join('; ') + ')');
});

scenario('an unlisted item playing (another library, a disabled station): no header, nothing repainted', async () => {
  const p = await open({ playing: 'item-plain' });
  await p.serve();
  check(p.headers().length === 0, 'an unlisted item playing at boot: no header');
  const before = p.requests.length;
  await p.play('item-plain');
  await p.serve();
  await p.advance(5000);
  await p.serve();
  check(p.headers().length === 0, 'an unlisted item started: no header');
  check(p.barText.querySelectorAll('a')[0].textContent === '' && !p.barImage.style.backgroundImage, 'the bar is untouched');
  check(p.card('item-plain').style.backgroundImage === 'url("original-item-plain")', 'its card is untouched');
  check(p.navigator.mediaSession.metadata === null, 'the media session is untouched');
  check(!p.requests.slice(before).some(r => r.url.startsWith('Jellywave/Feed')), 'no feed is asked');
});

scenario('playbackstart of a SUB/WAVE station: its header appears above the grid', async () => {
  const p = await open();
  await p.serve();
  await p.advance(300);
  await p.play('item-live');
  check(p.headers().length === 0 && p.pending('Jellywave/Stations').length === 1, 'playback start asks for the list first');
  await p.answer('Jellywave/Stations', { ChannelId: 'chan-1', ChannelName: 'Live Radio', Stations: baseModel().stations });
  const h = p.header();
  check(p.headers().length === 1 && h.parentNode === p.view && p.view.children.indexOf(h) === 0, 'one header, above the toolbar row, as soon as the list answers');
  check(h.getAttribute('data-station') === 'live', 'for the playing station');
  await p.serve();
  check(p.$('.jellywave-title-text').textContent === 'Title live', 'its feed is drawn');
  check(p.$('.jellywave-play') === null && p.shown(p.$('.jellywave-stop')), 'Stop, and no Play button');
  check(p.document.querySelectorAll('select').length === 0, 'no picker');
  check(p.urls().includes('GET Jellywave/Feed?stationId=live') && p.urls().includes('GET Jellywave/Like?stationId=live') && p.urls().includes('GET Jellywave/Schedule?stationId=live'), 'feed, like and schedule ask for the playing station');
  check(!p.urls().some(u => u.includes('stationId=two')), 'nothing is asked for the other station');
  check(p.barText.querySelectorAll('a')[0].textContent === 'Artist live – Title live', 'the bar is repainted from the header\'s feed');
  check(p.warnings.length === 0, 'no warnings (' + p.warnings.join('; ') + ')');
});

scenario('playing at boot: the header shows once the list answers', async () => {
  const p = await open({ playing: 'item-two' });
  await p.serve();
  check(p.headers().length === 1 && p.header().getAttribute('data-station') === 'two', 'the playing station\'s header');
});

scenario('another SUB/WAVE station starts: the header switches and late replies for the first are dropped', async () => {
  const p = await open();
  await p.play('item-live');
  const late = r => /^Jellywave\/(Like|Schedule|Avatar|Lyrics)/.test(r.url) || (r.url === 'Jellywave/Request' && r.method === 'POST');
  await p.serve(late);
  const first = p.header();
  p.$('.jellywave-lyrics').open = true; p.$('.jellywave-lyrics').dispatch('toggle'); await settle();
  p.$('.jellywave-request-text').value = 'play something';
  p.$('.jellywave-request').dispatch('submit');
  await settle();
  check(['Jellywave/Like?stationId=live', 'Jellywave/Lyrics?songId=song-live&stationId=live', 'Jellywave/Schedule?stationId=live', 'Jellywave/Avatar/dj_live?stationId=live', 'Jellywave/Request'].every(u => p.pending(u).length === 1),
    'like, lyrics, schedule, portrait and a request are on their way for the first station');
  await p.advance(300);

  await p.play('item-two');
  await p.answer('Jellywave/Stations', { ChannelId: 'chan-1', ChannelName: 'Live Radio', Stations: baseModel().stations });
  const second = p.header();
  check(second !== first && !p.document.body.contains(first) && p.headers().length === 1, 'a new header replaces the old one');
  check(second.getAttribute('data-station') === 'two', 'for the station playing now');
  check(p.$('.jellywave-title-text').textContent === 'Loading…', 'nothing of the previous station is shown');

  await p.answer('Jellywave/Like?stationId=live', { Enabled: true, SongId: 'song-live', Liked: true, Count: 9 });
  await p.answer('Jellywave/Lyrics?songId=song-live', { Status: 'ok', Lines: ['words of live'] });
  await p.answer('Jellywave/Schedule?stationId=live', { Available: true, Today: [{ StartHour: 0, EndHour: 24, Show: 'Grid live', Now: true }] });
  await p.answer('Jellywave/Avatar/dj_live', { blob: () => Promise.resolve({ size: 1 }) });
  await p.answer('Jellywave/Request', { Accepted: true, RequestId: 'req-live', Status: 'pending', Message: 'Sent.' });
  check(!p.shown(p.$('.jellywave-like')), 'a stale like status does not show a heart');
  check(!p.$('.jellywave-lyrics-body').textContent.includes('live') && !p.shown(p.$('.jellywave-schedule')), 'stale lyrics and schedule are not drawn');
  check(p.blobs === 0, 'a stale portrait makes no blob URL');
  check(p.$('.jellywave-request-result').textContent === '' && !p.urls().includes('GET Jellywave/Request/req-live'), 'a stale request receipt is neither shown nor polled');

  await p.serve();
  check(p.$('.jellywave-title-text').textContent === 'Title two', 'the new station\'s feed is drawn');
  const after = p.requests.slice(p.requests.findIndex(r => r.url === 'Jellywave/Feed?stationId=two'));
  check(after.length > 1 && after.every(r => !r.url.includes('stationId=live')), 'every request after the switch names the new station');
  check(p.$('.jellywave-like-count').textContent === '1' && p.shown(p.$('.jellywave-schedule')), 'the new station\'s like and schedule are drawn');
});

scenario('a switch seen as stop then start: the header goes, then comes back for the new station', async () => {
  const p = await open();
  await playStation(p, 'item-live');
  await p.advance(300);
  await p.stop();
  await p.advance(60);
  check(p.headers().length === 0, 'the stop removes the header');
  await playStation(p, 'item-two');
  check(p.headers().length === 1 && p.header().getAttribute('data-station') === 'two', 'the start brings the new station\'s header');
});

scenario('a feed reply for the previous station is dropped after a switch', async () => {
  const p = await open();
  await p.play('item-live');
  await p.answer('Jellywave/Stations', { ChannelId: 'chan-1', ChannelName: 'Live Radio', Stations: baseModel().stations });
  check(p.pending('Jellywave/Feed?stationId=live').length === 1, 'feed for the first station pending');
  await p.play('item-two');
  await p.answer('Jellywave/Stations', { ChannelId: 'chan-1', ChannelName: 'Live Radio', Stations: baseModel().stations });
  await p.answer('Jellywave/Feed?stationId=live', feedOf(p.model, 'live'));
  check(p.$('.jellywave-title-text').textContent === 'Loading…', 'the old station\'s feed is not drawn under the new station');
  check(p.pending('Jellywave/Like').length === 0 && p.pending('Jellywave/Feed?stationId=two').length === 1, 'nothing is asked on the strength of the old feed; the new one is awaited');
  await p.answer('Jellywave/Feed?stationId=two', feedOf(p.model, 'live'));
  check(p.$('.jellywave-title-text').textContent === 'Loading…' && p.pending('Jellywave/Like').length === 0, 'a reply naming another station is not drawn either');
  await p.advance(5000);
  await p.serve();
  check(p.$('.jellywave-title-text').textContent === 'Title two', 'the next poll draws the right station');
});

scenario('two stations playing the same song: a like reply for the previous station is dropped', async () => {
  const model = baseModel();
  model.songs = { live: 'shared', two: 'shared' };
  const p = await open({ model });
  await p.play('item-live');
  await p.serve(r => r.url.startsWith('Jellywave/Like'));
  check(p.pending('Jellywave/Like?stationId=live').length === 1, 'the first station\'s like status is pending');
  await p.play('item-two');
  await p.serve(r => r.url.startsWith('Jellywave/Like'));
  check(p.pending('Jellywave/Like?stationId=two').length === 1, 'the new station asks its own like status for the same song id');
  await p.answer('Jellywave/Like?stationId=live', { Enabled: true, SongId: 'shared', Liked: true, Count: 9 });
  check(!p.shown(p.$('.jellywave-like')), 'the previous station\'s answer is not shown');
  await p.answer('Jellywave/Like?stationId=two', { Enabled: true, SongId: 'shared', Liked: false, Count: 1 });
  check(p.$('.jellywave-like-count').textContent === '1', 'the new station\'s own answer is');
});

scenario('two stations playing the same song: a lyrics reply for the previous station is dropped', async () => {
  const model = baseModel();
  model.songs = { live: 'shared', two: 'shared' };
  const p = await open({ model });
  await playStation(p, 'item-live');
  p.$('.jellywave-lyrics').open = true; p.$('.jellywave-lyrics').dispatch('toggle'); await settle();
  check(p.pending('Jellywave/Lyrics?songId=shared&stationId=live').length === 1, 'the first station\'s lyrics are on their way');
  await p.play('item-two');
  await p.serve(r => r.url.startsWith('Jellywave/Lyrics'));
  p.$('.jellywave-lyrics').open = true; p.$('.jellywave-lyrics').dispatch('toggle'); await settle();
  check(p.pending('Jellywave/Lyrics?songId=shared&stationId=two').length === 1, 'the new station asks its own lyrics for the same song id');
  await p.answer('Jellywave/Lyrics?songId=shared&stationId=live', { Status: 'ok', Lines: ['words of live'] });
  check(!p.$('.jellywave-lyrics-body').textContent.includes('words of live'), 'the previous station\'s lyrics are not shown');
  await p.answer('Jellywave/Lyrics?songId=shared&stationId=two', { Status: 'ok', Lines: ['words of two'] });
  check(p.$('.jellywave-lyrics-body').textContent === 'words of two', 'the new station\'s own lyrics are');
});

scenario('L11 P paused counts as playing only for the settle: then the station stops', async () => {
  const p = await open();
  await playStation(p, 'item-live');
  const h = p.header();
  await p.pause();
  await p.advance(60);
  check(p.header() === h && p.pm.stops === 0, 'L11 P just paused: the header stays and nothing is stopped yet');
  await p.advance(400);
  check(p.pm.stops === 0, 'L11 P still within the 500 ms settle: no stop');
  await p.advance(100);
  check(p.pm.stops === 1 && p.pm.stoppedPlayers[0] === p.pm.player, 'L11 P after the settle: one stop, of the paused player');
  await p.advance(60);
  check(p.headers().length === 0 && !p.document.body.classList.contains('jellywave-station-current'), 'L11 P the stop removes the header and the bar class');
  await p.advance(5000);
  check(p.pm.stops === 1, 'L11 P only one stop');
});

scenario('L11 P a pause that is resumed within the settle is not stopped', async () => {
  const p = await open();
  await playStation(p, 'item-live');
  await p.pause();
  p.pm.pausedNow = false;   // resumed at once
  await p.advance(1000);
  check(p.pm.stops === 0 && p.headers().length === 1, 'L11 P not paused after the settle: no stop');
});

scenario('L11 P a pause during a switch to another item never stops', async () => {
  // 12.3: playbackManager stops the old item and drops its stream info before the media element's pause arrives.
  const p = await open(plainModelOpts());
  await playStation(p, 'item-live');
  p.pm.current = null; p.pm.isPlayingNow = false;
  await p.pause();
  await playStation(p, 'item-two');
  await p.advance(2000);
  check(p.pm.stops === 0 && p.header() && p.header().getAttribute('data-station') === 'two', 'L11 P the old item already gone at the pause: no stop, the new station plays');

  // A slower switch: the pause arrives while the old station is still current, the new item is current within the settle.
  const q = await open(plainModelOpts());
  await playStation(q, 'item-live');
  await q.pause();
  await playStation(q, 'item-plain');
  await q.advance(2000);
  check(q.pm.stops === 0 && q.header() && q.header().getAttribute('data-station') === 'plain', 'L11 P another station current after the settle: no stop');

  // The same station started again: paused for a moment, playing after the settle.
  const r = await open();
  await playStation(r, 'item-live');
  await r.pause();
  await playStation(r, 'item-live');
  await r.advance(2000);
  check(r.pm.stops === 0 && r.headers().length === 1, 'L11 P the same station restarted within the settle: no stop');
});

scenario('L11 P within the settle another item, or another player, is current and paused: no stop', async () => {
  // Another station, loading paused (a cast receiver starts paused); its own pause would be its own matter.
  const p = await open();
  await playStation(p, 'item-live');
  await p.pause();
  p.pm.current = 'item-two';
  await p.advance(1000);
  check(p.pm.stops === 0, 'L11 P another station current and paused after the settle: the first pause stops nothing');

  // A non-station item, paused.
  const q = await open();
  await playStation(q, 'item-live');
  await q.pause();
  q.pm.current = 'item-music';
  await q.advance(1000);
  check(q.pm.stops === 0, 'L11 P a non-station item current and paused after the settle: no stop');

  // Playback moved to a cast receiver within the settle, the station paused there while it loads.
  const r = await open();
  await playStation(r, 'item-live');
  await r.pause();
  r.pm.player = { name: 'cast' };
  await r.advance(1000);
  check(r.pm.stops === 0, 'L11 P another player after the settle: the local pause stops nothing');
});

scenario('L11 P a pause of anything but a listed station is Jellyfin\'s own', async () => {
  const p = await open();
  await p.serve();
  await p.play('item-music');
  await p.serve();
  await p.pause();
  await p.advance(5000);
  check(p.pm.stops === 0 && p.pm.pausedNow, 'L11 P a non-station item stays paused');
  check(!p.document.body.classList.contains('jellywave-station-current'), 'L11 P and the bar keeps its play/pause (no class)');

  // A station item paused after the list dropped it (disabled meanwhile): not a listed station, so not stopped.
  const q = await open();
  await playStation(q, 'item-live');
  q.model.stations = q.model.stations.filter(x => x.Id !== 'live');
  await q.advance(60000); await q.serve();
  await q.pause();
  await q.advance(2000);
  check(q.pm.stops === 0, 'L11 P an item no longer listed: its pause is left alone');
});

scenario('L11 P the bar class follows the current station item', async () => {
  const p = await open(plainModelOpts());
  await p.serve();
  const on = () => p.document.body.classList.contains('jellywave-station-current');
  check(!on(), 'L11 P nothing playing: no class');
  await playStation(p, 'item-live');
  check(on(), 'L11 P a SUB/WAVE station plays: class');
  await playStation(p, 'item-plain');
  check(on(), 'L11 P switched to a plain stream: class');
  await p.play('item-music'); await p.serve();
  check(!on(), 'L11 P another item: no class');
  await playStation(p, 'item-two');
  check(on(), 'L11 P a station again: class');
  await p.stop(); await p.advance(60);
  check(!on(), 'L11 P stop: no class');
});

scenario('L11 P the bar CSS and our Stop for narrow widths', async () => {
  const p = await open();
  const styles = p.document.querySelectorAll('style');
  check(styles.length === 1 && styles[0].parentNode === p.document.head, 'L11 P one style element, in head');
  const css = styles[0].textContent;
  check(css.includes('body.jellywave-station-current .nowPlayingBar .playPauseButton{display:none!important}'), 'L11 P every play/pause is hidden while a station is current');
  check(css.includes('body.jellywave-station-current .nowPlayingBar .nowPlayingBarCenter .stopButton{display:inline-flex!important}'), 'L11 P jellyfin-web\'s own Stop shows (beats its 80em rule)');
  check(css.includes('@media (max-width:56em){body.jellywave-station-current .nowPlayingBar .nowPlayingBarRight .jellywave-barstop{display:inline-flex!important}}')
    && css.includes('.nowPlayingBar .jellywave-barstop{display:none!important}'), 'L11 P our Stop shows below 56em, while a station is current, else not');
  check(css.includes('body.jellywave-station-current .nowPlayingBar .nowPlayingBarCenter.hide~.nowPlayingBarRight .jellywave-barstop{display:inline-flex!important}')
    && css.indexOf('nowPlayingBarCenter.hide~') > css.indexOf('.nowPlayingBar .jellywave-barstop{display:none!important}'), 'R5 tablet: our Stop also shows at any width when the bar hides its centre group (a browser it calls mobile)');
  await p.boot();
  check(p.document.querySelectorAll('style').length === 1, 'L11 P a second construction adds no second style');
  await p.serve();
  check(p.barRight.querySelectorAll('.jellywave-barstop').length === 0, 'L11 P no station yet: no Stop of ours');
  await playStation(p, 'item-live');
  const ours = p.barRight.querySelectorAll('.jellywave-barstop');
  check(ours.length === 1 && p.barRight.children.indexOf(ours[0]) === 1 && ours[0].getAttribute('aria-label') === 'Stop'
    && ours[0].querySelector('.material-icons.stop') !== null, 'L11 P our Stop sits before the right play/pause, labelled, with the stop icon');
  check(p.innerHTMLs.length === p.innerHTMLs.filter(h => !h.includes('jellywave-barstop')).length, 'L11 P built without innerHTML');
  ours[0].click();
  check(p.pm.stops === 1, 'L11 P our Stop stops');
  await p.advance(60);
  await playStation(p, 'item-two');
  check(p.barRight.querySelectorAll('.jellywave-barstop').length === 1, 'L11 P added once, kept for the next station');
});

scenario('L11 fix round (S4): a refused list or a sign-out while a station plays clears the bar class', async () => {
  const on = q => q.document.body.classList.contains('jellywave-station-current');
  const p = await open();
  await playStation(p, 'item-live');
  check(on(p), 'L11 S4 a station plays: class');
  await p.advance(60000);
  await p.fail('Jellywave/Stations', 403);
  check(!on(p), 'L11 S4 the list answers 403: no class (Jellyfin\'s play/pause is back)');

  const q = await open();
  await playStation(q, 'item-live');
  q.token = '';
  await q.advance(5000);
  check(!on(q), 'L11 S4 signed out: no class');

  const r = await open();
  await playStation(r, 'item-live');
  await r.advance(5000);
  await r.fail('Jellywave/Feed?stationId=live', 403);
  check(!on(r), 'L11 S4 a feed 403: no class');
});

scenario('L11 P a new player (a cast target) is listened to instead of the old one', async () => {
  const p = await open();
  await playStation(p, 'item-live');
  const local = p.pm.player;
  check(p.targetHandlers.filter(h => h.name === 'pause' && h.target === local).length === 1, 'L11 P one pause listener on the current player');
  const cast = { name: 'cast' };
  p.pm.player = cast;
  await p.emit('playerchange');
  check(p.targetHandlers.filter(h => h.name === 'pause').length === 1 && p.targetHandlers[0].target === cast, 'L11 P playerchange moves the listener to the new player');
  p.pm.pausedNow = true;
  p.fireOn(local, 'pause');
  await p.advance(1000);
  check(p.pm.stops === 0, 'L11 P the old player\'s pause does nothing');
  p.fireOn(cast, 'pause'); await settle();
  await p.advance(1000);
  check(p.pm.stops === 1 && p.pm.stoppedPlayers[0] === cast, 'L11 P the new player\'s pause stops the station on it');
});

scenario('stop removes the header, and the poll stops', async () => {
  const p = await open({ drawer: true });
  await playStation(p, 'item-live');
  await p.advance(300);
  const portrait = p.$('.jellywave-dj-avatar').src;
  check(portrait.startsWith('blob:'), 'the portrait shows as a blob URL');
  p.$('.jellywave-stop').click();
  check(p.pm.stops === 1, 'Stop stops');
  await p.advance(60);
  check(p.headers().length === 0, 'the header goes on playbackstop');
  check(p.revoked.includes(portrait), 'the portrait\'s blob URL is revoked with its header');
  check(p.document.querySelectorAll('.jellywave-link').length === 1, 'the drawer link stays');
  await p.serve();
  await p.advance(5000);
  const count = p.requests.length;
  await p.advance(30000);
  check(p.requests.length === count && !p.timers.some(t => t.every), 'nothing is asked after the stop and the poll is gone');
  check(p.warnings.length === 0, 'no warnings (' + p.warnings.join('; ') + ')');
});

scenario('L14 fix round (S1): the page asks a feed only while a station plays in this client, hidden tab or not', async () => {
  // A background tab: Chrome slows a hidden, silent tab's timers to about one a minute, near the server's 60 s page window.
  // The page never polls such a tab: it asks a feed only while a station plays here (a pause becomes a stop, L11 P), and
  // that playing session alone keeps the station asked on the server; a playing tab is not silent, so not slowed.
  const p = await open({ drawer: true });
  p.document.hidden = true;
  p.document.visibilityState = 'hidden';
  await p.serve();
  const before = p.requests.length;
  await p.advance(120000);
  check(p.requests.length === before && !p.timers.some(t => t.every), 'L14 S1 a hidden tab with nothing playing asks nothing and keeps no poll');
  await playStation(p, 'item-live');
  await p.advance(5000);
  check(p.pending('Jellywave/Feed?stationId=live').length === 1, 'L14 S1 a hidden tab playing a station keeps the bar\'s feed every 5 s');
  await p.serve();
  p.$('.jellywave-stop').click();
  await p.advance(60);
  await p.serve();
  await p.advance(5000);
  const after = p.requests.length;
  await p.advance(120000);
  check(p.requests.length === after && !p.timers.some(t => t.every), 'L14 S1 after the stop a hidden tab asks nothing (' + p.urls().slice(after).join() + ')');
});

scenario('playback moves without an event this script saw: the next tick follows', async () => {
  const p = await open();
  await playStation(p, 'item-live');
  await p.advance(300);
  p.observing = false;
  p.pm.current = 'item-two';   // no playbackstart delivered
  await p.advance(5000);
  check(p.headers().length === 1 && p.header().getAttribute('data-station') === 'two', 'the tick switches the header');
  p.pm.isPlayingNow = false;   // no playbackstop delivered
  await p.advance(5000);
  check(p.headers().length === 0, 'the tick removes the header');
});

scenario('an unlisted item starts while the list is in its network back-off: the header goes at once', async () => {
  const p = await open();
  await playStation(p, 'item-live');
  await p.advance(60000);   // the poll refreshes the list ...
  await p.fail('Jellywave/Stations');   // ... and the network drops it: the list waits 15 s
  await p.serve();
  await p.advance(300);
  p.observing = false;
  await p.play('item-plain');
  check(p.pending('Jellywave/Stations').length === 0, 'the list is not asked during its back-off');
  check(p.headers().length === 0, 'playbackstart alone removes the header');
});

scenario('an unlisted item after a SUB/WAVE station removes the header', async () => {
  const p = await open();
  await playStation(p, 'item-live');
  await p.advance(300);
  const before = p.requests.length;
  await p.play('item-plain');
  await p.serve();
  check(p.headers().length === 0, 'its playbackstart removes the header');
  await p.advance(5000);
  await p.serve();
  check(!p.requests.slice(before).some(r => r.url.startsWith('Jellywave/Feed')), 'no feed is asked while it plays');
  check(p.card('item-plain').style.backgroundImage === 'url("original-item-plain")', 'its card is untouched');
});

scenario('an unlisted item seen through statechange alone removes the header', async () => {
  const p = await open();
  await playStation(p, 'item-live');
  await p.advance(300);
  p.pm.current = 'item-plain';
  await p.emit('statechange');
  await p.advance(60);
  check(p.headers().length === 0, 'statechange removes the header');
});

scenario('opening the tile page while a station plays shows its header at once', async () => {
  const p = await open({ noView: true });
  check(p.urls().join() === 'GET Jellywave/Stations', 'a cached library view: only the list is asked at boot');
  await p.serve();
  await playStation(p, 'item-two');
  check(p.headers().length === 0 && p.barText.querySelectorAll('a')[0].textContent === 'Artist two – Title two', 'off the tile page: no header, the bar is repainted');
  p.document.body.appendChild(p.view);
  await p.viewshow(p.view);
  check(p.headers().length === 1 && p.header().getAttribute('data-station') === 'two', 'viewshow of the tile page shows the playing station\'s header at once');
  await p.serve();
  check(p.$('.jellywave-title-text').textContent === 'Title two', 'with its feed');
});

scenario('repaint: bar, card and media session follow the playing station', async () => {
  const p = await open();
  await playStation(p, 'item-two');
  const lines = p.barText.querySelectorAll('a');
  check(lines[0].textContent === 'Artist two – Title two' && lines[1].textContent === 'Station Two · Show two', 'the playing bar shows the playing station');
  check(p.barImage.style.backgroundImage === 'url("img/item-two?tag=cover-two&size=140")', 'the bar image is the playing station\'s cover');
  check(p.card('item-two').style.backgroundImage === 'url("img/item-two?tag=cover-two&size=400")', 'the playing station\'s card is repainted');
  check(p.card('item-live').style.backgroundImage === 'url("original-item-live")', 'another station\'s card is left alone');
  check(p.navigator.mediaSession.metadata && p.navigator.mediaSession.metadata.title === 'Title two', 'the media session is the playing station\'s');
  await playStation(p, 'item-live');
  await p.advance(5000);
  await p.serve();
  check(p.barText.querySelectorAll('a')[0].textContent === 'Artist live – Title live', 'the bar follows the newly playing station');
});

scenario('repaint while off the channel page', async () => {
  const p = await open({ noView: true });
  await p.serve();
  await p.play('item-two');
  check(p.urls().join() === 'GET Jellywave/Stations,GET Jellywave/Stations', 'playback start asks for the station list again');
  await p.serve();
  check(p.barText.querySelectorAll('a')[0].textContent === 'Artist two – Title two' && p.headers().length === 0, 'the bar is repainted without a header');
  p.pm.current = null;
  await p.advance(5000);
  await p.serve();
  const count = p.requests.length;
  await p.advance(20000);
  check(p.requests.length === count, 'polling stops when nothing plays and no header shows');
});

scenario('a playing feed that arrives after playback moved on is dropped', async () => {
  const p = await open({ noView: true });
  await p.serve();
  await p.play('item-two');
  await p.answer('Jellywave/Stations', { ChannelId: 'chan-1', ChannelName: 'Live Radio', Stations: baseModel().stations });
  check(p.pending('Jellywave/Feed?stationId=two').length === 1, 'the playing station\'s feed is pending');
  p.pm.current = 'item-plain';
  await p.answer('Jellywave/Feed?stationId=two', feedOf(p.model, 'two'));
  check(p.barText.querySelectorAll('a')[0].textContent === '', 'the late reply does not repaint an unlisted item\'s bar');
});

scenario('features the station turns off stay hidden', async () => {
  const model = baseModel();
  Object.assign(model, { likeOff: true, lyricsOff: true, noSchedule: true, noRecent: true });
  const p = await open({ model });
  await playStation(p, 'item-live');
  check(p.$('.jellywave-title-text').textContent === 'Title live', 'the header is drawn');
  check(!p.shown(p.$('.jellywave-like')), 'likes off: no heart');
  check(!p.shown(p.$('.jellywave-lyrics')), 'lyrics off: no lyrics panel');
  check(!p.shown(p.$('.jellywave-schedule')) && !p.header().textContent.includes('No schedule'), 'no schedule published: no schedule panel, no sentence');
  check(!p.shown(p.$('.jellywave-recent')), 'nothing recently played: no list');
  for (let i = 0; i < 3; i++) { await p.advance(5000); await p.serve(); }
  check(!p.shown(p.$('.jellywave-like')) && !p.shown(p.$('.jellywave-lyrics')) && !p.shown(p.$('.jellywave-schedule')) && !p.shown(p.$('.jellywave-recent')), 'and they stay hidden across polls');
  check(p.urls().filter(u => u.startsWith('GET Jellywave/Schedule')).length === 1, 'a station without a schedule is not asked again within five minutes');
  p.model.noSchedule = false;
  await p.advance(300000);
  await p.serve();
  check(p.shown(p.$('.jellywave-schedule')) && p.$('.jellywave-schedule-body').textContent.includes('Grid live'), 'a schedule published later shows within five minutes');

  const q = await open();
  await playStation(q, 'item-live');
  check(q.shown(q.$('.jellywave-like')) && q.shown(q.$('.jellywave-lyrics')) && q.shown(q.$('.jellywave-schedule')) && q.shown(q.$('.jellywave-recent')), 'all on: heart, lyrics, schedule and recent show');
  for (let i = 0; i < 62; i++) { await q.advance(5000); await q.serve(); }
  check(q.urls().filter(u => u.startsWith('GET Jellywave/Schedule')).length === 1, 'a published schedule is not asked again while its panel stays closed');
  q.$('.jellywave-schedule').open = true; q.$('.jellywave-schedule').dispatch('toggle'); await settle();
  check(q.pending('Jellywave/Schedule').length === 1, 'opening the panel after five minutes asks again');
  await q.serve();
  check(q.$('.jellywave-schedule-body').textContent.includes('Grid live'), 'and shows it');

  const r = await open();
  await r.play('item-live');
  await r.serve(x => x.url.startsWith('Jellywave/Like'));
  await r.answer('Jellywave/Like?stationId=live', { Enabled: false, SongId: 'song-live', Liked: false, Count: 3 });
  check(!r.shown(r.$('.jellywave-like')), 'likes off: no heart even when the answer names a song');
});

scenario('a schedule that fails to load hides its panel', async () => {
  const model = baseModel();
  model.scheduleFails = true;
  const p = await open({ model });
  await playStation(p, 'item-live');
  check(!p.shown(p.$('.jellywave-schedule')), 'a failure: no schedule panel');
  check(p.pending().length === 0 && p.urls().filter(u => u.startsWith('GET Jellywave/Schedule')).length === 1, 'asked once');
});

scenario('a refused or unavailable request keeps the form and shows the station\'s message', async () => {
  const model = baseModel();
  model.requestAnswer = { Accepted: false, Status: 'unavailable', Message: 'Requests are closed right now.' };
  const p = await open({ model });
  await playStation(p, 'item-live');
  const form = p.$('.jellywave-request');
  p.$('.jellywave-request-text').value = 'play something';
  form.dispatch('submit');
  await p.serve();
  check(p.$('.jellywave-request-result').textContent === 'Requests are closed right now.', 'the station\'s own words are shown');
  check(p.shown(form) && !p.$('.jellywave-request-send').disabled, 'the form stays, ready for another try');
  p.model.requestAnswer = { Accepted: false, Status: 'refused', Message: 'Slow down.', RetryAfterSeconds: 40 };
  form.dispatch('submit');
  await p.serve();
  check(p.$('.jellywave-request-result').textContent === 'Slow down. (40 s)' && p.shown(form), 'a refusal with a wait shows the wait, and the form stays');
});

scenario('requests post the header\'s station; the poll names none', async () => {
  const p = await open();
  await playStation(p, 'item-two');
  p.$('.jellywave-request-text').value = 'play something';
  p.$('.jellywave-request').dispatch('submit');
  await settle();
  const post = p.pending('Jellywave/Request')[0];
  check(post && post.method === 'POST' && post.data.StationId === 'two' && post.data.Text === 'play something' && post.data.Name === 'kevin', 'the request carries the header\'s station');
  await p.serve();
  check(p.urls().includes('GET Jellywave/Request/req-two'), 'the status poll carries only the request id');
  check(p.$('.jellywave-request-result').textContent === 'ack for req-two', 'the answer is shown');
});

scenario('likes and the portrait name the header\'s station', async () => {
  const p = await open();
  await playStation(p, 'item-two');
  check(p.urls().includes('GET Jellywave/Avatar/dj_two?stationId=two'), 'the portrait is fetched for the header\'s station');
  p.$('.jellywave-like').click();
  await settle();
  const post = p.pending('Jellywave/Like')[0];
  check(post && post.method === 'POST' && post.data.SongId === 'song-two' && post.data.StationId === 'two', 'the like carries the song and the station');
  await p.play('item-live');
  await p.answer('Jellywave/Stations', { ChannelId: 'chan-1', ChannelName: 'Live Radio', Stations: baseModel().stations });
  await p.answer('Jellywave/Like', { Enabled: true, SongId: 'song-two', Liked: true, Count: 7 });
  await p.serve();
  check(p.header().getAttribute('data-station') === 'live' && p.$('.jellywave-like-count').textContent === '1', 'a like reply for the previous station is dropped');
});

scenario('nothing listed: no header, no drawer link', async () => {
  const model = baseModel();
  model.stations = [];
  const p = await open({ model, drawer: true, playing: 'item-live' });
  await p.serve();
  check(p.headers().length === 0, 'an empty list: no header');
  check(p.document.querySelectorAll('.jellywave-link').length === 0, 'and no drawer link');
  check(p.pending().length === 0 && !p.urls().some(u => u.startsWith('GET Jellywave/Feed')), 'no feed is asked');
  await p.advance(1000);
  p.mutated();
  await p.advance(300);
  check(p.urls().filter(u => u === 'GET Jellywave/Stations').length === 1, 'an empty list is not asked again within the refresh interval');
  p.model.stations = baseModel().stations;
  await p.advance(60000);
  p.mutated();
  await p.advance(300);
  await p.serve();
  check(p.header() !== null && p.header().getAttribute('data-station') === 'live', 'a SUB/WAVE station added later shows after the refresh interval while it plays');
});

scenario('a feed 404 re-reads the list at once; the list\'s own 404 and 403 refuse with a back-off', async () => {
  const p = await open({ drawer: true });
  await playStation(p, 'item-live');
  check(p.headers().length === 1 && p.document.querySelectorAll('.jellywave-link').length === 1, 'header and drawer link shown');
  check(p.document.querySelector('.jellywave-link .navMenuOptionText').textContent === 'Live Radio', 'the drawer link names the tile');
  await p.advance(5000);
  await p.fail('Jellywave/Feed?stationId=live', 404);
  check(p.headers().length === 0 && p.document.querySelectorAll('.jellywave-link').length === 0, 'a feed 404 removes the header and the drawer link');
  check(p.pending('Jellywave/Stations').length === 1, 'and asks for the list again at once (no back-off)');
  await p.fail('Jellywave/Stations', 404);
  check(p.headers().length === 0, 'a list 404 leaves no header');
  p.mutated();
  await p.advance(300);
  check(p.pending('Jellywave/Stations').length === 0, 'after the list\'s 404 it is not asked again at once');
  await p.advance(15000);
  check(p.pending('Jellywave/Stations').length === 1, 'the list is asked again after 15 s');
  await p.fail('Jellywave/Stations', 403);
  check(p.headers().length === 0, 'a list 403 leaves no header');
  await p.advance(60000);
  p.mutated();
  await p.advance(300);
  check(p.pending('Jellywave/Stations').length === 0, 'after a 403 the list waits longer than a minute');
  await p.advance(300000);
  check(p.pending('Jellywave/Stations').length === 1, 'and is asked again after five minutes');
  await p.serve();
  check(p.headers().length === 1, 'the header comes back once allowed, while the station still plays');
});

scenario('a 403 removes everything: header, drawer link, repaint', async () => {
  const p = await open({ drawer: true });
  await playStation(p, 'item-live');
  await p.advance(5000);
  await p.fail('Jellywave/Feed?stationId=live', 403);
  check(p.headers().length === 0 && p.document.querySelectorAll('.jellywave-link').length === 0, 'a feed 403 removes the header and the drawer link at once');
  check(p.pending('Jellywave/Stations').length === 0, 'and the list is not asked again at once');
  p.barText.querySelectorAll('a')[0].textContent = 'jellyfin text';
  await p.play('item-live');
  await p.advance(60000);
  check(p.pending().length === 0 && p.barText.querySelectorAll('a')[0].textContent === 'jellyfin text' && p.headers().length === 0, 'nothing is asked or repainted within a minute, even on a new playback start');
  const q = await open({ drawer: true });
  await playStation(q, 'item-live');
  await q.advance(60000);
  await q.fail('Jellywave/Stations', 403);
  check(q.headers().length === 0 && q.document.querySelectorAll('.jellywave-link').length === 0, 'a list 403 removes them too');
});

scenario('the playing station is disabled: its header goes within two idle minutes (HI step 9)', async () => {
  const p = await open();
  await playStation(p, 'item-two');
  check(p.header().getAttribute('data-station') === 'two', 'two playing, its header shown');
  p.model.stations = p.model.stations.slice(0, 1);   // the admin disables "two": its feed answers 404
  for (let i = 0; i < 24; i++) { await p.advance(5000); await p.serve(); }
  check(p.headers().length === 0, 'its header goes and no other station\'s header takes its place');
  await playStation(p, 'item-live');
  check(p.headers().length === 1 && p.header().getAttribute('data-station') === 'live', 'a listed station played next gets its header');
});

scenario('a feed 404 re-reads the list even right after a list refresh failed on the network', async () => {
  const p = await open();
  await playStation(p, 'item-two');
  await p.advance(60000);   // the poll refreshes the list ...
  await p.fail('Jellywave/Stations');   // ... and the network drops it: the header stays, the list waits 15 s
  check(p.headers().length === 1, 'a network failure keeps the header');
  p.model.stations = p.model.stations.slice(0, 1);
  await p.advance(5000);
  await p.serve(r => r.url.startsWith('Jellywave/Stations'));   // the feed of "two" answers 404
  check(p.pending('Jellywave/Stations').length === 1, 'the list is asked again at once, despite its back-off');
  await p.serve();
  check(p.headers().length === 0, 'and the header of the station that left stays away');
});

scenario('a feed that keeps answering 404 while the list still names its station does not loop', async () => {
  const p = await open();
  await playStation(p, 'item-live');
  p.model.missingFeeds.add('live');
  await p.advance(5000);
  await p.serve();
  await p.advance(1000);
  await p.serve();
  const asked = p.urls().filter(u => u === 'GET Jellywave/Stations').length;
  check(p.headers().length === 0, 'the second 404 within 15 s refuses the page');
  for (let i = 0; i < 10; i++) { p.mutated(); await p.advance(1000); await p.serve(); }
  check(p.urls().filter(u => u === 'GET Jellywave/Stations').length === asked, 'and the list waits for its back-off');
  p.model.missingFeeds.delete('live');
  await p.advance(15000);   // no DOM change from outside: the refusal's own retry asks again
  await p.serve();
  check(p.headers().length === 1, 'the header comes back after the back-off while the station plays');
  check(p.urls().filter(u => u === 'GET Jellywave/Stations').length === asked + 1, 'with one list request for the back-off');
});

scenario('RB3: the channel answers 404, then comes back, while a station plays', async () => {
  const p = await open();
  await playStation(p, 'item-live');
  p.model.missingFeeds.add('live');   // feed 404 ...
  await p.advance(5000);
  await p.fail('Jellywave/Feed?stationId=live', 404);
  await p.fail('Jellywave/Stations', 404);   // ... and the list too (channel gone)
  check(p.headers().length === 0, 'RB3: no header while the channel is gone');
  p.model.missingFeeds.delete('live');
  for (let i = 0; i < 24; i++) { await p.advance(5000); await p.serve(); }
  check(p.headers().length === 1, 'RB3: after the channel is back, the header returns without a DOM change');
  const lists = p.urls().filter(u => u === 'GET Jellywave/Stations').length;
  check(lists <= 5, 'RB3: the list was asked at a bounded rate (' + lists + ' times)');
});

scenario('a session change forgets the re-read guard (G1)', async () => {
  const p = await open({ model: threeModel() });
  await playStation(p, 'item-live');
  p.model.missingFeeds.add('live');
  await p.advance(5000);
  await p.fail('Jellywave/Feed?stationId=live', 404);   // user A: a feed 404 and a re-read just now
  await p.serve(r => r.url.startsWith('Jellywave/Feed'));
  check(p.header() && p.pending('Jellywave/Feed?stationId=live').length === 1, 'user A: the header is back after the re-read, its feed on its way');
  p.user = 'user-b';
  await p.advance(5000);
  await p.serve(r => r.url.startsWith('Jellywave/Feed'));
  const forB = p.pending('Jellywave/Feed?stationId=live');
  check(forB.length === 2, 'user A\'s feed request and user B\'s are both out');
  forB[1].done = true; forB[1].reject({ status: 404 }); await settle();   // user B's first feed 404, within 15 s of user A's
  check(p.pending('Jellywave/Stations').length === 1, 'user B\'s first feed 404 re-reads the list at once');
});

scenario('RA K20: off the channel page the bar keeps polling the playing station', async () => {
  const p = await open({ noView: true });
  await p.serve();
  await playStation(p, 'item-two');
  await p.advance(5000);
  check(p.pending('Jellywave/Feed?stationId=two').length === 1, 'the playing station is asked again after 5 s');
  await p.serve();
  check(p.navigator.mediaSession.metadata && p.navigator.mediaSession.metadata.title === 'Title two', 'the bar is repainted from the new answer');
});

scenario('RA K12: a list answer for the previous user is dropped', async () => {
  const p = await open({ playing: 'item-live' });
  await p.serve();
  await p.advance(60000);   // list refresh in flight for user A
  check(p.pending('Jellywave/Stations').length === 1, 'list refresh pending for A');
  p.user = 'user-b';
  await p.advance(5000);    // tick: new session, B's list asked
  const forA = p.pending('Jellywave/Stations')[0];
  forA.done = true; forA.resolve({ ChannelId: 'chan-1', ChannelName: 'A', Stations: [{ Id: 'secret', Name: 'A only', ItemId: 'item-live' }] });
  await p.advance(300);
  check(!p.headers().some(h => h.getAttribute('data-station') === 'secret'), 'user B never sees user A\'s station for the playing item');
  await p.serve();
  check(p.header() && p.header().getAttribute('data-station') === 'live', 'user B\'s own list builds the header');
});

scenario('a station that leaves the list while it plays: its header goes on the list\'s answer', async () => {
  const p = await open({ drawer: true });
  await playStation(p, 'item-two');
  p.model.stations = p.model.stations.slice(0, 1);
  await p.advance(60000);
  await p.serve(r => r.url.startsWith('Jellywave/Feed'));   // the list's answer alone removes it
  check(p.headers().length === 0, 'no header');
  check(p.document.querySelectorAll('.jellywave-link').length === 1, 'the drawer link stays (a SUB/WAVE station is still listed)');
});

scenario('the last SUB/WAVE station leaves the list: the header goes', async () => {
  const p = await open({ drawer: true });
  await playStation(p, 'item-live');
  p.model.stations = [];
  await p.advance(60000);
  await p.serve(r => r.url.startsWith('Jellywave/Feed'));   // the list's answer alone removes it (the feed is still out)
  check(p.headers().length === 0 && p.document.querySelectorAll('.jellywave-link').length === 0, 'header and drawer link removed');
  await p.serve();
  const count = p.requests.length;
  await p.advance(30000);
  check(p.requests.length === count, 'and the poll stops');
});

scenario('the state key changes with the user', async () => {
  const p = await open();
  await playStation(p, 'item-two');
  p.$('.jellywave-lyrics').open = true; p.$('.jellywave-lyrics').dispatch('toggle'); await settle();
  check(p.pending('Jellywave/Lyrics').length === 1, 'a lyrics request is in flight for user A');
  p.user = 'user-b';
  await p.advance(5000);
  check(p.headers().length === 0, 'another user: the old header goes at the next tick');
  check(p.pending('Jellywave/Stations').length === 1, 'and the list is asked again for the new user');
  await p.advance(300);
  check(p.headers().length === 0, 'no header is built from the previous user\'s list meanwhile');
  await p.answer('Jellywave/Lyrics', { Status: 'ok', Lines: ['words of user A'] });
  await p.serve();
  check(p.header() && p.header().getAttribute('data-station') === 'two', 'user B gets the header of the station playing');
  check(!p.header().textContent.includes('user A'), 'a reply asked for user A is dropped');
  p.token = '';
  await p.advance(5000);
  check(p.headers().length === 0, 'a sign-out removes the header');
});

scenario('station text goes in as text only', async () => {
  const model = baseModel();
  model.stations[1].Name = '<img src=x onerror=alert(1)>Two';
  model.channelName = '<b>Tile</b>';
  const p = await open({ model, drawer: true });
  await playStation(p, 'item-two');
  check(p.barText.querySelectorAll('a')[1].textContent === '<img src=x onerror=alert(1)>Two · Show two', 'a station name with markup is the bar\'s text');
  check(p.document.querySelector('.jellywave-link .navMenuOptionText').textContent === '<b>Tile</b>', 'the tile name is the link\'s text');
  check(p.document.querySelectorAll('img').length === 1 && p.document.querySelectorAll('b').length === 0, 'no element was made from station text');
  const fixed = p.innerHTMLs.filter(h => h.length > 0);
  check(fixed.every(h => !h.includes('onerror') && !h.includes('<b>') && !h.includes('Title two')), 'markup is only ever the fixed header and link strings');
});

scenario('a cached view keeps its header; viewshow re-points it', async () => {
  const p = await open();
  await playStation(p, 'item-live');
  const h1 = p.header();
  await p.viewshow(p.view);
  check(p.headers().length === 1 && p.header() === h1, 'viewshow on the same view keeps one header');
  // The user opens the tile again: jellyfin-web hides the first view (cached) and shows a second one.
  p.view.classList.add('hide');
  const view2 = p.tileView(false);
  p.document.body.appendChild(view2);
  await p.viewshow(view2);
  const h2 = view2.querySelector('.jellywave-header');
  check(h2 && h2 !== h1 && p.view.querySelector('.jellywave-header') === h1, 'the second view gets its own header; the cached one keeps its own');
  await p.serve();
  // Back to the cached first view: its header becomes the one drawn and polled.
  view2.classList.add('hide');
  p.view.classList.remove('hide');
  h1.querySelector('.jellywave-title-text').textContent = 'stale';
  await p.viewshow(p.view);
  check(h1.querySelector('.jellywave-title-text').textContent === 'Title live', 'viewshow re-points to the cached view\'s header and draws it');
  h1.querySelector('.jellywave-title-text').textContent = 'stale';
  await p.advance(5000);
  await p.serve();
  check(h1.querySelector('.jellywave-title-text').textContent === 'Title live', 'the poll keeps drawing into it');
  p.view.querySelector('.jellywave-header').setAttribute('data-station', 'two');   // as if built for another station before a switch elsewhere
  await p.viewshow(p.view);
  await p.serve();
  const rebuilt = p.view.querySelector('.jellywave-header');
  check(rebuilt && rebuilt !== h1 && rebuilt.getAttribute('data-station') === 'live', 'a header for another station is rebuilt');
});

scenario('two views of the tile: a stop removes both headers, a switch leaves at most one', async () => {
  const p = await open();
  await playStation(p, 'item-live');
  p.view.classList.add('hide');
  const view2 = p.tileView(false);
  p.document.body.appendChild(view2);
  await p.viewshow(view2);
  await p.serve();
  check(p.headers().length === 2, 'a header in the cached view and one in the shown view');
  await p.advance(300);
  p.observing = false;
  await p.stop();
  await p.advance(60);
  check(p.headers().length === 0, 'a stop removes every header, the cached view\'s too');
  p.observing = true;
  await playStation(p, 'item-live');
  view2.classList.add('hide');
  p.view.classList.remove('hide');
  await p.viewshow(p.view);
  await p.serve();
  check(p.headers().length === 2, 'both views have a header again');
  await p.advance(300);
  p.observing = false;
  await playStation(p, 'item-two');
  check(p.headers().length === 1 && p.header().getAttribute('data-station') === 'two', 'a switch leaves one header, for the new station, in the shown view');
});

scenario('a hidden cached view of the tile never gets a header', async () => {
  const p = await open({ noView: true });
  const hidden = p.tileView(true);
  p.document.body.appendChild(hidden);
  await p.serve();
  await playStation(p, 'item-two');
  check(p.headers().length === 0, 'playbackstart builds no header in a hidden view');
  await p.advance(5000);
  await p.serve();
  check(p.headers().length === 0, 'nor does the tick');
  check(p.barText.querySelectorAll('a')[0].textContent === 'Artist two – Title two', 'the bar is still repainted');
});

scenario('the boot guard and the debounce', async () => {
  const p = await open();
  await playStation(p, 'item-live');
  const before = p.observers.length;
  const Plugin = await p.window.JellywaveUi();
  new Plugin({ playbackManager: p.pm, events: p.events }); // eslint-disable-line no-new
  check(p.observers.length === before && (p.events.playbackstart || []).length === 1, 'a second construction adds no observer or listener');
  const count = p.requests.length;
  for (let i = 0; i < 20; i++) { p.mutated(); }
  check(p.timers.filter(t => !t.every).length <= 1, 'many DOM changes arm one pending timer');
  await p.advance(250);
  check(p.requests.length === count, 'a DOM pass with a header in place asks nothing');
});

// ---------------------------------------------------------------- card L8: a header for plain streams
// The plain header's shape: logo, name, genre, notes, format line, website link, Stop; nothing else is asked.
function checkPlainHeader(p, label) {
  const h = p.header();
  check(!!h && p.headers().length === 1 && h.getAttribute('data-station') === 'plain' && h.getAttribute('data-kind') === 'Stream', label + ': one plain header for the playing stream');
  if (!h) { return; }
  check(p.$('.jellywave-cover').style.backgroundImage === 'url("img/item-plain?tag=tp&size=600")', label + ': the logo is the item\'s image, by tag');
  check(p.$('.jellywave-title-text').textContent === 'Plain <b>FM</b>', label + ': the name, as text');
  check(p.$('.jellywave-genre').textContent === 'Eclectic · <i>Seattle</i>' && p.shown(p.$('.jellywave-genre')), label + ': the genre, as text');
  check(p.$('.jellywave-notes').textContent === 'Listener-supported, <img src=x onerror=alert(1)> 90.3 FM.' && p.shown(p.$('.jellywave-notes')), label + ': the notes, markup shown literally');
  check(p.$('.jellywave-format-text').textContent === 'MP3 · 128 kbps', label + ': the format line (codec and bitrate; no delivery since L10)');
  const link = p.$('.jellywave-website');
  check(link.getAttribute('href') === 'https://www.plain.example/about?x=1' && link.textContent === 'plain.example ↗' && p.shown(link), label + ': the website link and its host');
  check(link.getAttribute('target') === '_blank' && link.getAttribute('rel') === 'noopener noreferrer', label + ': opened in a new tab with rel="noopener noreferrer"');
  check(p.shown(p.$('.jellywave-stop')), label + ': Stop');
  check(['.jellywave-request', '.jellywave-like', '.jellywave-lyrics', '.jellywave-schedule', '.jellywave-recent', '.jellywave-dj', '.jellywave-sub'].every(sel => p.$(sel) === null),
    label + ': no request form, heart, lyrics, schedule, recent list, DJ or track line');
  check(h.querySelectorAll('b').length === 0 && h.querySelectorAll('i').length === 0 && h.querySelectorAll('img').length === 0, label + ': no element was made from station text');
}

scenario('L8 a plain stream playing: its header, drawn from the list alone', async () => {
  const p = await open({ model: plainModel() });
  await p.serve();
  const before = p.requests.length;
  await p.play('item-plain');
  check(p.pending('Jellywave/Stations').length === 1, 'playback start asks for the list');
  await p.serve();
  checkPlainHeader(p, 'L8 started');
  check(p.header().parentNode === p.view && p.view.children.indexOf(p.header()) === 0, 'L8 above the toolbar row');
  await p.advance(120000);
  await p.serve();
  const asked = p.requests.slice(before).map(r => r.url);
  check(asked.length > 0 && asked.every(u => u === 'Jellywave/Stations' || u === 'Jellywave/Title?stationId=plain'), 'L8 only the list (and since L6 the stream\'s title) is asked while the plain stream plays (' + asked.join() + ')');
  check(!p.requests.some(r => stationRoute(r.url)), 'L8 no SUB/WAVE route is called for a plain stream');
  check(p.barText.querySelectorAll('a')[0].textContent === '' && !p.barImage.style.backgroundImage && p.navigator.mediaSession.metadata === null, 'L8 the bar and the media session keep Jellyfin\'s own');
  check(p.card('item-plain').style.backgroundImage === 'url("original-item-plain")', 'L8 its card is untouched');
  const header = p.header();
  for (const name of ['pause', 'unpause', 'statechange']) { await p.emit(name); await p.advance(60); }
  await p.serve();
  check(p.header() === header && p.headers().length === 1, 'L8 pause, unpause and statechange keep the plain header');
  check(!p.requests.some(r => stationRoute(r.url)), 'L8 nor do they call a SUB/WAVE route');
  check(p.warnings.length === 0, 'L8 no warnings (' + p.warnings.join('; ') + ')');
  p.$('.jellywave-stop').click();
  await p.advance(60);
  check(p.pm.stops === 1 && p.headers().length === 0, 'L8 Stop stops playback and the header goes');
  const count = p.requests.length;
  await p.advance(60000);
  check(p.requests.length === count, 'L8 after the stop the poll stops');
});

scenario('L8 a plain stream playing at boot, and the tile opened later', async () => {
  const p = await open({ model: plainModel(), playing: 'item-plain' });
  await p.serve();
  checkPlainHeader(p, 'L8 at boot');
  const q = await open({ model: plainModel(), noView: true });
  await q.serve();
  await q.play('item-plain');
  await q.serve();
  const count = q.requests.length;
  await q.advance(120000);
  check(q.headers().length === 0 && q.requests.length === count, 'L8 off the tile page: no header and no polling for a plain stream');
  q.document.body.appendChild(q.view);
  await q.viewshow(q.view);
  checkPlainHeader(q, 'L8 viewshow');
});

scenario('L8 switching SUB/WAVE → plain → SUB/WAVE rebuilds the header each way', async () => {
  const p = await open({ model: plainModel() });
  const late = r => /^Jellywave\/(Like|Schedule|Avatar)/.test(r.url);
  await p.play('item-live');
  await p.serve(late);
  const first = p.header();
  check(first.getAttribute('data-kind') === 'SubWave' && p.$('.jellywave-title-text').textContent === 'Title live', 'L8 the SUB/WAVE header first');
  await p.advance(300);
  p.observing = false;   // the event and tick paths alone

  await p.play('item-plain');
  await p.serve(late);
  check(!p.document.body.contains(first), 'L8 the SUB/WAVE header is gone');
  checkPlainHeader(p, 'L8 after SUB/WAVE');
  const plainHeader = p.header();
  await p.answer('Jellywave/Like?stationId=live', { Enabled: true, SongId: 'song-live', Liked: true, Count: 9 });
  await p.answer('Jellywave/Schedule?stationId=live', { Available: true, Today: [{ StartHour: 0, EndHour: 24, Show: 'Grid live', Now: true }] });
  await p.answer('Jellywave/Avatar/dj_live', { blob: () => Promise.resolve({ size: 1 }) });
  check(p.header() === plainHeader && !plainHeader.textContent.includes('live') && p.blobs === 0, 'L8 late SUB/WAVE replies change nothing on the plain header');
  const before = p.requests.length;
  await p.advance(10000);
  await p.serve();
  check(!p.requests.slice(before).some(r => stationRoute(r.url)), 'L8 no feed is asked while the plain stream plays');

  await p.play('item-two');
  await p.serve();
  const back = p.header();
  check(p.headers().length === 1 && back !== plainHeader && !p.document.body.contains(plainHeader), 'L8 the plain header is gone');
  check(back.getAttribute('data-station') === 'two' && back.getAttribute('data-kind') === 'SubWave', 'L8 the SUB/WAVE header for the new station');
  check(p.$('.jellywave-title-text').textContent === 'Title two' && p.$('.jellywave-request') !== null && p.$('.jellywave-website') === null, 'L8 with its feed and request form, and no website link');
  check(p.barText.querySelectorAll('a')[0].textContent === 'Artist two – Title two', 'L8 the bar is repainted again');

  await p.play('item-plain');
  await p.serve();
  checkPlainHeader(p, 'L8 plain again');
  await p.stop();
  await p.advance(60);
  check(p.headers().length === 0, 'L8 a stop removes the plain header');
});

scenario('L8 a website that is not http or https never becomes an href', async () => {
  for (const site of ['javascript:alert(1)', 'JavaScript:alert(1)//https://x.example', ' https://x.example/', 'data:text/html,<b>x</b>', 'vbscript:x', 'ftp://x.example/',
    'https://u:p@x.example/', 'https://x.example/a b', 'https://x.example/\nnext', '//x.example/', 'https://x.example/' + 'a'.repeat(2048), 42, null,
    'https://\u0430pple.example/', 'https://a\u2028b.example/', 'https://x.example/\u00e9', 'https://x.example\u2028/']) {
    const model = plainModel();
    model.stations[2].Website = site;
    const p = await open({ model, playing: 'item-plain' });
    await p.serve();
    const link = p.$('.jellywave-website');
    check(!!link && link.getAttribute('href') === null && link.href === '' && !p.shown(link) && link.textContent === '', 'L8 no link for ' + JSON.stringify(typeof site === 'string' ? site.slice(0, 40) : site));
  }
  const p = await open({ model: plainModel(), playing: 'item-plain' });
  await p.serve();
  check(p.$('.jellywave-website').getAttribute('href') === 'https://www.plain.example/about?x=1', 'L8 a good link first');
  p.model.stations[2].Website = 'javascript:alert(1)';
  await p.advance(65000);
  await p.serve();
  check(p.$('.jellywave-website').getAttribute('href') === null && !p.shown(p.$('.jellywave-website')), 'L8 a list refresh with a bad website takes the href away');
  p.model.stations[2].Website = null;
  p.model.stations[2].Genre = null;
  p.model.stations[2].Notes = '';
  p.model.stations[2].BitrateKbps = null;
  await p.advance(65000);
  await p.serve();
  check(!p.shown(p.$('.jellywave-genre')) && !p.shown(p.$('.jellywave-notes')) && p.$('.jellywave-format-text').textContent === 'MP3', 'L8 empty fields are hidden; no bitrate leaves it out of the format line');
});

scenario('L10 the format line names no delivery, even from an older server that still sends one', async () => {
  const model = plainModel();
  model.stations[2].Delivery = 'Direct';
  const p = await open({ model });
  await p.serve();
  await p.play('item-plain');
  await p.serve();
  check(p.$('.jellywave-format-text').textContent === 'MP3 · 128 kbps', 'L10 an older answer\'s Delivery is not shown');
  check(!/Relay|Direct/.test(p.header().textContent), 'L10 the header names no delivery mode');
});

scenario('L8 a SUB/WAVE header shows the station\'s notes when set', async () => {
  const model = baseModel();
  model.stations[0].Notes = 'Late-night <b>static</b>.';
  const p = await open({ model });
  await playStation(p, 'item-live');
  check(p.$('.jellywave-notes').textContent === 'Late-night <b>static</b>.' && p.shown(p.$('.jellywave-notes')) && p.header().querySelectorAll('b').length === 0, 'L8 the notes line, as text');
  check(p.$('.jellywave-website') === null && p.$('.jellywave-format') === null, 'L8 no website or format line on a SUB/WAVE header');
  await playStation(p, 'item-two');
  check(!p.shown(p.$('.jellywave-notes')) && p.$('.jellywave-notes').textContent === '', 'L8 a station without notes: no notes line');
});

scenario('L8 a station whose kind changes under the same id gets the other header', async () => {
  const model = plainModel();
  const p = await open({ model });
  await playStation(p, 'item-live');
  check(p.header().getAttribute('data-kind') === 'SubWave', 'L8 SUB/WAVE first');
  // The admin re-checked "live" and it is now a plain stream with the same id and item.
  p.model.stations[0] = Object.assign(plainEntry(), { Id: 'live', ItemId: 'item-live', Name: 'Live as plain' });
  await p.advance(60000);
  await p.serve(r => r.url.startsWith('Jellywave/Feed'));   // the list's answer alone, before the feed's 404 could
  check(p.headers().length === 1 && p.header().getAttribute('data-kind') === 'Stream', 'L8 the list\'s answer alone rebuilds it, before any feed 404');
  await p.serve();
  check(p.headers().length === 1 && p.header().getAttribute('data-station') === 'live' && p.header().getAttribute('data-kind') === 'Stream', 'L8 the list refresh rebuilds it as a plain header');
  check(p.$('.jellywave-title-text').textContent === 'Live as plain' && p.$('.jellywave-request') === null, 'L8 drawn as a plain stream');
  const before = p.requests.length;
  await p.advance(10000);
  await p.serve();
  check(!p.requests.slice(before).some(r => stationRoute(r.url)), 'L8 and no SUB/WAVE route is called for it any more');
});

scenario('L8 plain streams only: header and drawer link', async () => {
  const model = plainModel();
  model.stations = [plainEntry()];
  const p = await open({ model, drawer: true });
  await p.serve();
  check(p.document.querySelectorAll('.jellywave-link').length === 1, 'L8 the drawer link shows for a tile of plain streams');
  await playStation(p, 'item-plain');
  checkPlainHeader(p, 'L8 plain only');
  check(!p.requests.some(r => stationRoute(r.url)), 'L8 no SUB/WAVE route called');
});

scenario('L8 fix: an international host shows as punycode', async () => {
  const model = plainModel();
  model.stations[2].Website = 'https://xn--pple-43d.example/';   // what the server serves for a Cyrillic look-alike host
  const p = await open({ model, playing: 'item-plain' });
  await p.serve();
  const link = p.$('.jellywave-website');
  check(link.getAttribute('href') === 'https://xn--pple-43d.example/' && link.textContent === 'xn--pple-43d.example ↗', 'L8 fix the link and its text name the punycode host');
});

scenario('L8 fix (J31): a missing or unknown kind gets the plain header, which asks nothing', async () => {
  for (const kind of [undefined, null, 'Radio', 'subwave', 1]) {
    const model = plainModel();
    model.stations[0] = { Id: 'live', Name: 'Live One', ItemId: 'item-live', ImageTag: 'tl' };
    if (kind !== undefined) { model.stations[0].Kind = kind; }
    const p = await open({ model });
    await playStation(p, 'item-live');
    await p.advance(10000);
    await p.serve();
    check(p.headers().length === 1 && p.header().getAttribute('data-kind') === 'Stream' && p.$('.jellywave-request') === null,
      'L8 fix kind ' + JSON.stringify(kind) + ': the plain header');
    check(!p.requests.some(r => stationRoute(r.url)), 'L8 fix kind ' + JSON.stringify(kind) + ': no SUB/WAVE route is called');
  }
});

scenario('L8 fix (J26): leaving the tile page while a plain header shows', async () => {
  const p = await open({ model: plainModel() });
  await playStation(p, 'item-plain');
  const header = p.header();
  check(!!header && header.getAttribute('data-kind') === 'Stream', 'L8 fix the plain header on the tile page');
  // jellyfin-web hides the tile's view (it stays in the document, cached) and shows another one.
  p.view.classList.add('hide');
  const other = p.document.createElement('div');
  other.className = 'mainAnimatedPage page';
  p.document.body.appendChild(other);
  await p.viewshow(other);
  const before = p.requests.length;
  await p.advance(180000);
  await p.serve();
  const asked = p.requests.slice(before).map(r => r.url);
  check(p.document.body.contains(header) && p.headers().length === 1, 'L8 fix the header stays in the cached view');
  const lists = asked.filter(u => u === 'Jellywave/Stations').length;
  check(asked.every(u => u === 'Jellywave/Stations' || u === 'Jellywave/Title?stationId=plain') && lists <= 3, 'L8 fix off the page, the tick reads only the list, at most once a minute, and (L6) the cached header\'s title (' + lists + ')');
  p.view.classList.remove('hide');
  other.classList.add('hide');
  await p.viewshow(p.view);
  check(p.header() === header && p.headers().length === 1, 'L8 fix back on the tile page: the same header, no second one');
  p.view.classList.add('hide');
  await p.viewshow(other);
  await p.stop();
  await p.advance(60);
  check(p.headers().length === 0, 'L8 fix a stop off the page removes the cached view\'s header');
  const count = p.requests.length;
  await p.advance(120000);
  check(p.requests.length === count, 'L8 fix then the timer stops');
});

// ---------------------------------------------------------------- card L6: the "Now:" line for plain streams
// The plain header asks GET Jellywave/Title for its stream and shows "Now: …": "Querying upstream…" from playback start,
// "Delayed response: is the stream up?" after 30 s without a title or while the server's samples fail, "Not supplied by
// the station" at once for a stream that sends no titles, else the title as sent. The bar shows "Artist – Title".
const NOW_QUERYING = 'Querying upstream…';
const NOW_DELAYED = 'Delayed response: is the stream up?';
const NOW_NONE = 'Not supplied by the station';
const nowOf = p => (p.$('.jellywave-now-text') ? p.$('.jellywave-now-text').textContent : null);
const titleCalls = p => p.requests.filter(r => r.url.startsWith('Jellywave/Title'));
const barLine = (p, i) => p.barText.querySelectorAll('a')[i].textContent;

scenario('L6 the Now line: querying until 30 s after playback start, then delayed, then the title', async () => {
  const p = await open({ model: plainModel() });
  await p.serve();
  const started = p.now;
  await p.play('item-plain');
  await p.serve();
  check(p.$('.jellywave-now-label').textContent === 'Now:' && p.shown(p.$('.jellywave-now')), 'L6 the plain header has a "Now:" line');
  check(titleCalls(p).length === 1 && titleCalls(p)[0].url === 'Jellywave/Title?stationId=plain', 'L6 the header asks for its stream\'s title at once (' + titleCalls(p).map(r => r.url).join() + ')');
  check(nowOf(p) === NOW_QUERYING, 'L6 waiting: "' + NOW_QUERYING + '" (' + nowOf(p) + ')');
  check(barLine(p, 0) === '' && p.navigator.mediaSession.metadata === null, 'L6 no title yet: the bar keeps Jellyfin\'s own');
  await p.advance(29999 - (p.now - started));
  check(nowOf(p) === NOW_QUERYING, 'L6 29.999 s after playback start: still querying');
  await p.advance(1);
  check(nowOf(p) === NOW_DELAYED, 'L6 30 s after playback start with no title: "' + NOW_DELAYED + '" (' + nowOf(p) + ')');
  p.model.titles = { plain: { State: 'Title', Title: 'Artist - Title' } };
  await p.serve();
  await p.advance(5000);
  await p.serve();
  check(nowOf(p) === 'Artist - Title', 'L6 the title as sent (' + nowOf(p) + ')');
  check(barLine(p, 0) === 'Artist – Title' && barLine(p, 1) === 'Plain <b>FM</b>', 'L6 the bar: "Artist – Title", then the station (' + barLine(p, 0) + ' | ' + barLine(p, 1) + ')');
  const md = p.navigator.mediaSession.metadata;
  check(!!md && md.title === 'Title' && md.artist === 'Artist', 'L6 the media session: title and artist split');
  p.model.titles = { plain: { State: 'Title', Title: 'Next One - Second Song' } };
  await p.advance(5000);
  await p.serve();
  check(nowOf(p) === 'Next One - Second Song' && barLine(p, 0) === 'Next One – Second Song', 'L6 a new title replaces the old in the line and the bar');
  check(p.warnings.length === 0, 'L6 no warnings (' + p.warnings.join('; ') + ')');
});

scenario('L6 the boundary counts from playback start, not from the first answer', async () => {
  const p = await open({ model: plainModel() });
  // A SUB/WAVE station first, so the 5 s tick keeps a phase 2 s off the plain stream's start: at 30 s only the line's
  // own boundary timer can redraw it.
  await playStation(p, 'item-live');
  await p.advance(2000);
  await p.serve();
  const started = p.now;
  await p.play('item-plain');
  await p.serve(r => r.url.startsWith('Jellywave/Title'));   // the list at once, the title not yet
  p.observing = false;   // no DOM pass either
  await p.advance(25000);
  await p.answer('Jellywave/Title?stationId=plain', { StationId: 'plain', State: 'Waiting' });
  check(nowOf(p) === NOW_QUERYING, 'L6 the first answer 25 s in: querying');
  await p.advance(30000 - (p.now - started) - 1);
  check(nowOf(p) === NOW_QUERYING, 'L6 just before 30 s: querying');
  await p.advance(1);
  check(nowOf(p) === NOW_DELAYED, 'L6 at 30 s from playback start: delayed');
  // Stop and play again: the wait starts afresh.
  await p.stop();
  await p.advance(60);
  await p.play('item-plain');
  await p.serve();
  check(nowOf(p) === NOW_QUERYING, 'L6 a new playback start: querying again');
});

scenario('L6 a stream that sends no titles reads the not-supplied text at once and keeps it', async () => {
  const model = plainModel();
  model.titles = { plain: { State: 'NoTitles' } };
  const p = await open({ model });
  await p.serve();
  await p.play('item-plain');
  await p.serve();
  check(nowOf(p) === NOW_NONE, 'L6 NoTitles: "' + NOW_NONE + '" at once (' + nowOf(p) + ')');
  for (let i = 0; i < 12; i++) { await p.advance(5000); await p.serve(); }
  check(nowOf(p) === NOW_NONE, 'L6 NoTitles: still the not-supplied text a minute later, never delayed');
  check(barLine(p, 0) === '' && p.navigator.mediaSession.metadata === null, 'L6 NoTitles: the bar keeps Jellyfin\'s own');
});

scenario('L6 failing samples read the delayed text; a title that comes back replaces it', async () => {
  const model = plainModel();
  model.titles = { plain: { State: 'Failing' } };
  const p = await open({ model });
  await p.serve();
  await p.play('item-plain');
  await p.serve();
  check(nowOf(p) === NOW_DELAYED, 'L6 Failing: the delayed text, at once (' + nowOf(p) + ')');
  p.model.titles = { plain: { State: 'Title', Title: 'Back - Again' } };
  await p.advance(5000);
  await p.serve();
  check(nowOf(p) === 'Back - Again', 'L6 a title after failing replaces the delayed text');
  p.model.titles = { plain: { State: 'Failing' } };
  await p.advance(5000);
  await p.serve();
  check(nowOf(p) === NOW_DELAYED, 'L6 failing again after a title: delayed (' + nowOf(p) + ')');
  check(barLine(p, 0) === 'Back – Again', 'L6 the bar keeps its last title while failing, as a SUB/WAVE bar keeps its last track');
});

scenario('L6 the title route failing (network) keeps the 30 s rule', async () => {
  const model = plainModel();
  model.titleFails = 'network';
  const p = await open({ model });
  await p.serve();
  const started = p.now;
  await p.play('item-plain');
  await p.serve();
  check(nowOf(p) === NOW_QUERYING && p.headers().length === 1, 'L6 no answer: querying, the header stays');
  await p.advance(30000 - (p.now - started));
  await p.serve();
  check(nowOf(p) === NOW_DELAYED && p.headers().length === 1, 'L6 no answer for 30 s: delayed');
  p.model.titleFails = 500;
  await p.advance(5000);
  await p.serve();
  check(nowOf(p) === NOW_DELAYED && p.headers().length === 1, 'L6 a 500: delayed, the header stays');
});

scenario('L6 title text goes in as text only, in the line and the bar', async () => {
  const model = plainModel();
  model.titles = { plain: { State: 'Title', Title: '<b>Bold</b> & co - <img src=x onerror=alert(1)>' } };
  const p = await open({ model });
  await p.serve();
  await p.play('item-plain');
  await p.serve();
  check(nowOf(p) === '<b>Bold</b> & co - <img src=x onerror=alert(1)>', 'L6 markup in a title shows literally');
  check(p.header().querySelectorAll('b').length === 0 && p.header().querySelectorAll('img').length === 0, 'L6 no element made from title text in the header');
  check(barLine(p, 0) === '<b>Bold</b> & co – <img src=x onerror=alert(1)>' && p.bar.querySelectorAll('b').length === 0 && p.bar.querySelectorAll('img').length === 0, 'L6 nor in the bar');
  check(!p.innerHTMLs.some(h => h.includes('Bold') || h.includes('onerror')), 'L6 the title never reaches innerHTML');
});

scenario('L6 the playing bar splits at the first " - " only', async () => {
  for (const [title, line0, mdTitle, mdArtist] of [
    ['Artist - Title - Remix', 'Artist – Title - Remix', 'Title - Remix', 'Artist'],
    ['Just a title', 'Just a title', 'Just a title', 'Plain <b>FM</b>'],
    [' - Leading dash', ' - Leading dash', ' - Leading dash', 'Plain <b>FM</b>'],
    ['Trailing dash - ', 'Trailing dash - ', 'Trailing dash - ', 'Plain <b>FM</b>'],
    ['Hyphen-in-name - Song', 'Hyphen-in-name – Song', 'Song', 'Hyphen-in-name'],
    ['Artist – Title (en dash)', 'Artist – Title (en dash)', 'Artist – Title (en dash)', 'Plain <b>FM</b>']
  ]) {
    const model = plainModel();
    model.titles = { plain: { State: 'Title', Title: title } };
    const p = await open({ model });
    await p.serve();
    await p.play('item-plain');
    await p.serve();
    const md = p.navigator.mediaSession.metadata;
    check(nowOf(p) === title && barLine(p, 0) === line0 && !!md && md.title === mdTitle && md.artist === mdArtist,
      'L6 ' + JSON.stringify(title) + ' → bar ' + JSON.stringify(barLine(p, 0)) + ', media ' + JSON.stringify(md && [md.title, md.artist]));
  }
});

scenario('L6 no title call for a SUB/WAVE station or a station of unknown kind', async () => {
  const model = plainModel();
  model.titles = { plain: { State: 'Title', Title: 'A - B' }, live: { State: 'Title', Title: 'never' } };
  model.stations.push({ Id: 'odd', Name: 'Odd', Kind: 'Radio', ItemId: 'item-odd', ImageTag: 'to' });
  const p = await open({ model });
  await playStation(p, 'item-live');
  for (let i = 0; i < 6; i++) { await p.advance(5000); await p.serve(); }
  check(titleCalls(p).length === 0, 'L6 a SUB/WAVE station playing: no title call');
  check(p.$('.jellywave-now') === null && barLine(p, 0) === 'Artist live – Title live', 'L6 its header has no Now line; its bar follows its feed');
  await playStation(p, 'item-odd');
  for (let i = 0; i < 6; i++) { await p.advance(5000); await p.serve(); }
  check(titleCalls(p).length === 0, 'L6 an unknown kind: no title call');
  check(p.header().getAttribute('data-kind') === 'Stream' && !p.shown(p.$('.jellywave-now')), 'L6 an unknown kind: the plain header without a Now line');
});

scenario('L6 the title is asked about every 5 s while the header shows, and never by a page that never showed it', async () => {
  const p = await open({ model: plainModel() });
  await playStation(p, 'item-plain');
  const before = titleCalls(p).length;
  for (let i = 0; i < 12; i++) { await p.advance(5000); await p.serve(); }
  const asked = titleCalls(p).length - before;
  check(asked >= 10 && asked <= 13, 'L6 about one title call per 5 s over a minute (' + asked + ')');
  await p.advance(30000);
  check(p.pending('Jellywave/Title').length === 1, 'L6 one title request at a time: unanswered, the ticks add none (' + p.pending('Jellywave/Title').length + ')');
  const q = await open({ model: plainModel(), noView: true });
  await q.serve();
  await q.play('item-plain');
  await q.serve();
  for (let i = 0; i < 12; i++) { await q.advance(5000); await q.serve(); }
  check(titleCalls(q).length === 0 && q.headers().length === 0, 'L6 a page that never showed the header (and so never painted the bar): no title call');
});

scenario('L6 polling stops on stop, and a switch drops the previous stream\'s late title', async () => {
  const model = plainModel();
  model.stations.push(Object.assign(plainEntry(), { Id: 'plain2', Name: 'Second', ItemId: 'item-plain2', ImageTag: 'tp2' }));
  model.titles = { plain: { State: 'Title', Title: 'First - Stream' }, plain2: { State: 'Title', Title: 'Second - Stream' } };
  const p = await open({ model });
  await playStation(p, 'item-plain');
  check(nowOf(p) === 'First - Stream', 'L6 the first stream\'s title');
  // Stop: the header goes and no title is asked any more.
  p.$('.jellywave-stop').click();
  await p.advance(60);
  await p.serve();
  const afterStop = titleCalls(p).length;
  for (let i = 0; i < 12; i++) { await p.advance(5000); await p.serve(); }
  check(p.headers().length === 0 && titleCalls(p).length === afterStop, 'L6 after Stop: no header and no title call');

  // A switch while a title reply for the first stream is on its way: the reply is dropped.
  await playStation(p, 'item-plain');
  await p.advance(5000);   // a title call for "plain" now pending
  check(p.pending('Jellywave/Title?stationId=plain').length === 1, 'L6 a title call for the first stream is on its way');
  await p.play('item-plain2');
  await p.serve(r => r.url === 'Jellywave/Title?stationId=plain');
  check(nowOf(p) === 'Second - Stream' && p.header().getAttribute('data-station') === 'plain2', 'L6 the second stream\'s header shows its own title');
  await p.answer('Jellywave/Title?stationId=plain', { StationId: 'plain', State: 'Title', Title: 'Late - Reply' });
  check(nowOf(p) === 'Second - Stream' && barLine(p, 0) === 'Second – Stream', 'L6 the late reply for the first stream changes nothing');
  check(!p.requests.slice(-3).some(r => r.url === 'Jellywave/Title?stationId=plain' && !r.done), 'L6 the first stream is not asked again');

  // A switch to a SUB/WAVE station: title calls stop, its feed repaints the bar.
  await playStation(p, 'item-two');
  const atSwitch = titleCalls(p).length;
  for (let i = 0; i < 6; i++) { await p.advance(5000); await p.serve(); }
  check(titleCalls(p).length === atSwitch && barLine(p, 0) === 'Artist two – Title two', 'L6 on a SUB/WAVE station: no title call, its own track in the bar');
});

scenario('L6 a reply from before a stop and restart of the same stream is dropped', async () => {
  const p = await open({ model: plainModel() });
  await p.serve();
  await p.play('item-plain');
  await p.serve(r => r.url.startsWith('Jellywave/Title'));
  const old = p.pending('Jellywave/Title')[0];
  await p.stop();
  await p.advance(60);
  await p.play('item-plain');
  await p.serve(r => r.url.startsWith('Jellywave/Title'));
  check(p.pending('Jellywave/Title').length === 2, 'L6 the old and the new title request are both on their way');
  old.done = true; old.resolve({ StationId: 'plain', State: 'Title', Title: 'Old - Reply' });
  await p.advance(5000);
  check(nowOf(p) === NOW_QUERYING && barLine(p, 0) === '', 'L6 the reply asked before the stop changes nothing (' + nowOf(p) + ')');
});

scenario('L6 a switch to another plain stream starts its line afresh', async () => {
  const model = plainModel();
  model.stations.push(Object.assign(plainEntry(), { Id: 'plain2', Name: 'Second', ItemId: 'item-plain2', ImageTag: 'tp2' }));
  model.titles = { plain: { State: 'Title', Title: 'First - Stream' } };
  const p = await open({ model });
  await playStation(p, 'item-plain');
  check(nowOf(p) === 'First - Stream', 'L6 the first stream\'s title');
  await p.play('item-plain2');
  await p.serve(r => r.url.startsWith('Jellywave/Title'));
  check(p.header().getAttribute('data-station') === 'plain2' && nowOf(p) === NOW_QUERYING, 'L6 the second stream: querying, not the first stream\'s title (' + nowOf(p) + ')');
  await p.advance(5000);
  check(nowOf(p) === NOW_QUERYING, 'L6 still querying on the next tick');
});

scenario('L6 jellyfin-web rebuilding the bar on pause or unpause gets the title back', async () => {
  const model = plainModel();
  model.titles = { plain: { State: 'Title', Title: 'Artist - Title' } };
  const p = await open({ model });
  await playStation(p, 'item-plain');
  check(barLine(p, 0) === 'Artist – Title', 'L6 the title in the bar');
  for (const name of ['pause', 'unpause', 'statechange']) {
    p.barText.querySelectorAll('a')[0].textContent = 'Plain FM (Jellyfin\'s own)';
    await p.emit(name);
    await p.advance(60);
    check(barLine(p, 0) === 'Artist – Title', 'L6 after ' + name + ' the bar shows the title again');
  }
});

scenario('L6 a 403 for the title refuses the page', async () => {
  const model = plainModel();
  const p = await open({ model, drawer: true });
  await playStation(p, 'item-plain');
  p.model.titleFails = 403;
  await p.advance(5000);
  await p.serve();
  check(p.headers().length === 0 && p.document.querySelectorAll('.jellywave-link').length === 0, 'L6 a 403 removes the header and the drawer link');
});

scenario('L6 fix (RA R2): the bar keeps following the title after the header leaves the document', async () => {
  const p = await open({ model: plainModel() });
  await p.serve();
  p.model.titles = { plain: { State: 'Title', Title: 'Old - Song' } };
  await p.play('item-plain');
  await p.serve();
  check(barLine(p, 0) === 'Old – Song', 'L6 fix the title in the bar first');
  // jellyfin-web evicts the cached tile view (removed from the document) and shows another one.
  p.view.parentNode.removeChild(p.view);
  const other = p.document.createElement('div');
  other.className = 'mainAnimatedPage page';
  p.document.body.appendChild(other);
  await p.viewshow(other);
  p.model.titles = { plain: { State: 'Title', Title: 'New - Song' } };
  const before = titleCalls(p).length;
  for (let i = 0; i < 6; i++) { await p.advance(5000); await p.serve(); }
  check(p.headers().length === 0, 'L6 fix the header is gone with its view');
  check(titleCalls(p).length - before >= 4, 'L6 fix the title is still asked for the bar (' + (titleCalls(p).length - before) + ')');
  check(barLine(p, 0) === 'New – Song' && p.navigator.mediaSession.metadata.title === 'Song', 'L6 fix the bar follows the new title (' + barLine(p, 0) + ')');
  p.model.titles = { plain: { State: 'NoTitles' } };
  await p.advance(5000); await p.serve();
  check(barLine(p, 0) === 'New – Song', 'L6 fix without a title the bar keeps its last one');
  await p.stop();
  await p.advance(60);
  const atStop = titleCalls(p).length;
  for (let i = 0; i < 6; i++) { await p.advance(5000); await p.serve(); }
  check(titleCalls(p).length === atStop, 'L6 fix after a stop: no title call');
  // Played again with no header anywhere: Jellyfin's bar is its own, so nothing is asked or painted.
  await p.play('item-plain');
  await p.serve();
  p.barText.querySelectorAll('a')[0].textContent = 'Jellyfin own';
  const atReplay = titleCalls(p).length;
  for (let i = 0; i < 6; i++) { await p.advance(5000); await p.serve(); }
  check(titleCalls(p).length === atReplay && barLine(p, 0) === 'Jellyfin own', 'L6 fix a new playback without a header: no title call, the bar left alone');
});

scenario('L6 fix (RA R2): a header that leaves before any title painted the bar stops asking', async () => {
  const p = await open({ model: plainModel() });
  await playStation(p, 'item-plain');   // Waiting: nothing painted
  p.view.parentNode.removeChild(p.view);
  await p.advance(5000); await p.serve();
  const before = titleCalls(p).length;
  p.model.titles = { plain: { State: 'Title', Title: 'Late - Title' } };
  for (let i = 0; i < 6; i++) { await p.advance(5000); await p.serve(); }
  check(titleCalls(p).length === before && barLine(p, 0) === '', 'L6 fix no header and nothing painted: no title call, the bar is Jellyfin\'s');
});

scenario('L6 fix: the same stream started again with its header gone leaves the new bar alone', async () => {
  const model = plainModel();
  model.titles = { plain: { State: 'Title', Title: 'Some - Song' } };
  const p = await open({ model });
  await playStation(p, 'item-plain');
  check(barLine(p, 0) === 'Some – Song', 'L6 fix the title in the bar');
  p.view.parentNode.removeChild(p.view);
  await p.advance(5000); await p.serve();
  // The user starts the same item again: jellyfin-web rebuilds the bar from the item.
  await p.play('item-plain');
  await p.serve();
  p.barText.querySelectorAll('a')[0].textContent = 'Jellyfin own';
  await p.emit('pause');
  await p.advance(60);
  for (let i = 0; i < 3; i++) { await p.advance(5000); await p.serve(); }
  check(barLine(p, 0) === 'Jellyfin own', 'L6 fix nothing painted during this playback and no header: the bar is Jellyfin\'s (' + barLine(p, 0) + ')');
});

scenario('L6 fix: a title outside the Title state, or an empty one, is not shown', async () => {
  const model = plainModel();
  model.titles = { plain: { State: 'Failing', Title: 'Stale - Song' } };
  const p = await open({ model });
  await playStation(p, 'item-plain');
  check(nowOf(p) === NOW_DELAYED && barLine(p, 0) === '', 'L6 fix Failing with a title field: the delayed text, the bar untouched');
  p.model.titles = { plain: { State: 'Title', Title: '' } };
  const q = await open({ model: Object.assign(plainModel(), { titles: { plain: { State: 'Title', Title: '' } } }) });
  await playStation(q, 'item-plain');
  check(nowOf(q) === NOW_QUERYING && barLine(q, 0) === '', 'L6 fix an empty title: querying, the bar untouched (' + nowOf(q) + ')');
});

scenario('L6 fix: an HLS stream reads the not-supplied text at once and asks nothing', async () => {
  const model = plainModel();
  model.stations[2].Codec = 'HLS';
  model.titles = { plain: { State: 'Title', Title: 'never asked' } };
  const p = await open({ model });
  await playStation(p, 'item-plain');
  check(nowOf(p) === NOW_NONE, 'L6 fix HLS: "' + NOW_NONE + '" at once (' + nowOf(p) + ')');
  for (let i = 0; i < 12; i++) { await p.advance(5000); await p.serve(); }
  check(titleCalls(p).length === 0 && nowOf(p) === NOW_NONE && barLine(p, 0) === '', 'L6 fix HLS: no title call in a minute, the text stays, the bar is Jellyfin\'s');
});

scenario('L6 fix (HI Q1): with "Show what\'s playing" off the line is hidden and the bar left alone', async () => {
  const model = plainModel();
  model.titles = { plain: { State: 'Off' } };
  const p = await open({ model });
  await playStation(p, 'item-plain');
  check(!p.shown(p.$('.jellywave-now')), 'L6 fix Off: no Now line');
  await p.advance(31000); await p.serve();
  check(!p.shown(p.$('.jellywave-now')) && barLine(p, 0) === '', 'L6 fix Off: still hidden after 30 s, the bar Jellyfin\'s');
  p.model.titles = { plain: { State: 'Title', Title: 'Back - On' } };
  await p.advance(5000); await p.serve();
  check(p.shown(p.$('.jellywave-now')) && nowOf(p) === 'Back - On', 'L6 fix switched on again: the line and the title come back');
});

scenario('L6 fix: a stop clears the 30 s timer', async () => {
  const p = await open({ model: plainModel() });
  await p.serve();
  const started = p.now;
  await p.play('item-plain');
  await p.serve();
  const due = t => t.at === started + 30000 && !t.every;
  check(p.timers.some(due), 'L6 fix the boundary timer is armed at 30 s');
  await p.stop();
  await p.advance(60);
  check(!p.timers.some(due), 'L6 fix a stop clears it');
});

scenario('L6 a reply that names another station is ignored', async () => {
  const p = await open({ model: plainModel() });
  await p.serve();
  await p.play('item-plain');
  await p.serve(r => r.url.startsWith('Jellywave/Title'));
  await p.answer('Jellywave/Title?stationId=plain', { StationId: 'other', State: 'Title', Title: 'Wrong - Station' });
  check(nowOf(p) === NOW_QUERYING && barLine(p, 0) === '', 'L6 a reply for another station changes nothing');
});

scenario('L6 a title 404 re-reads the list; the header goes when the stream left it', async () => {
  const p = await open({ model: plainModel() });
  await playStation(p, 'item-plain');
  check(p.headers().length === 1, 'L6 the plain header first');
  p.model.stations = p.model.stations.filter(s => s.Id !== 'plain');
  await p.advance(5000);
  check(p.pending('Jellywave/Title').length === 1, 'L6 the title is asked');
  await p.fail('Jellywave/Title', 404);
  check(p.pending('Jellywave/Stations').length === 1, 'L6 the 404 re-reads the list at once');
  await p.serve();
  check(p.headers().length === 0, 'L6 the stream left the list: its header goes');
});

// ---------------------------------------------------------------- run
scenario('L14 an unheard feed is asked again within 2 s, and the schedule follows the first heard feed', async () => {
  const p = await open();
  p.model.unheard = new Set(['live']);
  await p.serve();
  await playStation(p, 'item-live');
  const feeds = () => p.requests.filter(r => r.url === 'Jellywave/Feed?stationId=live').length;
  const schedules = () => p.requests.filter(r => r.url.startsWith('Jellywave/Schedule?stationId=live')).length;
  const f0 = feeds();
  const s0 = schedules();
  check(f0 >= 1 && p.$('.jellywave-status').textContent === 'The server has not heard from the station yet.', 'L14 the first feed of an idle station: nothing heard yet (' + f0 + ')');
  await p.advance(1999); await p.serve();
  check(feeds() === f0, 'L14 not asked again before 2 s');
  await p.advance(1); await p.serve();
  check(feeds() === f0 + 1, 'L14 asked again at 2 s, before the 5 s tick (' + feeds() + ')');
  p.model.unheard.delete('live');   // the woken server has asked the station
  await p.advance(2000); await p.serve();
  check(feeds() === f0 + 2 && p.$('.jellywave-title-text').textContent === 'Title live' && p.$('.jellywave-status').textContent === '',
    'L14 the live track shows 4 s after the page opened');
  await p.advance(900); await p.serve();
  check(feeds() === f0 + 2, 'L14 a heard feed is not asked early again');
  // The schedule asked with the unheard feed has no live show; once heard, opening it asks again (not 5 min later).
  p.$('.jellywave-schedule').open = true;
  await p.advance(200); await p.serve();
  check(schedules() === s0 + 1, 'L14 the schedule is asked again once the first heard feed arrived (' + s0 + ' → ' + schedules() + ')');
  check(p.warnings.length === 0, 'L14 no warnings (' + p.warnings.join('; ') + ')');
});

scenario('L14 a station the server never hears is asked early three times only', async () => {
  const p = await open();
  p.model.unheard = new Set(['live']);
  await p.serve();
  await playStation(p, 'item-live');
  const feeds = () => p.requests.filter(r => r.url === 'Jellywave/Feed?stationId=live').length;
  const f0 = feeds();
  const run = async ms => { for (let t = 0; t < ms; t += 100) { await p.advance(100); await p.serve(); } };
  // Early asks at 2 s, 4 s and (after the 5 s tick) 7 s; the 5 s tick asks as before; nothing at 9 s.
  await run(9900);
  check(feeds() === f0 + 4, 'L14 three early asks plus the 5 s tick in 9.9 s (' + (feeds() - f0) + ')');
  await run(100);
  check(feeds() === f0 + 5, 'L14 then every 5 s tick, as before');
});

scenario('L14 another station starts its early asks afresh', async () => {
  const p = await open();
  p.model.unheard = new Set(['live', 'two']);
  await p.serve();
  await playStation(p, 'item-live');
  for (let t = 0; t < 9900; t += 100) { await p.advance(100); await p.serve(); }   // live's three early asks are spent
  await playStation(p, 'item-two');
  const two = () => p.requests.filter(r => r.url === 'Jellywave/Feed?stationId=two').length;
  for (let t = 0; t < 200; t += 100) { await p.advance(100); await p.serve(); }   // past the 10 s tick
  const t0 = two();
  for (let t = 0; t < 4600; t += 100) { await p.advance(100); await p.serve(); }   // up to just before the 15 s tick
  check(t0 >= 1 && two() === t0 + 2, 'L14 the new station gets its own early asks (' + t0 + ' → ' + two() + ')');
});

// Card R2a: the server allows each user 60 station calls a minute (every kind together) and wants the page's normal use
// under a third of that. The busiest minute the page makes: lyrics panel open with lyrics "pending" all minute, a song
// change in the middle, one like, one request whose status stays "pending" all minute. These are page requests; the
// server's caches turn them into fewer station calls (StationCallsCacheTests.The_page_s_busiest_minute_...).
scenario('R2a a busy minute of page use: the requests per route the server budgets', async () => {
  const model = baseModel();
  model.lyricsPending = true;
  model.requestPending = true;
  const p = await open({ model });
  const start = p.requests.length;
  await playStation(p, 'item-live');
  p.$('.jellywave-lyrics').open = true; p.$('.jellywave-lyrics').dispatch('toggle'); await settle();
  await p.serve();
  p.$('.jellywave-like').click();
  p.$('.jellywave-request-text').value = 'play something';
  p.$('.jellywave-request').dispatch('submit');
  await p.serve();
  for (let t = 0; t < 60000; t += 100) {
    if (t === 30000) { p.model.songs = { live: 'song-live-2' }; }
    await p.advance(100);
    await p.serve();
  }
  const minute = p.requests.slice(start).map(r => r.method + ' ' + r.url.split('?')[0]);
  const count = re => minute.filter(u => re.test(u)).length;
  const likeStatus = count(/^GET Jellywave\/Like$/);
  const likes = count(/^POST Jellywave\/Like$/);
  const lyrics = count(/^GET Jellywave\/Lyrics$/);
  const avatars = count(/^GET Jellywave\/Avatar\//);
  const requests = count(/^POST Jellywave\/Request$/);
  const polls = count(/^GET Jellywave\/Request\//);
  console.log('     page requests in the minute: like status ' + likeStatus + ', like ' + likes + ', lyrics ' + lyrics + ', portrait ' + avatars + ', request ' + requests + ', request status ' + polls);
  check(likeStatus === 2 && likes === 1 && requests === 1 && avatars === 1, 'one like status per song (two songs), one like, one request, one portrait');
  check(lyrics >= 8 && lyrics <= 10, 'lyrics: asked again every 8 s while pending, and for the new song (' + lyrics + ')');
  check(polls >= 19 && polls <= 21, 'request status: polled every 3 s while pending (' + polls + ')');
});

// Card R2b: this server's own refusals, 429 (over the user's budget) and 503 (busy, or the receipts are full), each with
// Retry-After in seconds. Nothing reached the station. What the user acted on says when to try again; what the page polls
// is asked again after the wait, not before; a song request is never sent again by itself; a station, user or song change
// in between drops the old answer and its retry.
const RA = seconds => ({ 'Retry-After': String(seconds) });
const NO_ANSWER = 'Could not reach the server. The request may have been sent; it is not sent again by itself.';
const UNCLEAR = 'No clear answer from the server. The request may have reached the station; it is not sent again by itself.';
// Moves the clock in steps, answering everything the page asks on the way (except what keep names), so ticks run as in use.
async function walk(p, ms, keep, step) {
  const by = step || 500;
  for (let t = 0; t < ms; t += by) { await p.advance(by); await p.serve(keep); }
}
const countOf = (p, line) => p.urls().filter(u => u === line).length;
async function submitRequest(p, text) {
  p.$('.jellywave-request-text').value = text;
  p.$('.jellywave-request').dispatch('submit');
  await settle();
}

scenario('R2b a refused like status is asked again after Retry-After, not before', async () => {
  const p = await open();
  await p.play('item-live');
  await p.serve(r => r.url.startsWith('Jellywave/Like'));
  await p.fail('Jellywave/Like?stationId=live', 429, RA(20));
  check(!p.shown(p.$('.jellywave-like')), 'R2b no status yet: no heart');
  await walk(p, 19500);
  check(countOf(p, 'GET Jellywave/Like?stationId=live') === 1, 'R2b not asked again during the 20 s wait, ticks included');
  await walk(p, 500);
  check(countOf(p, 'GET Jellywave/Like?stationId=live') === 2, 'R2b asked again when the wait is over');
  check(p.shown(p.$('.jellywave-like')) && p.$('.jellywave-like-count').textContent === '1', 'R2b and the heart shows');

  const q = await open();
  await q.play('item-live');
  await q.serve(r => r.url.startsWith('Jellywave/Like'));
  await q.fail('Jellywave/Like?stationId=live', 503);
  await walk(q, 9500);
  check(countOf(q, 'GET Jellywave/Like?stationId=live') === 1, 'R2b a 503 without Retry-After waits the default 10 s');
  await walk(q, 500);
  check(countOf(q, 'GET Jellywave/Like?stationId=live') === 2 && q.shown(q.$('.jellywave-like')), 'R2b then asks again');

  const r = await open();
  await r.play('item-live');
  await r.serve(x => x.url.startsWith('Jellywave/Like'));
  const pending = r.pending('Jellywave/Like?stationId=live')[0];
  pending.done = true; pending.reject({ status: 503 }); await settle();   // no headers at all
  await walk(r, 9500);
  check(countOf(r, 'GET Jellywave/Like?stationId=live') === 1, 'R2b a rejection without headers waits the default too');
  await walk(r, 500);
  check(countOf(r, 'GET Jellywave/Like?stationId=live') === 2, 'R2b and asks again after it');
  check(p.warnings.length === 0 && q.warnings.length === 0 && r.warnings.length === 0, 'R2b no warnings (' + p.warnings.concat(q.warnings, r.warnings).join('; ') + ')');
});

scenario('R2b a refused like: the heart says when to try again, is free at once, and nothing is sent again', async () => {
  const p = await open();
  await playStation(p, 'item-live');
  const heart = p.$('.jellywave-like');
  heart.click();
  await settle();
  check(heart.disabled, 'R2b the heart is busy while the like is on its way');
  await p.fail('Jellywave/Like', 429, RA(30));
  check(!heart.disabled && p.shown(heart), 'R2b busy released: the heart is free and still shows');
  check(heart.title === 'Too many requests. Try again in 30 s.', 'R2b its title says when: ' + heart.title);
  const note = p.$('.jellywave-like-note');
  check(p.shown(note) && note.textContent === 'Too many requests. Try again in 30 s.', 'R2b fix: and a line under the actions says it too (no hover on touch or TV)');
  check(p.$('.jellywave-like-count').textContent === '1' && p.$('.jellywave-like-icon').textContent === 'favorite_border', 'R2b the old status stays');
  await walk(p, 29500);
  check(heart.title === 'Too many requests. Try again in 30 s.', 'R2b the title holds through the wait and the ticks');
  await walk(p, 500);
  check(heart.title === 'Like this track', 'R2b and goes when the wait is over: ' + heart.title);
  check(!p.shown(note) && note.textContent === '', 'R2b fix: the line goes with it');
  check(countOf(p, 'POST Jellywave/Like') === 1, 'R2b the like is never sent again by itself');
  heart.click();
  await settle();
  await p.fail('Jellywave/Like', 503);
  check(heart.title === 'The server is busy. Try again in 10 s.' && !heart.disabled, 'R2b a 503 without Retry-After: the default 10 s');
  check(p.shown(note), 'R2b fix: the line shows again');
  heart.click();
  await p.serve();
  check(!p.shown(note), 'R2b fix: a like the station took clears the line before the wait ends');
  check(countOf(p, 'POST Jellywave/Like') === 3 && heart.title === 'Liked' && p.$('.jellywave-like-count').textContent === '2', 'R2b a press after a refusal sends it; the station\'s answer replaces the notice');
});

scenario('R2b a second refused like keeps its own wait when the first one ends', async () => {
  const p = await open();
  await playStation(p, 'item-live');
  const heart = p.$('.jellywave-like');
  heart.click(); await settle();
  await p.fail('Jellywave/Like', 429, RA(10));
  await walk(p, 5000);
  heart.click(); await settle();
  await p.fail('Jellywave/Like', 429, RA(10));
  await walk(p, 5000);
  check(heart.title === 'Too many requests. Try again in 10 s.', 'R2b the first wait ending does not clear the second notice');
  await walk(p, 5000);
  check(heart.title === 'Like this track', 'R2b the second clears at its own end');
});

scenario('R2b fix: refused again in a row, a poll waits twice as long each time, up to 60 s; an answer starts afresh', async () => {
  const p = await open();
  const keepLike = r => r.url.startsWith('Jellywave/Like');
  await p.play('item-live');
  await p.serve(keepLike);
  const like = 'GET Jellywave/Like?stationId=live';
  const waited = [];
  for (let i = 0; i < 7; i++) {
    await p.fail('Jellywave/Like?stationId=live', 503, RA(2));
    const n = countOf(p, like);
    let ms = 0;
    while (countOf(p, like) === n && ms < 120000) { await walk(p, 500, keepLike); ms += 500; }
    waited.push(ms / 1000);
  }
  check(JSON.stringify(waited) === '[2,4,8,16,32,60,60]', 'R2b fix: like status, Retry-After 2 s every time: waits ' + JSON.stringify(waited));

  const q = await open();
  const keepLyrics = r => r.url.startsWith('Jellywave/Lyrics');
  await playStation(q, 'item-live');
  q.$('.jellywave-lyrics').open = true; q.$('.jellywave-lyrics').dispatch('toggle'); await settle();
  await q.fail('Jellywave/Lyrics', 503, RA(2));
  await walk(q, 2000, keepLyrics);
  await q.fail('Jellywave/Lyrics', 503, RA(2));
  check(q.$('.jellywave-lyrics-body').textContent === 'The server is busy. Asking again in 4 s.', 'R2b fix: lyrics refused twice: the panel says the doubled wait');
  await walk(q, 4000, keepLyrics);
  await q.answer('Jellywave/Lyrics', { Status: 'pending', Message: 'Still looking…' });
  await walk(q, 8000, keepLyrics);
  await q.fail('Jellywave/Lyrics', 503, RA(2));
  check(q.$('.jellywave-lyrics-body').textContent === 'The server is busy. Asking again in 2 s.', 'R2b fix: after an answer the next refusal waits its own Retry-After again');
  await walk(q, 2000, keepLyrics);
  await q.fail('Jellywave/Lyrics', 503, RA(2));
  q.model.songs = { live: 'song-live-2' };
  await walk(q, 5000, keepLyrics);
  await q.fail('Jellywave/Lyrics?songId=song-live-2', 503, RA(2));
  check(q.$('.jellywave-lyrics-body').textContent === 'The server is busy. Asking again in 2 s.', 'R2b fix: another song starts afresh');

  const model = baseModel();
  model.songs = { live: 'shared', two: 'shared' };
  const r = await open({ model });
  await r.play('item-live');
  await r.serve(keepLike);
  await r.fail('Jellywave/Like?stationId=live', 503, RA(2));
  await walk(r, 2000, keepLike);
  await r.fail('Jellywave/Like?stationId=live', 503, RA(2));   // the next wait for this song would be 8 s
  await r.play('item-two');
  await r.serve(keepLike);
  await r.fail('Jellywave/Like?stationId=two', 503, RA(2));
  const n = countOf(r, 'GET Jellywave/Like?stationId=two');
  await walk(r, 2000, keepLike);
  check(countOf(r, 'GET Jellywave/Like?stationId=two') === n + 1, 'R2b fix: another station (same song id) starts afresh too');

  const model2 = baseModel();
  model2.requestPending = true;
  const t = await open({ model: model2 });
  const keepPoll = x => x.url.startsWith('Jellywave/Request/');
  await playStation(t, 'item-live');
  await submitRequest(t, 'play something');
  await t.serve(keepPoll);
  const said = [];
  for (let i = 0; i < 3; i++) {
    await t.fail('Jellywave/Request/req-live', 429, RA(1));
    said.push(t.$('.jellywave-request-result').textContent);
    await walk(t, 4000, keepPoll);
  }
  check(JSON.stringify(said) === JSON.stringify(['Too many requests. Checking again in 1 s.', 'Too many requests. Checking again in 2 s.', 'Too many requests. Checking again in 4 s.']),
    'R2b fix: request status refused in a row: ' + JSON.stringify(said));
  await t.answer('Jellywave/Request/req-live', { Status: 'pending' });
  await walk(t, 3000, keepPoll);
  await t.fail('Jellywave/Request/req-live', 429, RA(1));
  check(t.$('.jellywave-request-result').textContent === 'Too many requests. Checking again in 1 s.', 'R2b fix: an answer in between starts afresh');
});

scenario('R2b fix: a new DJ\'s refused portrait does not leave the previous DJ\'s on screen', async () => {
  const p = await open();
  await playStation(p, 'item-live');
  const avatar = p.$('.jellywave-dj-avatar');
  check(avatar.src === 'blob:1' && p.shown(avatar), 'DJ X\'s portrait shows');
  p.model.avatars = { live: 'dj_y' };
  p.model.djs = { live: 'Y' };
  const keep = r => r.url.startsWith('Jellywave/Avatar/dj_y');
  const y = 'GET Jellywave/Avatar/dj_y?stationId=live';
  await walk(p, 5000, keep);
  check(countOf(p, y) === 1 && p.$('.jellywave-dj-text').textContent === 'DJ Y', 'the feed names DJ Y, whose portrait is asked');
  await p.fail('Jellywave/Avatar/dj_y', 429, RA(30));
  check(!p.shown(avatar) && !avatar.src && p.revoked.includes('blob:1'), 'R2b fix: refused: X\'s portrait goes (revoked), nothing shows next to Y\'s name');
  await walk(p, 29500, keep);
  check(countOf(p, y) === 1 && !p.shown(avatar) && !avatar.src, 'R2b fix: and stays hidden through the wait and the ticks');
  await walk(p, 500, keep);
  await p.answer('Jellywave/Avatar/dj_y', { blob: () => Promise.resolve({ size: 1 }) });
  check(countOf(p, y) === 2 && avatar.src === 'blob:2' && p.shown(avatar), 'R2b fix: Y\'s portrait shows once its retry answers');
});

scenario('R2b fix: an old portrait retry does nothing once the DJ changed or the header left the document', async () => {
  const p = await open();
  await p.play('item-live');
  await p.serve(r => r.url.startsWith('Jellywave/Avatar/'));
  await p.fail('Jellywave/Avatar/dj_live', 503, RA(20));
  p.model.avatars = { live: 'dj_y' };
  await walk(p, 6000);
  check(countOf(p, 'GET Jellywave/Avatar/dj_y?stationId=live') === 1 && p.shown(p.$('.jellywave-dj-avatar')), 'the new DJ\'s portrait is asked and shown');
  await walk(p, 20000);
  check(countOf(p, 'GET Jellywave/Avatar/dj_live?stationId=live') === 1 && countOf(p, 'GET Jellywave/Avatar/dj_y?stationId=live') === 1, 'R2b fix: the old DJ\'s retry asks nothing (no second fetch of the new one either)');

  const q = await open();
  await q.play('item-live');
  await q.serve(r => r.url.startsWith('Jellywave/Avatar/'));
  q.observing = false;   // no DOM-change pass: only the retry's own guard stands between it and the detached header
  const ticker = q.timers.find(t => t.every === 5000);
  await q.advance(ticker.at - q.now + 1);
  await q.serve(r => r.url.startsWith('Jellywave/Avatar/'));
  await q.fail('Jellywave/Avatar/dj_live', 503, RA(2));
  q.view.parentNode.removeChild(q.view);   // jellyfin-web dropped the tile's view before the next tick
  await q.advance(2500);
  check(countOf(q, 'GET Jellywave/Avatar/dj_live?stationId=live') === 1, 'R2b fix: the retry for a header that left the document asks nothing');
});

scenario('R2b fix: a portrait refusal that arrives after a switch arms no retry', async () => {
  const p = await open();
  await p.play('item-live');
  await p.serve(r => r.url.startsWith('Jellywave/Avatar/'));
  await p.play('item-two');
  await p.serve(r => r.url.startsWith('Jellywave/Avatar/dj_live'));
  const due = p.now + 3000;
  await p.fail('Jellywave/Avatar/dj_live', 503, RA(3));
  check(!p.timers.some(t => t.at === due && !t.every), 'R2b fix: no timer is armed for the previous station\'s portrait');
  await walk(p, 10000);
  check(countOf(p, 'GET Jellywave/Avatar/dj_live?stationId=live') === 1, 'and it is never asked again');
});

scenario('R2b fix: a refused poll at the last try stops, as an answered one does', async () => {
  const model = baseModel();
  model.requestPending = true;
  const p = await open({ model });
  await playStation(p, 'item-live');
  await submitRequest(p, 'play something');
  const poll = 'GET Jellywave/Request/req-live';
  const keep = r => r.url === 'Jellywave/Request/req-live' && countOf(p, poll) === 31;
  await p.serve(keep);
  for (let i = 0; i < 40 && countOf(p, poll) < 31; i++) { await p.advance(3000); await p.serve(keep); }
  check(countOf(p, poll) === 31 && p.pending('Jellywave/Request/req-live').length === 1, 'the 31st poll (try 30) is on its way');
  await p.fail('Jellywave/Request/req-live', 503, RA(1));
  check(p.$('.jellywave-request-result').textContent === 'Lost contact while waiting.' && !p.$('.jellywave-request-send').disabled, 'R2b fix: refused at the last try: the poll ends and Send is free');
  await walk(p, 120000, null, 5000);
  check(countOf(p, poll) === 31, 'R2b fix: and nothing more is asked');
});

scenario('R2b refused lyrics say when, are asked again after the wait, and reopening the panel does not ask sooner', async () => {
  const p = await open();
  await playStation(p, 'item-live');
  const panel = p.$('.jellywave-lyrics');
  panel.open = true; panel.dispatch('toggle'); await settle();
  await p.fail('Jellywave/Lyrics', 503, RA(2));
  check(p.$('.jellywave-lyrics-body').textContent === 'The server is busy. Asking again in 2 s.', 'R2b the panel says when it asks again: ' + p.$('.jellywave-lyrics-body').textContent);
  const lyrics = 'GET Jellywave/Lyrics?songId=song-live&stationId=live';
  await walk(p, 1000);
  panel.open = false; panel.dispatch('toggle'); panel.open = true; panel.dispatch('toggle'); await settle();
  await walk(p, 500);
  check(countOf(p, lyrics) === 1, 'R2b not asked again during the wait, not even by reopening the panel');
  await walk(p, 500);
  check(countOf(p, lyrics) === 2 && p.$('.jellywave-lyrics-body').textContent === 'words of live', 'R2b asked again after the wait, and shown');

  const q = await open();
  await playStation(q, 'item-live');
  q.$('.jellywave-lyrics').open = true; q.$('.jellywave-lyrics').dispatch('toggle'); await settle();
  await q.fail('Jellywave/Lyrics', 429, RA(45));
  check(q.$('.jellywave-lyrics-body').textContent === 'Too many requests. Asking again in 45 s.', 'R2b a 429 says so');
  await walk(q, 44000, null, 1000);
  check(countOf(q, lyrics) === 1, 'R2b nothing during the 45 s');
  await walk(q, 1000, null, 1000);
  check(countOf(q, lyrics) === 2 && q.$('.jellywave-lyrics-body').textContent === 'words of live', 'R2b then the lyrics');
  check(q.$('.jellywave-lyrics-body').children.length === 1 && q.innerHTMLs.every(h => !h.includes('Try again')), 'R2b the refusal went in as text');
});

scenario('R2b a refused portrait comes back after the wait instead of being hidden for good', async () => {
  const p = await open();
  await p.play('item-live');
  await p.serve(r => r.url.startsWith('Jellywave/Avatar/'));
  const avatar = p.$('.jellywave-dj-avatar');
  const portrait = 'GET Jellywave/Avatar/dj_live?stationId=live';
  await p.fail('Jellywave/Avatar/dj_live', 503, RA(2));
  check(avatar.getAttribute('data-failed') === null && p.blobs === 0 && !p.shown(avatar), 'R2b a refusal does not mark the portrait failed; it waits hidden');
  await walk(p, 1500, r => r.url.startsWith('Jellywave/Avatar/'));
  check(countOf(p, portrait) === 1, 'R2b not asked again before the wait is over');
  await walk(p, 500, r => r.url.startsWith('Jellywave/Avatar/'));
  check(countOf(p, portrait) === 2, 'R2b asked again after it');
  await p.fail('Jellywave/Avatar/dj_live', 429, RA(3));
  await walk(p, 3500, r => r.url.startsWith('Jellywave/Avatar/'));
  check(countOf(p, portrait) === 2 && !p.shown(avatar), 'R2b refused again in a row: Retry-After 3 s, but twice the last wait (4 s)');
  await walk(p, 500, r => r.url.startsWith('Jellywave/Avatar/'));
  check(countOf(p, portrait) === 3, 'R2b asked once more after that');
  await p.answer('Jellywave/Avatar/dj_live', { blob: () => Promise.resolve({ size: 1 }) });
  check(avatar.src.indexOf('blob:') === 0 && p.shown(avatar), 'R2b and the portrait shows');
  await walk(p, 30000);
  check(countOf(p, portrait) === 3, 'R2b and is not asked again');

  const q = await open();
  await q.play('item-live');
  await q.serve(r => r.url.startsWith('Jellywave/Avatar/'));
  await q.fail('Jellywave/Avatar/dj_live', 500);
  check(q.$('.jellywave-dj-avatar').getAttribute('data-failed') === '1' && !q.shown(q.$('.jellywave-dj-avatar')), 'R2b any other failure still hides it, as before');
});

scenario('R2b a refused song request keeps the text, frees Send, says when, and is sent again only by the listener', async () => {
  const p = await open();
  await playStation(p, 'item-live');
  const send = p.$('.jellywave-request-send');
  const out = () => p.$('.jellywave-request-result').textContent;
  await submitRequest(p, 'play something');
  check(send.disabled && out() === 'Sending…', 'R2b Send is busy while the request is on its way');
  await p.fail('Jellywave/Request', 429, RA(40));
  check(out() === 'Too many requests. Try again in 40 s.', 'R2b the box says when: ' + out());
  check(p.$('.jellywave-request-text').value === 'play something', 'R2b the text stays in the box');
  check(!send.disabled, 'R2b busy released: Send is free');
  await walk(p, 120000, null, 1000);
  check(countOf(p, 'POST Jellywave/Request') === 1, 'R2b the request is not sent again by itself');
  p.$('.jellywave-request').dispatch('submit');
  await p.serve();
  check(countOf(p, 'POST Jellywave/Request') === 2 && p.$('.jellywave-request-text').value === '' && out() === 'ack for req-live', 'R2b the listener\'s next press sends it');

  // Retry-After as this server sends it, missing, absurd, unreadable, or as an HTTP date (a proxy's).
  const cases = [
    [503, null, 'The server is busy. Try again in 10 s.'],
    [503, RA(2), 'The server is busy. Try again in 2 s.'],
    [429, RA(119), 'Too many requests. Try again in 119 s.'],
    [429, RA(3000), 'Too many requests. Try again in 50 min.'],
    [429, RA(999999), 'Too many requests. Try again in 60 min.'],
    [429, RA(0), 'Too many requests. Try again in 1 s.'],
    [429, RA('soon'), 'Too many requests. Try again in 10 s.'],
    [429, RA(-5), 'Too many requests. Try again in 10 s.'],
    [429, 'date', 'Too many requests. Try again in 30 s.']
  ];
  for (const [status, headers, text] of cases) {
    const q = await open();
    await playStation(q, 'item-live');
    await submitRequest(q, 'again');
    await q.fail('Jellywave/Request', status, headers === 'date' ? { 'Retry-After': new Date(q.now + 30000).toUTCString() } : headers);
    const said = q.$('.jellywave-request-result').textContent;
    check(said === text && q.$('.jellywave-request-text').value === 'again' && !q.$('.jellywave-request-send').disabled,
      'R2b ' + status + ' with ' + JSON.stringify(headers) + ': "' + said + '"');
  }
});

scenario('R2b a song request with an uncertain outcome is never sent again by itself', async () => {
  for (const status of [null, 500, 502, 504]) {
    const p = await open();
    await playStation(p, 'item-live');
    await submitRequest(p, 'play something');
    await p.fail('Jellywave/Request', status);
    const label = status ? 'HTTP ' + status : 'a network failure';
    check(p.$('.jellywave-request-result').textContent === (status ? UNCLEAR : NO_ANSWER), 'R2b ' + label + ': says it may have been sent: ' + p.$('.jellywave-request-result').textContent);
    check(p.$('.jellywave-request-text').value === 'play something' && !p.$('.jellywave-request-send').disabled, 'R2b ' + label + ': the text stays and Send is free');
    await walk(p, 300000, null, 5000);
    check(countOf(p, 'POST Jellywave/Request') === 1, 'R2b ' + label + ': not sent again in five minutes');
  }
  const q = await open();
  await playStation(q, 'item-live');
  await submitRequest(q, 'play something');
  await q.fail('Jellywave/Request', 400);
  check(q.$('.jellywave-request-result').textContent === 'The server did not take the request.' && q.$('.jellywave-request-text').value === 'play something', 'R2b another 4xx: not taken, the text stays');
});

scenario('R2b a refused request status poll goes on after the wait instead of losing contact', async () => {
  const model = baseModel();
  model.requestPending = true;
  const p = await open({ model });
  await playStation(p, 'item-live');
  await submitRequest(p, 'play something');
  await p.serve(r => r.url.startsWith('Jellywave/Request/'));
  const poll = 'GET Jellywave/Request/req-live';
  check(countOf(p, poll) === 1, 'R2b the first status poll is on its way');
  await p.fail('Jellywave/Request/req-live', 503, RA(5));
  const out = () => p.$('.jellywave-request-result').textContent;
  check(out() === 'The server is busy. Checking again in 5 s.', 'R2b the box says it will check again: ' + out());
  check(p.$('.jellywave-request-send').disabled, 'R2b Send stays busy while the request is still followed');
  await walk(p, 4500, r => r.url.startsWith('Jellywave/Request/'));
  check(countOf(p, poll) === 1, 'R2b not polled during the wait');
  await walk(p, 500, r => r.url.startsWith('Jellywave/Request/'));
  check(countOf(p, poll) === 2, 'R2b polled again after it');
  await p.fail('Jellywave/Request/req-live', 429);
  check(out() === 'Too many requests. Checking again in 10 s.', 'R2b a 429 without Retry-After waits the default');
  await walk(p, 9500, r => r.url.startsWith('Jellywave/Request/'));
  check(countOf(p, poll) === 2, 'R2b still not before the wait');
  p.model.requestPending = false;
  await walk(p, 500);
  check(countOf(p, poll) === 3 && out() === 'ack for req-live' && !p.$('.jellywave-request-send').disabled, 'R2b the outcome arrives and Send is free');
  check(!out().includes('Lost contact'), 'R2b no "lost contact"');
});

scenario('R2b another station in between: no old answer applies and no retry runs', async () => {
  const p = await open();
  await p.play('item-live');
  await p.serve(r => /^Jellywave\/(Like|Avatar)/.test(r.url));
  p.$('.jellywave-lyrics').open = true; p.$('.jellywave-lyrics').dispatch('toggle'); await settle();
  p.model.requestPending = true;
  await submitRequest(p, 'play something');
  await p.answer('Jellywave/Request', { Accepted: true, RequestId: 'req-live', Status: 'pending', Message: 'Sent.' });
  await p.fail('Jellywave/Like?stationId=live', 429, RA(3));
  await p.fail('Jellywave/Lyrics', 503, RA(3));
  await p.fail('Jellywave/Avatar/dj_live', 503, RA(3));
  const armedAt = p.now + 3000;
  await p.fail('Jellywave/Request/req-live', 503, RA(3));
  const armed = p.timers.filter(t => t.at === armedAt && !t.every).map(t => t.id);
  check(p.pending().length === 0 && armed.length === 4, 'R2b four refusals, each with a retry armed (' + armed.length + ')');
  const at = p.requests.length;
  await p.play('item-two');
  await p.serve();
  check(!p.timers.some(t => armed.includes(t.id)), 'R2b the switch cleared every armed retry');
  await walk(p, 15000);
  const after = p.requests.slice(at);
  check(after.some(r => r.url === 'Jellywave/Feed?stationId=two'), 'R2b the new station is asked');
  check(after.every(r => !r.url.includes('stationId=live') && !r.url.includes('req-live')), 'R2b no retry for the previous station: ' + JSON.stringify(after.map(r => r.url).filter(u => u.includes('live'))));
  check(p.header().getAttribute('data-station') === 'two' && p.$('.jellywave-request-result').textContent === '', 'R2b the new header shows nothing of the old one');
});

scenario('R2b another user in between: no retry runs for the previous user', async () => {
  const p = await open();
  await playStation(p, 'item-live');
  p.$('.jellywave-lyrics').open = true; p.$('.jellywave-lyrics').dispatch('toggle'); await settle();
  // Just after a tick, so the retry (2 s) is due before the next one (5 s), which would notice the new user first; and no
  // DOM-change pass (it checks the session too), so the retry's own session check is what is proved.
  p.observing = false;
  const ticker = p.timers.find(t => t.every === 5000);
  await p.advance(ticker.at - p.now + 1);
  await p.serve(r => r.url.startsWith('Jellywave/Lyrics'));
  await p.fail('Jellywave/Lyrics', 429, RA(2));
  p.user = 'user-b';
  const at = p.requests.length;
  await walk(p, 2000);
  check(!p.requests.slice(at).some(r => r.url.startsWith('Jellywave/Lyrics')), 'R2b the retry due before the next tick does not run for the new user');
  await walk(p, 10000);
  check(!p.requests.slice(at).some(r => r.url.startsWith('Jellywave/Lyrics')), 'R2b nor later');
  check(p.header() && p.header().getAttribute('data-station') === 'live' && !p.$('.jellywave-lyrics-body').textContent.includes('Try again'), 'R2b the new user\'s header starts clean');

  const q = await open();
  await q.play('item-live');
  await q.serve(r => r.url.startsWith('Jellywave/Like'));
  await q.fail('Jellywave/Like?stationId=live', 429, RA(2));
  q.token = '';
  const before = q.requests.length;
  await walk(q, 5000);
  check(!q.requests.slice(before).some(r => r.url.startsWith('Jellywave/')), 'R2b after a sign-out nothing is asked, the retry included');
});

scenario('R2b another song in between: the old song\'s retry does not run', async () => {
  const p = await open();
  await p.play('item-live');
  await p.serve(r => r.url.startsWith('Jellywave/Like'));
  p.$('.jellywave-lyrics').open = true; p.$('.jellywave-lyrics').dispatch('toggle'); await settle();
  await p.fail('Jellywave/Like?stationId=live', 429, RA(20));
  await p.fail('Jellywave/Lyrics?songId=song-live', 429, RA(20));
  p.model.songs = { live: 'song-live-2' };
  await walk(p, 30000);
  check(countOf(p, 'GET Jellywave/Like?stationId=live') === 2, 'R2b like status: once for the old song, once for the new one, no retry');
  check(countOf(p, 'GET Jellywave/Lyrics?songId=song-live&stationId=live') === 1 && countOf(p, 'GET Jellywave/Lyrics?songId=song-live-2&stationId=live') === 1,
    'R2b lyrics: the old song is not asked again, the new one once');
  check(p.shown(p.$('.jellywave-like')) && p.$('.jellywave-lyrics-body').textContent === 'words of live', 'R2b the new song\'s heart and lyrics show');
});

(async () => {
  for (const [name, fn] of scenarios) {
    try { await fn(); } catch (e) { failures++; console.log('FAIL ' + name + ': ' + (e && e.stack ? e.stack.split('\n').slice(0, 3).join(' | ') : e)); }
  }
  console.log(passes + ' passed, ' + failures + ' failed');
  process.exit(failures ? 1 : 0);
})();
