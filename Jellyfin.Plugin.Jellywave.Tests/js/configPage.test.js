// Page tests for Configuration/configPage.html (cards L3, L5c, L8 and L14). Plain node, no dependencies:
//   node Jellyfin.Plugin.Jellywave.Tests/js/configPage.test.js [path/to/configPage.html]
// Runs the page's inline script against a small fake DOM, ApiClient and Dashboard. Probe answers stay pending until a
// scenario resolves them, so answers that arrive late (after Cancel, another Edit, a changed URL) can be tested.
// Exits 1 when any check fails.
'use strict';
const fs = require('fs');
const path = require('path');
const { webcrypto } = require('crypto');

const pagePath = process.argv[2] || path.join(__dirname, '..', '..', 'Jellyfin.Plugin.Jellywave', 'Configuration', 'configPage.html');
const html = fs.readFileSync(pagePath, 'utf8');
const scripts = [...html.matchAll(/<script[^>]*>([\s\S]*?)<\/script>/g)].map(m => m[1]);
if (scripts.length !== 1) { throw new Error('expected one inline script, found ' + scripts.length); }
const ids = [...html.matchAll(/\bid="([^"]+)"/g)].map(m => m[1]);

let failures = 0;
let passes = 0;
function check(condition, name) {
  if (condition) { passes++; console.log('ok   ' + name); } else { failures++; console.log('FAIL ' + name); }
}

class El {
  constructor(tag, id) {
    this.tagName = tag; this.id = id; this.children = []; this.style = {}; this.dataset = {}; this.listeners = {};
    this._text = ''; this.value = ''; this.checked = false; this.disabled = false; this.attrs = {}; this.open = false; this.title = '';
  }
  get textContent() { return this._text + this.children.map(c => c.textContent).join(''); }
  set textContent(v) { this._text = String(v); this.children = []; }
  set innerHTML(v) { throw new Error('innerHTML used'); }
  appendChild(c) { this.children.push(c); c.parent = this; return c; }
  addEventListener(t, f) { (this.listeners[t] = this.listeners[t] || []).push(f); }
  fire(t, e) { (this.listeners[t] || []).forEach(f => f(e || { preventDefault() {}, key: undefined })); }
  click() { if (!this.disabled) { this.fire('click'); } }
  setAttribute(k, v) { this.attrs[k] = String(v); }
  // Constraint validation (fix round S1): a scenario sets el.invalid = true to stand for a value the field refuses.
  checkValidity() { return !this.invalid; }
  reportValidity() { this.reported = (this.reported || 0) + 1; return !this.invalid; }
  focus() { this.env.focused = this; }
  all() { return this.children.flatMap(c => [c, ...c.all()]); }
  querySelector(sel) {
    if (sel.startsWith('#')) { return this.env.byId[sel.slice(1)] || null; }
    let m = sel.match(/^\[data-focus="(.+)"\]$/);
    if (m) { return this.all().find(c => c.dataset.focus === m[1]) || null; }
    m = sel.match(/^option\[value="(.+)"\]$/);
    if (m) { this.options = this.options || {}; return (this.options[m[1]] = this.options[m[1]] || Object.assign(new El('option'), { env: this.env })); }
    throw new Error('unsupported selector ' + sel);
  }
}

const baseConfig = () => ({
  LibraryName: 'Live Radio', NowPlayingEnabled: true, NowPlayingPollSeconds: 20, CoverArtEnabled: true,
  PreviousChannelNames: ['IHateRadio'], CarriedChannelNames: [],
  Stations: [
    { Id: 'b', Name: 'Paradise', Kind: 'Stream', Enabled: true, SortOrder: 1, StreamUrl: 'https://s.example.org/x.aac', Codec: 'aac', Container: 'aac', BitrateKbps: 128, LogoUrl: '' },
    { Id: 'live', Name: 'IHateRadio', Kind: 'SubWave', Enabled: true, SortOrder: 0, BaseUrl: 'http://r.example.org:7700', StreamPath: '/stream.mp3', Codec: 'mp3', Container: 'mp3', BitrateKbps: 192 },
    { Id: 'b', Name: 'Dup', Kind: 'Stream', Enabled: true, SortOrder: 1, StreamUrl: 'https://s.example.org/y.mp3', Codec: 'mp3', Container: 'mp3' }
  ]
});

const baseHealth = () => ({
  PollingEnabled: true, Running: true, LastFetchSeconds: 31.5, ChannelStatus: null, Stations: [
    { Id: 'live', Name: 'IHateRadio', Kind: 'SubWave', Enabled: true, Listed: true, Reachable: false, LastError: 'timed out', LastSuccessUtc: new Date(Date.now() - 120000).toISOString(), Cover: 'logo' },
    { Id: 'b', Name: 'Paradise', Kind: 'Stream', Enabled: true, Listed: true, Logo: 'own' },
    { Id: 'b', Name: 'Dup', Kind: 'Stream', Enabled: true, Listed: false, NotListedReason: 'an earlier station in the list has the same id' }
  ]
});

const subWaveProbe = () => ({
  Ok: true, Kind: 'SubWave', StreamUrl: 'http://n.example.org/radio/stream.mp3', BaseUrl: 'http://n.example.org/radio', StreamPath: '/stream.mp3',
  Codec: 'mp3', Container: 'mp3', BitrateKbps: 256, SampleRate: 48000, Channels: 2, Name: '<b>Night</b>', Tagline: 'tag',
  Lines: [{ Level: 'ok', Text: 'Audio: MP3 · 256 kbps' }, { Level: 'ok', Text: 'SUB/WAVE detected at http://n.example.org/radio' }]
});

const tick = () => new Promise(r => setTimeout(r, 0));
const settle = async () => { for (let i = 0; i < 6; i++) { await tick(); } };

// A fresh page: its own DOM, globals and script run. setup(env) may change the answers before the script runs:
// env.health, env.webUi, env.carry (the CarryAccess status), env.viewers (card L13: the Viewers answer; env.viewerReads counts reads;
// with env.viewerHold each read waits in env.viewerHeld until a scenario resolves it), env.answers (POST answers by url; an object with a status
// rejects like ApiClient does), env.fail (urls whose GET fails with HTTP 500; 'config' fails getPluginConfiguration) and
// env.saveFails (a status: updatePluginConfiguration rejects with it, as ApiClient rejects with the fetch Response).
// env.healthHold (card R2b fix round): each Health read waits in env.healthHeld until a scenario calls it.
// env.loading counts Dashboard's spinner (show adds one, hide clears it, like jellyfin-web's loading indicator).
// Card L14: env.checks counts the Reload status POSTs (Jellywave/Health/Check), which answer with env.health; a url in
// env.fail of 'Jellywave/Health' or 'Jellywave/Health/Check' fails it with HTTP 500.
async function open(config, setup) {
  const env = { byId: {}, focused: null, saved: null, probes: [], posts: [], config: config || baseConfig(), health: baseHealth(),
    webUi: { State: 'NotInstalled' }, carry: { Pending: 0, PreviousNames: [] }, viewers: { Users: 6, Visible: 4, NoAudioTranscoding: [] },
    viewerReads: 0, viewerHold: false, viewerHeld: [], healthHold: false, healthHeld: [], answers: {}, fail: [], saveFails: null, loading: 0, checks: 0,
    windowListeners: {} };
  if (setup) { setup(env); }
  const make = (tag, id) => Object.assign(new El(tag, id), { env });
  ids.forEach(id => { env.byId[id] = make('div', id); });
  const page = env.byId.JellywaveConfigPage;
  // jellyfin-web's custom-elements polyfill calls toLowerCase() on createElement's second argument (host test, v0.5.0.0).
  const polyfilledCreate = (tag, typeExtension) => { if (typeExtension !== undefined) { typeExtension.toLowerCase(); } return make(tag); };
  global.document = { querySelector: sel => (sel === '#JellywaveConfigPage' ? page : null), createElement: polyfilledCreate };
  if (!global.crypto) { global.crypto = webcrypto; }
  // window: only the beforeunload guard uses it (card L11).
  global.window = {
    addEventListener: (t, f) => { (env.windowListeners[t] = env.windowListeners[t] || []).push(f); },
    removeEventListener: (t, f) => { env.windowListeners[t] = (env.windowListeners[t] || []).filter(x => x !== f); }
  };
  // A tab close or reload: every beforeunload listener runs; the browser asks when one called preventDefault.
  env.unload = () => {
    const e = { prevented: false, returnValue: undefined, preventDefault() { this.prevented = true; } };
    (env.windowListeners.beforeunload || []).forEach(f => f(e));
    return e.prevented;
  };
  global.ApiClient = {
    getUrl: p => p,
    getPluginConfiguration: () => (env.fail.includes('config') ? Promise.reject({ status: 500 })
      : Promise.resolve(JSON.parse(JSON.stringify(env.saved || env.config)))),
    getJSON: url => (url === 'Jellywave/Health' && env.healthHold ? new Promise(res => env.healthHeld.push(() => res(env.health)))
      : url === 'Jellywave/Viewers' && env.viewerHold ? new Promise(res => { env.viewerReads++; env.viewerHeld.push(res); })
      : env.fail.includes(url) ? Promise.reject({ status: 500 })
      : Promise.resolve(url === 'Jellywave/Health' ? env.health : url === 'Jellywave/CarryAccess' ? env.carry
        : url === 'Jellywave/Viewers' ? (env.viewerReads++, env.viewers) : env.webUi)),
    ajax: r => {
      env.posts.push(r);
      if (r.url === 'Jellywave/Probe') { return new Promise(res => env.probes.push({ body: JSON.parse(r.data), res })); }
      if (r.url === 'Jellywave/Health/Check') {
        env.checks++;
        return env.fail.includes('Jellywave/Health') || env.fail.includes(r.url) ? Promise.reject({ status: 500 }) : Promise.resolve(env.health);
      }
      if (r.url in env.answers) { const a = env.answers[r.url]; return a && a.status ? Promise.reject(a) : Promise.resolve(a); }
      return Promise.resolve({});
    },
    updatePluginConfiguration: (id, c) => {
      if (env.saveFails) { return Promise.reject({ status: env.saveFails }); }
      env.saved = JSON.parse(JSON.stringify(c));
      return Promise.resolve({});
    }
  };
  // processPluginConfigurationUpdateResult hides the spinner too (12.3: hide, then the "Settings saved" toast).
  global.Dashboard = { showLoadingMsg() { env.loading++; }, hideLoadingMsg() { env.loading = 0; }, processPluginConfigurationUpdateResult() { env.loading = 0; } };
  new Function(scripts[0])();   // eslint-disable-line no-new-func
  page.fire('pageshow');
  await settle();
  const $ = id => env.byId[id];
  const rows = () => $('stationRows').children.map(tr => tr.children.map(td => td.textContent).join(' | '));
  const rowButton = (text, i) => $('stationRows').children[i].all().find(b => b.tagName === 'button' && b.textContent === text);
  const rowBox = i => $('stationRows').children[i].all().find(c => c.tagName === 'input');
  const type = (id, value) => { $(id).value = value; $(id).fire('input'); };
  const answer = async (result, which) => { const p = which === 'last' ? env.probes.pop() : env.probes.shift(); p.res(result); await settle(); };
  const checkWith = async result => { $('btnCheck').click(); await answer(result); };
  const save = async () => { $('JellywaveConfigForm').fire('submit'); await settle(); };
  return { env, $, rows, rowButton, rowBox, type, answer, checkWith, save, unload: env.unload };
}

const scenarios = [];
const scenario = (name, fn) => scenarios.push([name, fn]);

scenario('table', async () => {
  const p = await open();
  const r = p.rows();
  check(r[0].includes('IHateRadio') && r[0].includes('⚠ failing: timed out · last success 2 min ago') && r[0].includes('image: logo'), 'table sorted by SortOrder with the station health');
  check(r[1].includes('✓ in the tile · own logo') && !r[1].includes('⚠'), 'an AAC plain stream: no warning (L10), the logo state');
  check(r[2].includes('left out of the tile: an earlier station'), 'a duplicate id takes its own (second) health entry');
  check(p.$('pollerStatus').textContent.includes('31.5 s, longer than'), 'last fetch time line');
  check(p.$('channelStatus').style.display === 'none' && p.$('channelStatus').textContent === '', 'L13 no tile line while the tile is fine (was "Tile status: ✓ ok.")');
});

scenario('reorder and remove', async () => {
  const p = await open();
  p.rowButton('↓', 0).click();
  check(p.rows()[1].includes('IHateRadio') && p.env.focused.dataset.focus === 'down1', 'move down keeps the focus on the moved row');
  p.rowButton('↑', 1).click();
  check(p.rows()[0].includes('IHateRadio') && p.env.focused.dataset.focus === 'down0', 'move up to the top focuses its down arrow');
  check(p.rowButton('↑', 0).disabled && p.rowButton('↓', 2).disabled, 'ends of the list disable their arrow');
  p.rowButton('✕', 2).click();
  check(p.rows()[2].includes('Remove Dup?'), 'remove asks in the row');
  p.rowButton('Keep', 2).click();
  check(p.rows().length === 3, 'Keep keeps the station');
  p.rowButton('✕', 2).click(); p.rowButton('Remove', 2).click();
  check(p.rows().length === 2, 'confirmed Remove removes it');
});

scenario('add, edit, save', async () => {
  const p = await open();
  p.$('btnAdd').click();
  check(p.$('stationEditor').style.display === '', 'Add opens the dialog');
  p.type('edUrl', 'http://n.example.org/radio/stream.mp3');
  await p.checkWith(subWaveProbe());
  check(p.env.probes.length === 0 && p.env.posts[0].contentType === 'application/json' && p.env.posts[0].type === 'POST', 'Check posts JSON to the probe');
  check(p.$('edName').value === '<b>Night</b>', 'name prefilled through value (no markup parsing)');
  check(p.$('edLines').textContent.includes('✓ SUB/WAVE detected'), 'probe lines rendered as text');
  check(p.$('edKind').textContent.includes('SUB/WAVE station (detected)'), 'kind shown from the probe');
  check(p.$('edTaglineBox').style.display === '' && p.$('edLogoBox').style.display === 'none', 'SUB/WAVE: description shown, logo hidden');
  p.$('btnEdOk').click();
  check(p.rows().length === 4 && p.rows()[3].includes('not saved yet'), 'added row says not saved yet');

  p.rowButton('Edit', 1).click();
  check(p.$('edUrl').value === 'https://s.example.org/x.aac' && p.$('edKind').textContent === 'Type: stream.', 'Edit opens with the stored values and kind');
  p.type('edGenre', 'Jazz'); p.$('btnEdOk').click();

  p.$('btnAdd').click(); p.type('edUrl', 'https://x.example.org/live.mp3');
  p.$('btnEdOk').click();
  check(p.$('stationEditor').style.display === '', 'an unchecked URL: the first press only warns');
  p.$('btnEdOk').click();
  check(p.rows().length === 5, 'an unchecked URL: the second press adds a plain stream');

  p.$('btnAdd').click(); p.type('edUrl', 'http://old.example.org/a.mp3');
  await p.checkWith({ Ok: false, Redirect: 'https://new.example.org/a.mp3', Lines: [{ Level: 'redirect', Text: 'The address redirected to https://new.example.org/a.mp3; use that address' }] });
  p.$('edLines').all().find(b => b.tagName === 'button').click();
  check(p.$('edUrl').value === 'https://new.example.org/a.mp3', 'Use it puts the redirect target in the field');
  p.$('btnEdCancel').click();
  check(p.$('stationEditor').style.display === 'none', 'Cancel closes the dialog');

  p.$('btnAdd').click(); p.type('edUrl', 'https://h.example.org/x.m3u8');
  await p.checkWith({ Ok: true, Kind: 'Stream', StreamUrl: 'https://h.example.org/x.m3u8', Codec: 'aac', Container: 'hls', Lines: [] });
  check(!p.$('edHints').textContent.includes('HLS') && !p.$('edDelivery'), 'HLS: no delivery choice and no HLS hint (L10)');
  p.$('stationEditor').fire('keydown', { key: 'Escape', preventDefault() {} });
  check(p.$('stationEditor').style.display === 'none', 'Escape cancels the dialog');

  await p.save();
  const s = p.env.saved;
  check(s && s.Stations.length === 5, 'Save posts every station');
  check(s.Stations.map(x => x.SortOrder).join() === '0,1,2,3,4', 'Save rewrites SortOrder in table order');
  check(s.Stations.every(x => !('_unsaved' in x)), 'Save sends no page-only keys');
  check(/^[0-9a-f]{32}$/.test(s.Stations[3].Id) && s.Stations[3].Kind === 'SubWave' && s.Stations[3].BaseUrl === 'http://n.example.org/radio', 'new SUB/WAVE station: N-format id and base URL');
  check(s.Stations[1].Genre === 'Jazz' && s.Stations[1].Id === 'b', 'edit applied, id kept');
  check(s.Stations[3].Id !== s.Stations[4].Id && /^[0-9a-f]{12}4[0-9a-f]{3}[89ab][0-9a-f]{15}$/.test(s.Stations[4].Id), 'new ids differ and are version-4 GUIDs');
  check(s.Stations[4].Kind === 'Stream' && s.Stations[4].StreamUrl === 'https://x.example.org/live.mp3', 'unchecked URL saved as a plain stream');
  check(s.PreviousChannelNames.join() === 'IHateRadio', 'bridge list passed through unchanged');
});

scenario('R-A late answer after Cancel and another Edit', async () => {
  const p = await open();
  p.$('btnAdd').click(); p.type('edUrl', 'https://h.example.org/x.m3u8');
  p.$('btnCheck').click();
  p.$('btnEdCancel').click();
  p.rowButton('Edit', 1).click();   // Paradise: AAC
  check(!p.$('btnCheck').disabled, 'R-A a new dialog does not wait for the old check');
  const before = [p.$('edContainer').value, p.$('edBitrate').value, p.$('edName').value, p.$('edGenre').value].join('/');
  await p.answer({ Ok: true, Kind: 'Stream', StreamUrl: 'https://h.example.org/x.m3u8', Codec: 'aac', Container: 'hls', BitrateKbps: 64, Name: 'HLS One', Lines: [{ Level: 'ok', Text: 'Audio: HLS' }] });
  const after = [p.$('edContainer').value, p.$('edBitrate').value, p.$('edName').value, p.$('edGenre').value].join('/');
  check(before === after && !p.$('edLines').textContent.includes('Audio: HLS'), 'R-A the late answer leaves the other dialog alone (' + before + ' -> ' + after + ')');
  p.$('btnEdOk').click();
  await p.save();
  const para = p.env.saved.Stations.find(x => x.Name === 'Paradise');
  check(para.Container === 'aac' && para.BitrateKbps === 128 && para.StreamUrl === 'https://s.example.org/x.aac', 'R-A the other station is saved as it was');
});

scenario('R-A2 late answer for the same URL typed into a new dialog', async () => {
  const p = await open();
  p.$('btnAdd').click(); p.type('edUrl', 'http://n.example.org/radio/stream.mp3'); p.$('btnCheck').click();
  p.$('btnEdCancel').click();
  p.$('btnAdd').click(); p.type('edUrl', 'http://n.example.org/radio/stream.mp3');
  await p.answer(subWaveProbe());
  check(p.$('edName').value === '' && !p.$('edLines').textContent.includes('SUB/WAVE detected') && p.$('edKind').textContent.includes('not checked'),
    'R-A2 an answer from a closed dialog is dropped even when the new dialog has the same URL');
});

scenario('R-A3 Edit replaces an open dialog while its check runs', async () => {
  const p = await open();
  p.$('btnAdd').click(); p.type('edUrl', 'https://h.example.org/x.m3u8'); p.$('btnCheck').click();
  p.rowButton('Edit', 0).click();   // no Cancel in between
  check(!p.$('btnCheck').disabled, 'R-A3 the new dialog\'s Check works at once');
  await p.answer({ Ok: true, Kind: 'Stream', StreamUrl: 'https://h.example.org/x.m3u8', Codec: 'aac', Container: 'hls', Name: 'HLS', Lines: [] });
  check(p.$('edContainer').value === 'mp3' && p.$('edName').value === 'IHateRadio', 'R-A3 the replaced dialog\'s answer is dropped');
});

scenario('R-B remove the station being edited', async () => {
  const p = await open();
  p.rowButton('Edit', 2).click();
  p.rowButton('✕', 2).click(); p.rowButton('Remove', 2).click();
  check(p.$('stationEditor').style.display === 'none' && p.$('tableNote').textContent.includes('dialog closed'), 'R-B its dialog closes with a note');
  p.$('btnEdOk').click();
  check(p.rows().length === 2, 'R-B it does not come back');
});

scenario('R-C Enabled changed while editing', async () => {
  const p = await open();
  p.rowButton('Edit', 0).click();
  const box = p.rowBox(0); box.checked = false; box.fire('change');
  p.type('edName', 'IHateRadio 2');
  p.$('btnEdOk').click();
  check(p.rows()[0].includes('IHateRadio 2 (off)'), 'R-C the row\'s Enabled untick survives Apply');
});

scenario('R-D Save while the dialog is open', async () => {
  const p = await open();
  p.$('btnAdd').click();
  await p.save();
  check(p.env.saved === null && p.$('saveNote').textContent.indexOf('Finish the station dialog') === 0, 'R-D Save is refused while the dialog is open');
});

scenario('R-E a second check while one runs, and a changed URL', async () => {
  const p = await open();
  p.$('btnAdd').click(); p.type('edUrl', 'https://a.example.org/a.mp3'); p.$('btnCheck').click();
  p.type('edUrl', 'https://b.example.org/b.mp3'); p.$('edUrl').fire('keydown', { key: 'Enter', preventDefault() {} });
  check(p.env.probes.length === 1, 'R-E Enter starts no second check while one runs');
  await p.answer({ Ok: true, Kind: 'Stream', StreamUrl: 'https://a.example.org/a.mp3', Codec: 'aac', Container: 'aac', BitrateKbps: 32, Name: 'A', Lines: [{ Level: 'ok', Text: 'A ok' }] });
  check(p.$('edName').value === '' && p.$('edCodec').value === 'mp3', 'R-E an answer for URL A is not applied while the field says B');
  check(p.$('edLines').textContent.includes('URL changed while it was being checked'), 'R-E the dialog says to check again');
  check(!p.$('btnCheck').disabled, 'R-E Check is usable again');
  p.$('edUrl').fire('keydown', { key: 'Enter', preventDefault() {} });
  await p.answer({ Ok: true, Kind: 'Stream', StreamUrl: 'https://b.example.org/b.mp3', Codec: 'mp3', Container: 'mp3', BitrateKbps: 128, Name: 'B', Lines: [] });
  check(p.$('edName').value === 'B', 'R-E Enter checks the URL in the field');
});

scenario('R-F reorder while editing', async () => {
  const p = await open();
  p.rowButton('Edit', 1).click(); p.rowButton('↑', 1).click();
  p.type('edName', 'Paradise 2'); p.$('btnEdOk').click();
  check(p.rows()[0].includes('Paradise 2') && p.rows()[1].includes('IHateRadio'), 'R-F Apply lands on the moved station');
});

scenario('R-G URL changed after a check', async () => {
  const p = await open();
  p.$('btnAdd').click(); p.type('edUrl', 'http://n.example.org/radio/stream.mp3');
  await p.checkWith(subWaveProbe());
  p.type('edUrl', 'https://other.example.org/x.mp3');
  const n = p.rows().length;
  p.$('btnEdOk').click();
  check(p.rows().length === n && p.$('stationEditor').style.display === '', 'R-G a URL changed after its check needs a second press');
});

scenario('S3 re-checking a SUB/WAVE station', async () => {
  const p = await open();
  p.rowButton('Edit', 0).click();   // IHateRadio, SUB/WAVE
  p.type('edUrl', 'http://r.example.org:7700/deep/stream.mp3');
  p.$('btnCheck').click();
  check(p.env.probes[0].body.BaseUrl === 'http://r.example.org:7700' && p.env.probes[0].body.Url === 'http://r.example.org:7700/deep/stream.mp3', 'S3 the stored base goes with the check');
  await p.answer({ Ok: true, Kind: 'Stream', StreamUrl: 'http://r.example.org:7700/deep/stream.mp3', Codec: 'mp3', Container: 'mp3', Lines: [] });
  p.$('btnEdOk').click();
  check(p.$('stationEditor').style.display === '' && p.$('edLines').textContent.includes('found a plain stream'), 'S3 a downgrade to a plain stream asks first');
  p.$('btnEdOk').click();
  await p.save();
  const s = p.env.saved.Stations[0];
  check(s.Id === 'live' && s.Kind === 'Stream' && s.StreamUrl === 'http://r.example.org:7700/deep/stream.mp3', 'S3 the second press saves the plain stream');

  const q = await open();
  q.$('btnAdd').click(); q.type('edUrl', 'https://x.example.org/a.mp3'); q.$('btnCheck').click();
  check(!('BaseUrl' in q.env.probes[0].body), 'S3 a new station sends no base');
});

scenario('L7 a SUB/WAVE station whose URL changed without a check', async () => {
  const p = await open();
  p.rowButton('Edit', 0).click();   // IHateRadio, SUB/WAVE
  p.type('edUrl', 'https://plain.example.org/live.mp3');
  p.$('btnEdOk').click();
  const lines = p.$('edLines').textContent;
  check(p.$('stationEditor').style.display === '' && lines.includes('This is a SUB/WAVE station, and its new URL has not been checked')
    && lines.includes('now playing, covers, likes and requests stop for it'), 'L7 the first press gives the SUB/WAVE warning');
  check(!lines.includes('This URL has not been checked successfully'), 'L7 not the generic plain-stream warning');
  p.$('btnEdOk').click();
  check(p.$('stationEditor').style.display === 'none', 'L7 one confirming press applies it');
  await p.save();
  const s = p.env.saved.Stations[0];
  check(s.Id === 'live' && s.Kind === 'Stream' && s.StreamUrl === 'https://plain.example.org/live.mp3' && s.BaseUrl === '', 'L7 saved as a plain stream after the confirming press');

  const q = await open();
  q.rowButton('Edit', 0).click();
  q.type('edUrl', 'https://plain.example.org/live.mp3');
  q.$('btnEdOk').click();
  q.type('edUrl', 'https://plain.example.org/other.mp3');   // a new URL takes back the confirmation
  q.$('btnEdOk').click();
  const again = q.$('edLines').children;
  check(q.$('stationEditor').style.display === '' && again[again.length - 1].textContent.includes('This is a SUB/WAVE station, and its new URL')
    && !q.$('edLines').textContent.includes('This URL has not been checked successfully'), 'L7 a URL typed after the warning asks again, as a SUB/WAVE station');
  q.$('btnEdCancel').click();
  await q.save();
  check(q.env.saved.Stations[0].Kind === 'SubWave' && q.env.saved.Stations[0].BaseUrl === 'http://r.example.org:7700', 'L7 Cancel keeps it SUB/WAVE');

  const r = await open();
  r.$('btnAdd').click(); r.type('edUrl', 'https://new.example.org/a.mp3');
  r.$('btnEdOk').click();
  check(r.$('edLines').textContent.includes('This URL has not been checked successfully') && !r.$('edLines').textContent.includes('SUB/WAVE station'),
    'L7 a new station gets the generic warning, not the SUB/WAVE one');
  r.$('btnEdCancel').click();
  r.$('btnAdd').click(); r.type('edUrl', 'https://new.example.org/b.mp3');
  await r.checkWith({ Ok: true, Kind: 'Stream', StreamUrl: 'https://new.example.org/b.mp3', Codec: 'mp3', Container: 'mp3', Lines: [] });
  r.$('btnEdOk').click();
  check(r.$('stationEditor').style.display === 'none' && r.rows().length === 4, 'L7 a new checked plain stream is added with one press');
});

scenario('K4 a confirmed downgrade, then Check again, then one press', async () => {
  const p = await open();
  const plain = () => ({ Ok: true, Kind: 'Stream', StreamUrl: 'https://plain.example.org/live.mp3', Codec: 'mp3', Container: 'mp3', Lines: [] });
  p.rowButton('Edit', 0).click();   // IHateRadio, SUB/WAVE
  p.type('edUrl', 'https://plain.example.org/live.mp3');
  await p.checkWith(plain());
  p.$('btnEdOk').click();
  check(p.$('edLines').textContent.includes('found a plain stream'), 'K4 the first press warns');
  await p.checkWith(plain());   // the admin checks again instead of confirming
  p.$('btnEdOk').click();
  check(p.$('stationEditor').style.display === '' && p.$('edLines').textContent.includes('found a plain stream'), 'K4 after a new check one press asks again');
  p.$('btnEdOk').click();
  check(p.$('stationEditor').style.display === 'none', 'K4 the confirming press after it applies');
  await p.save();
  check(p.env.saved.Stations[0].Kind === 'Stream', 'K4 saved as a plain stream');

  const q = await open();
  q.rowButton('Edit', 0).click();
  q.type('edUrl', 'http://r.example.org:7700/other.mp3');
  await q.checkWith(plain());
  q.$('btnEdOk').click();
  await q.checkWith({ Ok: true, Kind: 'SubWave', StreamUrl: 'http://r.example.org:7700/other.mp3', BaseUrl: 'http://r.example.org:7700', StreamPath: '/other.mp3', Codec: 'mp3', Container: 'mp3', Lines: [] });
  q.$('btnEdOk').click();
  check(q.$('stationEditor').style.display === 'none', 'K4 a re-check that finds SUB/WAVE applies with one press');
  await q.save();
  check(q.env.saved.Stations[0].Kind === 'SubWave' && q.env.saved.Stations[0].StreamPath === '/other.mp3', 'K4 kept SUB/WAVE with the new path');
});

// ---- Card L5c: help texts and status colours ----

// The head of a status line: its class list and text (symbol + one or two words).
const headOf = node => node.children.find(c => (c.className || '').split(' ').includes('jwHead'));
const levelClass = { ok: 'jwHead jwOk', warn: 'jwHead jwWarn', bad: 'jwHead jwBad', off: 'jwHead jwMuted', plain: 'jwHead', moved: 'jwHead jwWarn' };
const levelMark = { ok: '✓ ', warn: '⚠ ', bad: '✕ ', off: '', plain: '', moved: '↪ ' };
function checkHead(node, level, words, name) {
  const h = node && headOf(node);
  check(!!h && h.className === levelClass[level] && h.textContent === levelMark[level] + words,
    name + ' (' + (h ? h.className + ' "' + h.textContent + '"' : 'no head') + ')');
}
const statusCell = (p, i) => p.$('stationRows').children[i].children[4];   // L13 fix round: no Format column

scenario('L5c help texts', async () => {
  const flat = html.replace(/\s+/g, ' ');
  const has = (text, name) => check(flat.includes(text), 'L5c text: ' + name);
  has('<div class="fieldDescription">Users see all the stations or none; set it per user under Dashboard → Users → Access, where Jellyfin calls the tile a channel. The button adds the tile for everyone who doesn\'t have it, including anyone you switched off on purpose.</div>', 'Who can see it (L11: names "channel" once)');
  has('<div class="fieldDescription">Optional. Install <a is="emby-linkbutton" class="button-link" href="https://github.com/IAmParadox27/jellyfin-plugin-file-transformation" target="_blank" rel="noopener noreferrer">File Transformation</a> to get a live panel on the tile\'s page. Everything else works without it.</div>', 'Station page');
  has('<div class="fieldDescription">Shown in My Media. Renaming makes a new tile; see README first.</div>', 'Tile name (L13 fix round: the sketch\'s text)');
  has('SUB/WAVE stations: while someone plays one or has its station page open, the server keeps its item\'s description on the current track, DJ, show and what\'s next (most apps show it when the list is reopened), and the station page shows the live track; otherwise the server asks the station nothing and its item shows its own picture and text. Plain streams: while someone plays one, the server reads the title it sends every 15 seconds for the station page, each time a short extra listener on the station.', 'Show what\'s playing (L6: SUB/WAVE now playing and plain-stream titles, the extra listener)');
  has('Now playing</h3>', 'L6 the heading covers both kinds (L13: a heading inside Advanced)');
  has('<span>Show what\'s playing</span>', 'L6 the checkbox label');
  has('Uses each track\'s cover as the station\'s picture. Without it, or when a track has no art, the Jellywave logo shows. The My Media tile always shows the logo.', 'Track cover art');
  // Card U1: the repository URL is back, but only in the grey "Not installed" line (the help text stays short).
  check(flat.split('iamparadox.dev').length === 2 && flat.includes(': add the repository https://www.iamparadox.dev/jellyfin/plugins/manifest.json (Dashboard')
    && !flat.includes('2026-10-27') && !flat.includes('not in its catalog') && !/block list names/.test(flat),
    'L5c the dated catalog note and the block-list skip left the page; U1 the repository URL only in the "Not installed" line');
});

scenario('L5c status colours keep 3:1 on both backgrounds', async () => {
  const lin = c => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
  const lum = hex => { const n = parseInt(hex.slice(1), 16); return 0.2126 * lin(n >> 16) + 0.7152 * lin((n >> 8) & 255) + 0.0722 * lin(n & 255); };
  const ratio = (a, b) => { const x = lum(a), y = lum(b); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); };
  ['jwOk', 'jwWarn', 'jwBad', 'jwMuted'].forEach(cls => {
    const m = html.match(new RegExp('#JellywaveConfigPage \\.' + cls + ' \\{ color: (#[0-9a-f]{6}); \\}'));
    const dark = m ? ratio(m[1], '#101010') : 0;
    const light = m ? ratio(m[1], '#ffffff') : 0;
    check(m && dark >= 3 && light >= 3, 'L5c ' + cls + ' ' + (m ? m[1] : '?') + ': ' + dark.toFixed(2) + ':1 on #101010, ' + light.toFixed(2) + ':1 on #ffffff');
  });
});

scenario('L5c channel status line (L13: a banner only when it applies)', async () => {
  let p = await open();
  check(p.$('channelStatus').style.display === 'none' && !headOf(p.$('channelStatus')), 'L13 channel ok: no banner, no green line');
  p = await open(null, env => { env.health.ChannelStatus = 'another plugin\'s tile has the same name'; });
  checkHead(p.$('channelStatus'), 'bad', 'Another plugin\'s tile is also called "Live Radio".', 'L5c channel name taken: red (L13 fix round: the sketch\'s text)');
  check(p.$('channelStatus').style.display === '' && p.$('channelStatus').className === 'jwBanner jwBannerBad', 'L13 the collision is a red banner');
  check(p.$('channelStatus').textContent === '✕ Another plugin\'s tile is also called "Live Radio". Jellywave does nothing until you rename it (Advanced → Tile name).', 'L5c channel collision names the tile and the advice: ' + p.$('channelStatus').textContent);
  p.$('LibraryName').value = 'Typed but not saved';
  p.$('btnHealth').click(); await settle();
  check(headOf(p.$('channelStatus')).textContent === '✕ Another plugin\'s tile is also called "Live Radio".', 'L13 fix the collision names the saved tile, not the field being typed in');
  p = await open(null, env => { env.fail.push('Jellywave/Health'); });
  checkHead(p.$('channelStatus'), 'warn', 'Couldn\'t check the tile', 'L5c channel fetch failure: yellow');
  check(p.$('channelStatus').textContent === '⚠ Couldn\'t check the tile. Request failed: HTTP 500.' && p.$('channelStatus').className === 'jwBanner jwBannerWarn', 'L5c channel fetch failure tail, a yellow banner');
});

scenario('L5c poller line and its thresholds', async () => {
  // Poll interval 20 s (baseConfig): green up to 10 s, yellow over 10 s up to 20 s, red over 20 s.
  const at = async (setup, level, words, name) => { const p = await open(null, setup); checkHead(p.$('pollerStatus'), level, words, 'L5c poller ' + name); return p; };
  let p = await at(env => { env.health.PollingEnabled = false; }, 'plain', 'now playing is off', 'now playing off: plain, no symbol');
  check(p.$('pollerStatus').textContent === 'Now playing updates: now playing is off.', 'L5c poller off text');
  p = await at(env => { env.health.Running = false; }, 'warn', 'not running', 'not running: yellow');
  check(p.$('pollerStatus').textContent === 'Now playing updates: ⚠ not running (starts after server startup).', 'L5c poller not running text');
  p = await at(env => { env.health.LastFetchSeconds = null; }, 'ok', 'running', 'running, no poll finished yet: green');
  check(p.$('pollerStatus').textContent === 'Now playing updates: ✓ running.', 'L5c poller running text');
  p = await at(env => { env.health.LastFetchSeconds = 3.2; }, 'ok', 'running', '3.2 s: green');
  check(p.$('pollerStatus').textContent === 'Now playing updates: ✓ running. The last update asked the SUB/WAVE stations someone listens to in 3.2 s.', 'L5c poller green text');
  await at(env => { env.health.LastFetchSeconds = 10; }, 'ok', 'running', 'exactly half the interval (10 s): green');
  p = await at(env => { env.health.LastFetchSeconds = 10.1; }, 'warn', 'running slow', 'just over half (10.1 s): yellow');
  check(p.$('pollerStatus').textContent === 'Now playing updates: ⚠ running slow. The last update asked the SUB/WAVE stations someone listens to in 10.1 s, more than half the update interval (20 s).', 'L5c poller yellow text');
  await at(env => { env.health.LastFetchSeconds = 20; }, 'warn', 'running slow', 'exactly the interval (20 s): yellow');
  p = await at(env => { env.health.LastFetchSeconds = 20.1; }, 'bad', 'too slow', 'just over the interval (20.1 s): red');
  check(p.$('pollerStatus').textContent === 'Now playing updates: ✕ too slow. The last update asked the SUB/WAVE stations someone listens to in 20.1 s, longer than the update interval: the stations update less often than set.', 'L5c poller red text keeps the slow-poll explanation');
  const c = baseConfig(); c.NowPlayingPollSeconds = 5;
  p = await open(c, env => { env.health.LastFetchSeconds = 2.6; });
  checkHead(p.$('pollerStatus'), 'warn', 'running slow', 'L5c poller threshold follows the interval (2.6 s of 5 s: yellow)');
  p = await at(env => { env.fail.push('Jellywave/Health'); }, 'warn', 'couldn\'t check', 'fetch failure: yellow');
  check(p.$('pollerStatus').textContent === 'Now playing updates: ⚠ couldn\'t check. Request failed: HTTP 500.', 'L5c poller fetch failure text');
});

scenario('L5c station page line', async () => {
  const at = async (setup, level, words, text, name) => {
    const p = await open(null, setup);
    checkHead(p.$('webUiStatus'), level, words, 'L5c station page ' + name);
    check(p.$('webUiStatus').textContent === text, 'L5c station page ' + name + ' text: ' + p.$('webUiStatus').textContent);
  };
  await at(env => { env.webUi = { State: 'Active', FileTransformationVersion: '3.0.0.0' }; }, 'ok', 'Active',
    '✓ Active with File Transformation 3.0.0.0. Users may need to reload the web client once.', 'Active: green, version and reload note in the tail');
  await at(env => { env.webUi = { State: 'NotInstalled' }; }, 'off', 'Not installed', 'Not installed: add the repository https://www.iamparadox.dev/jellyfin/plugins/manifest.json (Dashboard → Plugins → Repositories), install File Transformation, then restart.', 'L11 NotInstalled: grey, no mark; U1: the repository, install and restart steps');
  await at(env => { env.webUi = { State: 'Failed', FileTransformationVersion: '3.0.0.0' }; }, 'bad', 'Not active', '✕ Not active: registration failed; see the server log.', 'Failed: red');
  await at(env => { env.webUi = { State: 'Unknown', Message: 'Not checked yet.' }; }, 'warn', 'Not checked', '⚠ Not checked yet (the check runs at server startup).', 'Unknown: yellow');
  await at(env => { env.webUi = { State: 'Later', Message: 'Some <i>new</i> state' }; }, 'warn', 'Unknown state', '⚠ Unknown state: Some <i>new</i> state', 'a state this page does not know: yellow, message as text');
  await at(env => { env.fail.push('Jellywave/WebUi'); }, 'warn', 'Couldn\'t check', '⚠ Couldn\'t check. Request failed: HTTP 500.', 'fetch failure: yellow');
});

scenario('L5c station table status column', async () => {
  const sub = (id, extra) => Object.assign({ Id: id, Name: id, Kind: 'SubWave', Enabled: true, BaseUrl: 'http://' + id + '.example.org', StreamPath: '/stream.mp3', Codec: 'mp3', Container: 'mp3' }, extra);
  const config = baseConfig();
  config.Stations = [
    sub('ok'), sub('failing'), sub('waiting'), sub('bare'),
    { Id: 'plain', Name: 'plain', Kind: 'Stream', Enabled: true, StreamUrl: 'https://p.example.org/a.mp3', Codec: 'mp3', Container: 'mp3' },
    { Id: 'refused', Name: 'refused', Kind: 'Stream', Enabled: true, StreamUrl: 'https://q.example.org/a.mp3', Codec: 'mp3', Container: 'mp3' },
    sub('off', { Enabled: false })
  ].map((s, i) => Object.assign(s, { SortOrder: i }));
  const recent = new Date(Date.now() - 12000).toISOString();
  const health = env => {
    env.health.Stations = [
      { Id: 'ok', Kind: 'SubWave', Enabled: true, Listed: true, Reachable: true, LastSuccessUtc: recent, Cover: 'track' },
      { Id: 'failing', Kind: 'SubWave', Enabled: true, Listed: true, Reachable: false, LastError: '<b>timed out</b>', LastSuccessUtc: recent, Cover: 'logo' },
      { Id: 'waiting', Kind: 'SubWave', Enabled: true, Listed: true, Reachable: null },
      { Id: 'bare', Kind: 'SubWave', Enabled: true, Listed: true, Reachable: false, LastSuccessUtc: null },
      { Id: 'plain', Kind: 'Stream', Enabled: true, Listed: true, Logo: 'failed' },
      { Id: 'refused', Kind: 'Stream', Enabled: true, Listed: false, NotListedReason: 'an <script>x</script> reason' },
      { Id: 'off', Kind: 'SubWave', Enabled: false, Listed: false }
    ];
  };
  let p = await open(config, health);
  const expect = [
    ['ok', 'on air', '✓ on air · 12 s ago · image: track cover', 'SUB/WAVE reached: green (L13 fix round: "on air")'],
    ['warn', 'failing', '⚠ failing: <b>timed out</b> · last success 12 s ago · image: logo', 'SUB/WAVE failing: yellow, error as text'],
    ['off', 'in the tile', 'in the tile · waiting for the first update', 'L11 waiting for the first update: grey'],
    ['warn', 'failing', '⚠ failing · last success never', 'failing without a category: yellow'],
    ['warn', 'in the tile', '⚠ in the tile · ⚠ logo failed; retried within 30 min', 'plain stream in the tile with a failed logo: yellow'],
    ['bad', 'left out of the tile', '✕ left out of the tile: an <script>x</script> reason', 'left out of the tile: red, reason as text'],
    ['off', 'disabled', 'disabled', 'disabled: grey']
  ];
  expect.forEach(([level, words, text, name], i) => {
    checkHead(statusCell(p, i), level, words, 'L5c table ' + name);
    check(statusCell(p, i).textContent === text, 'L5c table ' + name + ' text: ' + statusCell(p, i).textContent);
  });
  check(!headOf(statusCell(p, 0)).textContent.includes('image') && !headOf(statusCell(p, 1)).textContent.includes('image'), 'L5c table the image suffix stays in the plain tail');

  p = await open(config, env => { health(env); env.health.Running = false; });
  checkHead(statusCell(p, 0), 'warn', 'in the tile', 'L5c table poller not running: yellow');
  check(statusCell(p, 0).textContent === '⚠ in the tile · now playing updates not running yet · image: track cover', 'L5c table poller not running text');
  checkHead(statusCell(p, 4), 'warn', 'in the tile', 'L5c table plain stream unaffected by the poller');

  p = await open(config, env => { health(env); env.health.PollingEnabled = false; });
  checkHead(statusCell(p, 0), 'off', 'in the tile', 'L5c table now playing off: grey');
  check(statusCell(p, 0).textContent === 'in the tile · now playing is off · image: track cover', 'L5c table now playing off text');

  p.$('btnAdd').click(); p.type('edUrl', 'https://x.example.org/live.mp3'); p.$('btnEdOk').click(); p.$('btnEdOk').click();
  checkHead(statusCell(p, 7), 'off', 'not saved yet', 'L5c table not saved yet: grey');
});

scenario('L5c fix round: a green head turns yellow when the tail warns', async () => {
  const config = baseConfig();
  config.Stations = [
    { Id: 'own', Name: 'own', Kind: 'Stream', Enabled: true, SortOrder: 0, StreamUrl: 'https://p.example.org/a.mp3', Codec: 'mp3', Container: 'mp3' },
    { Id: 'aac', Name: 'aac', Kind: 'Stream', Enabled: true, SortOrder: 1, StreamUrl: 'https://p.example.org/a.aac', Codec: 'aac', Container: 'aac' },
    { Id: 'sw', Name: 'sw', Kind: 'SubWave', Enabled: true, SortOrder: 2, BaseUrl: 'http://s.example.org', StreamPath: '/a.aac', Codec: 'aac', Container: 'aac' },
    { Id: 'sw2', Name: 'sw2', Kind: 'SubWave', Enabled: true, SortOrder: 3, BaseUrl: 'http://t.example.org', StreamPath: '/a.mp3', Codec: 'mp3', Container: 'mp3' },
    { Id: 'bad', Name: 'bad', Kind: 'Stream', Enabled: true, SortOrder: 4, StreamUrl: 'https://p.example.org/b.mp3', Codec: 'mp3', Container: 'mp3', LogoUrl: 'https://p.example.org/b.png' }
  ];
  const recent = new Date(Date.now() - 12000).toISOString();
  const p = await open(config, env => {
    env.health.Stations = [
      { Id: 'own', Kind: 'Stream', Enabled: true, Listed: true, Logo: 'own' },
      { Id: 'aac', Kind: 'Stream', Enabled: true, Listed: true, Logo: 'embedded' },
      { Id: 'sw', Kind: 'SubWave', Enabled: true, Listed: true, Reachable: true, LastSuccessUtc: recent, Cover: 'logo' },
      { Id: 'sw2', Kind: 'SubWave', Enabled: true, Listed: true, Reachable: true, LastSuccessUtc: recent, Cover: 'logo' },
      { Id: 'bad', Kind: 'Stream', Enabled: true, Listed: true, Logo: 'failed' }
    ];
  });
  checkHead(statusCell(p, 0), 'ok', 'in the tile', 'L5c fix plain stream with its own logo: green');
  // L10: AAC is relayed like everything else, so an AAC station's tail carries no warning and its head stays green.
  checkHead(statusCell(p, 1), 'ok', 'in the tile', 'L10 plain stream with AAC: green');
  check(statusCell(p, 1).textContent === '✓ in the tile · Jellywave logo (no logo URL)', 'L10 plain stream with AAC text');
  checkHead(statusCell(p, 2), 'ok', 'on air', 'L10 SUB/WAVE ok with AAC: green');
  check(statusCell(p, 2).textContent === '✓ on air · 12 s ago · image: logo', 'L10 SUB/WAVE with AAC text');
  checkHead(statusCell(p, 3), 'ok', 'on air', 'L5c fix SUB/WAVE ok with only the image suffix: green');
  checkHead(statusCell(p, 4), 'warn', 'in the tile', 'L5c fix plain stream with a failed logo: yellow');
  check(statusCell(p, 4).textContent === '⚠ in the tile · ⚠ logo failed; retried within 30 min', 'L5c fix failed logo text');
});

scenario('L5c fix round: Health failing', async () => {
  const p = await open(null, env => { env.fail.push('Jellywave/Health'); });
  [0, 1, 2].forEach(i => {
    checkHead(statusCell(p, i), 'warn', 'couldn\'t check', 'L5c fix Health failing on first load: row ' + i + ' yellow');
    check(statusCell(p, i).textContent === '⚠ couldn\'t check', 'L5c fix Health failing on first load: row ' + i + ' text');
  });
  p.$('btnAdd').click(); p.type('edUrl', 'https://x.example.org/live.mp3'); p.$('btnEdOk').click(); p.$('btnEdOk').click();
  checkHead(statusCell(p, 3), 'off', 'not saved yet', 'L5c fix an unsaved station still says not saved yet');

  const q = await open();
  checkHead(statusCell(q, 1), 'ok', 'in the tile', 'L5c fix before the refresh: the server\'s view');
  q.env.fail.push('Jellywave/Health');
  q.$('btnHealth').click(); await settle();
  checkHead(statusCell(q, 1), 'warn', 'couldn\'t check', 'L5c fix a failed refresh drops the stale view');
  q.env.fail.length = 0;
  q.$('btnHealth').click(); await settle();
  check(statusCell(q, 1).textContent === '✓ in the tile · own logo' && q.$('pollerStatus').textContent.startsWith('Now playing updates: ✕ too slow'),
    'L5c fix a later good refresh shows the server\'s view again');
});

scenario('L5c fix round: the poller uses the interval the server uses', async () => {
  const at = async (field, took, level, words, name) => {
    const c = baseConfig(); c.NowPlayingPollSeconds = field;
    const p = await open(c, env => { env.health.LastFetchSeconds = took; });
    checkHead(p.$('pollerStatus'), level, words, 'L5c fix poller ' + name);
    return p;
  };
  let p = await at('2', 3, 'warn', 'running slow', 'field "2" is 5 s: 3 s is yellow');
  check(p.$('pollerStatus').textContent.endsWith('more than half the update interval (5 s).'), 'L5c fix poller names the clamped 5 s');
  await at('2', 2.5, 'ok', 'running', 'field "2" is 5 s: 2.5 s is green');
  await at('2', 5.1, 'bad', 'too slow', 'field "2" is 5 s: 5.1 s is red');
  await at('-5', 3, 'warn', 'running slow', 'field "-5" is 5 s: 3 s is yellow');
  await at('1000', 151, 'warn', 'running slow', 'field "1000" is 300 s: 151 s is yellow');
  await at('1000', 301, 'bad', 'too slow', 'field "1000" is 300 s: 301 s is red');
  await at('1000', 150, 'ok', 'running', 'field "1000" is 300 s: 150 s is green');
  for (const field of [null, '', 'abc']) {
    const name = 'field ' + JSON.stringify(field) + ' is the default 20 s';
    await at(field, 10, 'ok', 'running', name + ': 10 s is green');
    p = await at(field, 10.1, 'warn', 'running slow', name + ': 10.1 s is yellow');
    check(p.$('pollerStatus').textContent.endsWith('(20 s).'), 'L5c fix poller ' + name + ': names 20 s');
    await at(field, 20, 'warn', 'running slow', name + ': 20 s is yellow');
    await at(field, 20.1, 'bad', 'too slow', name + ': 20.1 s is red');
  }
  const c = baseConfig(); c.NowPlayingPollSeconds = '2';
  const q = await open(c);
  await q.save();
  check(q.env.saved.NowPlayingPollSeconds === 5, 'L5c fix Save clamps the same way (2 -> 5)');
});

scenario('L5c fix round: refreshing replaces every status line', async () => {
  const p = await open(null, env => { env.webUi = { State: 'Active', FileTransformationVersion: '3.0.0.0' }; });
  const expected = {
    channelStatus: '',
    pollerStatus: 'Now playing updates: ✕ too slow. The last update asked the SUB/WAVE stations someone listens to in 31.5 s, longer than the update interval: the stations update less often than set.',
    webUiStatus: '✓ Active with File Transformation 3.0.0.0. Users may need to reload the web client once.'
  };
  const rowsExpected = [
    '⚠ failing: timed out · last success 2 min ago · image: logo',
    '✓ in the tile · own logo',
    '✕ left out of the tile: an earlier station in the list has the same id'
  ];
  for (let press = 1; press <= 2; press++) {
    p.$('btnHealth').click(); await settle();
    p.$('JellywaveConfigPage').fire('pageshow'); await settle();
    Object.keys(expected).forEach(id => check(p.$(id).textContent === expected[id], 'L5c fix refresh ' + press + ': ' + id + ' exact (' + p.$(id).textContent + ')'));
    check(p.$('stationRows').children.length === 3, 'L5c fix refresh ' + press + ': three rows');
    rowsExpected.forEach((t, i) => check(statusCell(p, i).textContent === t, 'L5c fix refresh ' + press + ': row ' + i + ' status exact (' + statusCell(p, i).textContent + ')'));
  }
});

scenario('L5c server text stays text in the status lines', async () => {
  const c = baseConfig(); c.LibraryName = '<b>Radio</b>';
  const p = await open(c, env => {
    env.health.ChannelStatus = 'another plugin\'s tile has the same name';
    env.webUi = { State: 'Active', FileTransformationVersion: '<b>3</b>' };
  });
  check(headOf(p.$('channelStatus')).textContent === '✕ Another plugin\'s tile is also called "<b>Radio</b>".',
    'L5c a tile name with <b> shows literally in the collision banner (L13 fix round)');
  check(p.$('webUiStatus').textContent === '✓ Active with File Transformation <b>3</b>. Users may need to reload the web client once.', 'L5c a version with <b> shows literally');
  check(p.rows()[0].includes('⚠ failing: timed out'), 'L5c LastError in the row');
  const q = await open(null, env => { env.health.Stations[0].LastError = '<b>x</b>'; });
  check(statusCell(q, 0).textContent.startsWith('⚠ failing: <b>x</b> · last success'), 'L5c a LastError with <b> shows literally');
});

// ---- Card L8: genre, notes and website in the station dialog ----

const plainProbe = extra => Object.assign({
  Ok: true, Kind: 'Stream', StreamUrl: 'https://p.example.org/live.mp3', Codec: 'mp3', Container: 'mp3', BitrateKbps: 128, Name: 'Plain FM',
  Genre: 'Eclectic', Notes: 'Listener-supported <b>radio</b>.', Website: 'http://www.p.example.org/', Lines: []
}, extra);

scenario('L8 the dialog fields and their limits', async () => {
  const flat = html.replace(/\s+/g, ' ');
  check(/<input id="edGenre" type="text" is="emby-input" maxlength="60" \/>/.test(flat), 'L8 genre input with maxlength 60');
  check(/<textarea id="edNotes" is="emby-textarea" class="emby-textarea" rows="5" maxlength="280"><\/textarea>/.test(flat), 'L8 notes textarea with maxlength 280');
  check(/#edNotes \{[^}]*width: 100%;[^}]*height: calc\(5 \* 1\.4em \+ 0\.7em\) !important;[^}]*resize: none;/.test(flat), 'notes box fixed at full width and 5 lines (HI 2026-10-29)');
  check(/<input id="edWebsite" type="text" is="emby-input" maxlength="2048" /.test(flat), 'L8 website input with maxlength 2048');
  const has = (text, name) => check(flat.includes(text), 'L8 help text: ' + name);
  has('Plain streams: optional, at most 60 characters, shown in the station page header. Check fills it from the stream when it is empty.', 'genre');
  has('Optional plain text, shown in the station page header. On a plain stream it is also the item\'s description in every app.', 'notes');
  has('Plain streams: optional http:// or https:// link in the station page header; it opens in a new tab. The server never visits it.', 'website');
  const p = await open();
  p.$('btnAdd').click();
  check(p.$('edNotesCount').textContent === '0 of 280 characters', 'L8 the counter starts at 0');
  p.type('edNotes', 'Five.');
  check(p.$('edNotesCount').textContent === '5 of 280 characters', 'L8 the counter follows typing');
  p.type('edNotes', 'x'.repeat(280));
  check(p.$('edNotesCount').textContent === '280 of 280 characters', 'L8 the counter at the limit');
});

scenario('L8 Check fills empty fields, Save sends them', async () => {
  const p = await open();
  p.$('btnAdd').click(); p.type('edUrl', 'https://p.example.org/live.mp3');
  await p.checkWith(plainProbe());
  check(p.$('edGenre').value === 'Eclectic' && p.$('edWebsite').value === 'http://www.p.example.org/', 'L8 genre and website filled from the check');
  check(p.$('edNotes').value === 'Listener-supported <b>radio</b>.' && p.$('edNotesCount').textContent === 'Listener-supported <b>radio</b>.'.length + ' of 280 characters', 'L8 notes filled through value, markup kept as text, counter updated');
  check(p.$('edGenreBox').style.display === '' && p.$('edWebsiteBox').style.display === '', 'L8 a plain stream shows genre and website');
  p.$('btnEdOk').click();
  await p.save();
  const added = p.env.saved.Stations[3];
  check(added.Genre === 'Eclectic' && added.Notes === 'Listener-supported <b>radio</b>.' && added.Website === 'http://www.p.example.org/', 'L8 Save sends genre, notes and website');

  const q = await open();
  q.$('btnAdd').click(); q.type('edUrl', 'https://p.example.org/live.mp3');
  q.type('edGenre', 'My genre'); q.type('edNotes', 'My notes'); q.type('edWebsite', 'https://mine.example.org/');
  await q.checkWith(plainProbe());
  check(q.$('edGenre').value === 'My genre' && q.$('edNotes').value === 'My notes' && q.$('edWebsite').value === 'https://mine.example.org/', 'L8 a check never overwrites what the admin typed');

  const r = await open();
  r.$('btnAdd').click(); r.type('edUrl', 'https://p.example.org/live.mp3');
  await r.checkWith(plainProbe());
  await r.checkWith(plainProbe({ Genre: 'Jazz', Notes: null, Website: null }));
  check(r.$('edGenre').value === 'Jazz', 'L8 a second check replaces a value the first check filled');
  check(r.$('edNotes').value === 'Listener-supported <b>radio</b>.' && r.$('edWebsite').value === 'http://www.p.example.org/', 'L8 a check without a value leaves the field as it is');
});

scenario('L8 editing keeps the fields; SUB/WAVE shows notes only', async () => {
  const config = baseConfig();
  Object.assign(config.Stations[0], { Genre: 'Chill', Notes: 'From Paradise.', Website: 'https://paradise.example.org/' });
  Object.assign(config.Stations[1], { Genre: 'kept genre', Notes: 'SUB/WAVE notes', Website: 'https://kept.example.org/' });
  const p = await open(config);
  p.rowButton('Edit', 1).click();   // Paradise, plain
  check(p.$('edGenre').value === 'Chill' && p.$('edNotes').value === 'From Paradise.' && p.$('edWebsite').value === 'https://paradise.example.org/', 'L8 Edit shows the stored values');
  check(p.$('edNotesCount').textContent === '14 of 280 characters', 'L8 the counter shows the stored notes\' length');
  p.type('edNotes', 'New notes'); p.$('btnEdOk').click();
  p.rowButton('Edit', 0).click();   // IHateRadio, SUB/WAVE
  check(p.$('edGenreBox').style.display === 'none' && p.$('edWebsiteBox').style.display === 'none', 'L8 SUB/WAVE: genre and website hidden');
  check(p.$('edNotes').value === 'SUB/WAVE notes', 'L8 SUB/WAVE: notes shown');
  p.type('edNotes', 'Changed SUB/WAVE notes'); p.$('btnEdOk').click();
  await p.save();
  const paradise = p.env.saved.Stations.find(x => x.Name === 'Paradise');
  const sub = p.env.saved.Stations.find(x => x.Id === 'live');
  check(paradise.Notes === 'New notes' && paradise.Genre === 'Chill', 'L8 the plain stream\'s edit is saved');
  check(sub.Notes === 'Changed SUB/WAVE notes' && sub.Genre === 'kept genre' && sub.Website === 'https://kept.example.org/', 'L8 a SUB/WAVE station keeps its hidden genre and website');

  const q = await open();
  q.$('btnAdd').click(); q.type('edUrl', 'http://n.example.org/radio/stream.mp3');
  await q.checkWith(Object.assign(subWaveProbe(), { Genre: 'Mount genre', Notes: 'Mount notes', Website: 'https://mount.example.org/' }));
  check(q.$('edGenreBox').style.display === 'none' && q.$('edNotes').value === 'Mount notes', 'L8 a new SUB/WAVE station: notes filled, genre hidden');
  q.$('btnEdOk').click();
  await q.save();
  const added = q.env.saved.Stations[3];
  check(added.Kind === 'SubWave' && added.Notes === 'Mount notes' && added.Genre === '' && added.Website === '', 'L8 a hidden field filled by the check is not saved');
});

scenario('L8 fix (N5): genre, notes and website are saved trimmed', async () => {
  const p = await open();
  p.$('btnAdd').click(); p.type('edUrl', 'https://p.example.org/live.mp3');
  await p.checkWith(plainProbe({ Genre: null, Notes: null, Website: null }));
  p.type('edGenre', '  Jazz  '); p.type('edNotes', '\n Notes \n'); p.type('edWebsite', ' https://w.example.org/ ');
  p.$('btnEdOk').click();
  await p.save();
  const added = p.env.saved.Stations[3];
  check(added.Genre === 'Jazz' && added.Notes === 'Notes' && added.Website === 'https://w.example.org/', 'L8 fix the saved fields are trimmed');
});

scenario('L8 a website the rule refuses gets a hint', async () => {
  const p = await open();
  p.$('btnAdd').click(); p.type('edUrl', 'https://p.example.org/live.mp3');
  const hint = value => { p.type('edWebsite', value); return p.$('edHints').textContent; };
  check(hint('javascript:alert(1)').includes('Hint: Website: only http:// and https:// are allowed; the header leaves it out.'), 'L8 javascript: hint');
  check(hint('data:text/html,x').includes('Website: only http:// and https:// are allowed'), 'L8 data: hint');
  check(hint('https://u:p@x.example.org/').includes('Website: it contains credentials'), 'L8 credentials hint');
  check(hint('x.example.org').includes('Website: it is not an absolute URL'), 'L8 a bare host needs its scheme');
  check(hint('https://x.example.org/' + 'a'.repeat(2048)).includes('Website: it is longer than 2048 characters'), 'L8 too long');
  check(!hint('https://x.example.org/about?x=1#top').includes('Website'), 'L8 an https link with a query and fragment is fine');
  check(!hint('').includes('Website'), 'L8 blank is fine');
});

// ---- The grant and carry result texts (a gap from before L5c, picked up in L8) ----

scenario('L8 the grant button\'s result text', async () => {
  const run = async answer => {
    const p = await open(null, env => { env.answers['Jellywave/GrantAccess'] = answer; });
    p.$('btnGrant').click();
    check(p.$('grantResult').textContent === 'Running…', 'L8 grant: running while the request is out');
    await settle();
    return p.$('grantResult').textContent;
  };
  check((await run({ Granted: 3, AlreadyVisible: 2, Blocked: 1 })) === 'Done: granted 3, already visible 2, blocked by a block list 1.', 'L8 grant: the counts');
  check((await run({ Granted: 0, AlreadyVisible: 0, Blocked: 0 })) === 'Done: granted 0, already visible 0, blocked by a block list 0.', 'L8 grant: zero counts');
  check((await run({ Error: 'the tile does not exist yet (add a station and save)' })) === 'Nothing done: the tile does not exist yet (add a station and save).', 'L8 grant: the server\'s refusal');
  check((await run({ status: 403 })) === 'Request failed: HTTP 403', 'L8 grant: an HTTP failure');
  check((await run({ Error: '<b>x</b>' })) === 'Nothing done: <b>x</b>.', 'L8 grant: server text stays text');
  check(html.includes('<div class="fieldDescription" id="grantResult"></div>') && !html.includes('Save the station settings first'), 'L11 grant: no text before the button is pressed');
});

scenario('L8 the carry button\'s result text and the notice after it', async () => {
  const pending = { Pending: 2, PreviousNames: ['IHateRadio'], TileName: 'Live Radio' };
  const p = await open(null, env => {
    env.carry = pending;
    env.answers['Jellywave/CarryAccess'] = { CarriedGrants: 2, CarriedBlocks: 1, AlreadyCurrent: 4, Untouched: 7 };
  });
  check(p.$('renameNotice').style.display === '' && p.$('renameText').textContent === 'Tile renamed: 2 users could see "IHateRadio" but not "Live Radio" yet.', 'L8 carry: the notice before the press (L13 fix round: the sketch\'s text)');
  p.env.carry = { Pending: 0, PreviousNames: ['IHateRadio'], TileName: 'Live Radio' };
  p.$('btnCarry').click();
  check(p.$('carryResult').textContent === 'Running…', 'L8 carry: running while the request is out');
  await settle();
  check(p.$('carryResult').textContent === 'Done: carried over 2, blocks copied 1, already current 4, not affected 7.', 'L8 carry: the counts (L11: "blocks copied")');
  check(p.$('renameNotice').style.display === '' && p.$('renameText').textContent === 'Every user who named an old tile now has the same access to "Live Radio".', 'L8 carry: the notice stays open with the outcome');

  const q = await open(null, env => { env.carry = pending; env.answers['Jellywave/CarryAccess'] = { Error: 'another plugin\'s tile has the same name' }; });
  q.$('btnCarry').click(); await settle();
  check(q.$('carryResult').textContent === 'Nothing done: another plugin\'s tile has the same name.', 'L8 carry: the server\'s refusal');
  const r = await open(null, env => { env.carry = pending; env.answers['Jellywave/CarryAccess'] = { status: 500 }; });
  r.$('btnCarry').click(); await settle();
  check(r.$('carryResult').textContent === 'Request failed: HTTP 500', 'L8 carry: an HTTP failure');
});

scenario('L10 relay only: no delivery choice, no client URL, no Direct text', async () => {
  // The page itself, markup and script: nothing offers or names a delivery mode or a client stream URL any more.
  ['Delivery', 'Direct', 'Relay', 'ClientStreamUrl', 'AAC in'].forEach(t => check(!html.includes(t), 'L10 the page has no "' + t + '"'));
  check(!/client stream url/i.test(html), 'L10 the page has no "client stream URL", in any case');
  ['edDelivery', 'edDeliveryNote', 'edClientUrl'].forEach(id => check(!ids.includes(id), 'L10 no #' + id));
  const hlsOption = html.match(/<option value="hls">([^<]*)<\/option>/);
  check(hlsOption && hlsOption[1] === 'HLS', 'L10 the HLS container option is plain "HLS" (' + (hlsOption && hlsOption[1]) + ')');
  check(html.includes('Every station plays through this Jellyfin server, which relays it to each listener with ffmpeg.'), 'L10 the stations help line says every station is relayed');

  // A normal round (load, add a checked SUB/WAVE station, add an unchecked stream, edit one, save): the page itself never
  // writes a delivery or client URL key into what it posts.
  const fresh = await open();
  fresh.$('btnAdd').click(); fresh.type('edUrl', 'http://n.example.org/radio/stream.mp3');
  await fresh.checkWith(subWaveProbe()); fresh.$('btnEdOk').click();
  fresh.$('btnAdd').click(); fresh.type('edUrl', 'https://x.example.org/live.mp3'); fresh.$('btnEdOk').click(); fresh.$('btnEdOk').click();
  fresh.rowButton('Edit', 1).click(); fresh.type('edName', 'Paradise 2'); fresh.$('btnEdOk').click();
  await fresh.save();
  const posted = fresh.env.saved.Stations;
  check(posted.length === 5 && posted.every(x => !('Delivery' in x) && !('ClientStreamUrl' in x)), 'L10 the saved stations carry no Delivery or ClientStreamUrl key');

  // An older server's answer that still carries the removed fields: the page shows none of it.
  const config = baseConfig();
  Object.assign(config.Stations[0], { Delivery: 'Direct', ClientStreamUrl: 'https://c.example.org/x.aac' });
  const p = await open(config);
  check(p.$('stationRows').children.every(tr => tr.children.length === 6), 'L10 six columns (L13 fix round: no Format): order, name, type, on, status, actions');
  check(p.rows().every(r => !/Direct|Relay/.test(r)), 'L10 no row names a delivery mode');
  p.rowButton('Edit', 1).click();
  check(p.$('edUrl').value === 'https://s.example.org/x.aac' && !p.$('edHints').textContent.includes('Client'), 'L10 Edit shows the one stream URL and no client-URL hint');
  check(p.$('edContainer').value === 'aac' && p.$('edCodec').value === 'aac', 'L10 the format fields stay under Advanced');
});

// ---- Card L11: first-run fixes (G1), File Transformation status (B), plain words (C) ----

scenario('L11 G1.1 a failed save hides the spinner and says why, next to Save', async () => {
  const p = await open(null, env => { env.saveFails = 500; });
  p.$('btnAdd').click(); p.type('edUrl', 'https://x.example.org/live.mp3'); p.$('btnEdOk').click(); p.$('btnEdOk').click();
  await p.save();
  check(p.env.loading === 0, 'L11 G1.1 the spinner is gone after a failed save (' + p.env.loading + ')');
  checkHead(p.$('saveNote'), 'bad', 'Not saved', 'L11 G1.1 the reason is red');
  check(p.$('saveNote').textContent === '✕ Not saved: Request failed: HTTP 500. Your changes are still on this page; press Save to try again.', 'L11 G1.1 the reason text: ' + p.$('saveNote').textContent);
  check(p.env.saved === null && p.rows().length === 4 && p.rows()[3].includes('not saved yet'), 'L11 G1.1 the page keeps the unsaved station');
  p.env.saveFails = null;
  await p.save();
  check(p.env.saved && p.env.saved.Stations.length === 4 && p.$('saveNote').textContent === '' && p.env.loading === 0, 'L11 G1.1 a second Save goes through and clears the note');

  // The settings read before the post fails.
  const q = await open();
  q.env.fail.push('config');
  await q.save();
  check(q.env.loading === 0 && q.$('saveNote').textContent.startsWith('✕ Not saved: Request failed: HTTP 500.'), 'L11 G1.1 a failed read before the post: the same line');
});

scenario('L11 G1.1 a failed load hides the spinner and blocks Save', async () => {
  const p = await open(null, env => { env.fail.push('config'); });
  check(p.env.loading === 0, 'L11 a failed load leaves no spinner');
  checkHead(p.$('saveNote'), 'bad', 'Couldn\'t load the settings', 'L11 a failed load says so in red');
  p.env.fail.length = 0;
  await p.save();
  check(p.env.saved === null && p.$('saveNote').textContent === '✕ Not saved: the settings did not load. Reload the page before you save.',
    'L11 Save posts nothing after a failed load (the empty list would replace the stations): ' + p.$('saveNote').textContent);
  p.$('JellywaveConfigPage').fire('pageshow'); await settle();
  await p.save();
  check(p.env.saved && p.env.saved.Stations.length === 3, 'L11 after a good load Save works again');
});

scenario('L11 G1.2 a tab close or reload asks only while the list differs from the last save', async () => {
  const p = await open();
  check(!p.unload(), 'L11 G1.2 nothing changed: no prompt');
  p.rowButton('↓', 0).click();
  check(p.unload(), 'L11 G1.2 a reorder: prompt');
  p.rowButton('↑', 1).click();
  check(!p.unload(), 'L11 G1.2 moved back: the list equals the saved one, no prompt');
  const box = p.rowBox(1); box.checked = false; box.fire('change');
  check(p.unload(), 'L11 G1.2 an Enabled untick: prompt');
  box.checked = true; box.fire('change');
  check(!p.unload(), 'L11 G1.2 ticked again: no prompt');
  p.$('btnAdd').click(); p.type('edUrl', 'https://x.example.org/live.mp3');
  check(!p.unload(), 'L11 G1.2 an open dialog alone changes no list: no prompt');
  p.$('btnEdOk').click(); p.$('btnEdOk').click();
  check(p.unload(), 'L11 G1.2 an added station: prompt');
  p.$('JellywaveConfigPage').fire('pagehide');
  check(!p.unload(), 'L11 G1.2 after jellyfin-web hid this view (in-app navigation): no prompt');
  p.$('JellywaveConfigPage').fire('pageshow'); await settle();
  check(!p.unload() && p.rows().length === 3, 'L11 G1.2 shown again: the page reloads the saved list, no prompt');
  p.rowButton('✕', 2).click(); p.rowButton('Remove', 2).click();
  check(p.unload(), 'L11 G1.2 a removed station: prompt');
  await p.save();
  check(!p.unload(), 'L11 G1.2 after Save: no prompt');
  p.rowButton('Edit', 1).click(); p.type('edName', 'Paradise 2'); p.$('btnEdOk').click();
  check(p.unload(), 'L11 G1.2 an applied edit: prompt');
  p.$('JellywaveConfigPage').fire('viewhide');
  check(!p.unload(), 'L11 G1.2 viewhide: no prompt');
  p.$('JellywaveConfigPage').fire('viewdestroy');
  check((p.env.windowListeners.beforeunload || []).length === 0, 'L11 G1.2 viewdestroy removes the listener');
  check(p.$('saveNear').style.display === '' && /<span id="saveNear" style="display:none"><span class="jwHead">Unsaved changes\.<\/span>/.test(html), 'L11 G1.2 the "Unsaved changes" note stays (L13: beside the second Save)');
});

scenario('L11 G1.3 a blank name: the stream URL\'s host, SUB/WAVE for a SUB/WAVE station', async () => {
  const config = baseConfig();
  config.Stations = [
    { Id: 'a', Name: '', Kind: 'Stream', Enabled: true, SortOrder: 0, StreamUrl: 'https://Stream.Example.org:8443/live.mp3?x=1', Codec: 'mp3', Container: 'mp3' },
    { Id: 'b', Name: '  ', Kind: 'Stream', Enabled: true, SortOrder: 1, StreamUrl: 'http://bücher.example/x.mp3', Codec: 'mp3', Container: 'mp3' },
    { Id: 'c', Name: '', Kind: 'SubWave', Enabled: true, SortOrder: 2, BaseUrl: 'http://r.example.org', StreamPath: '/stream.mp3', Codec: 'mp3', Container: 'mp3' },
    { Id: 'd', Name: '', Kind: 'Stream', Enabled: true, SortOrder: 3, StreamUrl: 'not a url', Codec: 'mp3', Container: 'mp3' },
    { Id: 'e', Name: '', Kind: 'Stream', Enabled: true, SortOrder: 4, StreamUrl: 'ftp://files.example/x.mp3', Codec: 'mp3', Container: 'mp3' }
  ];
  const p = await open(config, env => { env.health.Stations = []; });
  const names = p.$('stationRows').children.map(tr => tr.children[1].textContent);
  check(names.join('|') === 'stream.example.org|xn--bcher-kva.example|SUB/WAVE|Unnamed stream|Unnamed stream',
    'L11 G1.3 the names shown, an ftp:// URL "Unnamed stream" as on the server: ' + names.join('|'));
  check(!p.rows()[0].includes('SUB/WAVE') || p.rows()[0].includes('Stream'), 'L11 G1.3 a plain stream is never called SUB/WAVE');
  p.rowButton('Edit', 0).click();
  check(p.$('editorTitle').textContent === 'Edit stream.example.org', 'L11 G1.3 the dialog title');
  check(p.$('edHints').textContent.includes('Hint: Name is blank: the station shows as "stream.example.org".'), 'L11 G1.3 the dialog warns on a blank name');
  p.type('edName', 'Named');
  check(!p.$('edHints').textContent.includes('Name is blank'), 'L11 G1.3 a typed name ends the warning');
  p.$('btnEdCancel').click();
  p.rowButton('Edit', 2).click();
  check(p.$('edHints').textContent.includes('Name is blank: the station shows as "SUB/WAVE".'), 'L11 G1.3 a SUB/WAVE station keeps "SUB/WAVE"');
  p.$('btnEdCancel').click();
  p.$('btnAdd').click(); p.type('edUrl', 'https://new.example.org/a.mp3');
  check(!p.$('edHints').textContent.includes('Name is blank'), 'L11 N2 a URL typed into an Add box: no blank-name hint before Check');
  await p.checkWith({ Ok: true, Kind: 'Stream', StreamUrl: 'https://new.example.org/a.mp3', Codec: 'mp3', Container: 'mp3', Name: 'New FM', Lines: [] });
  check(!p.$('edHints').textContent.includes('Name is blank'), 'L11 G1.3 a name filled by Check: no warning');
  p.type('edName', '');
  check(p.$('edHints').textContent.includes('Name is blank: the station shows as "new.example.org".'), 'L11 G1.3 the name cleared after a Check: the warning');
  p.$('btnEdCancel').click();
  p.$('btnAdd').click(); p.type('edUrl', 'https://quiet.example.org/a.mp3');
  await p.checkWith({ Ok: true, Kind: 'Stream', StreamUrl: 'https://quiet.example.org/a.mp3', Codec: 'mp3', Container: 'mp3', Lines: [] });
  check(p.$('edHints').textContent.includes('Name is blank: the station shows as "quiet.example.org".'), 'L11 N2 a Check that finds no name: the warning');
  p.$('btnEdCancel').click();
  p.$('btnAdd').click(); p.type('edUrl', 'https://down.example.org/a.mp3');
  p.$('btnEdOk').click();
  check(p.$('stationEditor').style.display === '' && p.$('edHints').textContent.includes('Name is blank: the station shows as "down.example.org".'),
    'L11 N2 Add to list pressed without a Check: the warning shows with the unchecked one');
});

scenario('L11 fix round: a failed check answer turns the blank-name hint on too', async () => {
  const p = await open();
  p.$('btnAdd').click(); p.type('edUrl', 'https://down.example.org/a.mp3');
  check(!p.$('edHints').textContent.includes('Name is blank'), 'L11 N2 before the Check: no blank-name hint');
  p.$('btnCheck').click();
  p.env.probes.shift().res(Promise.reject({ status: 502 })); await settle();
  check(p.$('edLines').textContent.includes('Request failed: HTTP 502') && p.$('edHints').textContent.includes('Name is blank: the station shows as "down.example.org".'),
    'L11 N2 a Check that failed: the warning');
});

scenario('L11 fix round (S1): a later good load clears a stale save or load note', async () => {
  const p = await open(null, env => { env.saveFails = 500; });
  p.rowButton('↓', 0).click();
  await p.save();
  check(p.$('saveNote').textContent.startsWith('✕ Not saved:'), 'L11 S1 the failed save says so');
  p.env.saveFails = null;
  p.$('JellywaveConfigPage').fire('pageshow'); await settle();
  check(p.$('saveNote').textContent === '', 'L11 S1 back on the page after a reload: the note is gone (' + p.$('saveNote').textContent + ')');
  const q = await open(null, env => { env.fail.push('config'); });
  check(q.$('saveNote').textContent.startsWith('✕ Couldn\'t load the settings'), 'L11 S1 a failed load says so');
  q.env.fail.length = 0;
  q.$('JellywaveConfigPage').fire('pageshow'); await settle();
  check(q.$('saveNote').textContent === '', 'L11 S1 a later good load clears it');
});

scenario('L11 fix round (S4): a failed reload after a good one blocks Save', async () => {
  const p = await open();
  p.env.fail.push('config');
  p.$('JellywaveConfigPage').fire('pageshow'); await settle();
  check(p.env.loading === 0 && p.$('saveNote').textContent.startsWith('✕ Couldn\'t load the settings'), 'L11 S4 the failed reload says so, no spinner');
  p.env.fail.length = 0;
  await p.save();
  check(p.env.saved === null && p.$('saveNote').textContent === '✕ Not saved: the settings did not load. Reload the page before you save.',
    'L11 S4 Save posts nothing after the failed reload');
});

scenario('L11 G1.6 the Add box shows no hints until the URL is typed in or Check is pressed', async () => {
  const p = await open();
  p.$('btnAdd').click();
  check(p.$('edHints').children.length === 0, 'L11 G1.6 an untouched Add box: no hints');
  p.type('edName', 'Typed first');
  check(p.$('edHints').children.length === 0, 'L11 G1.6 typing a name only: still no hints');
  p.type('edUrl', 'https://u:p@x.example.org/a.mp3');
  check(p.$('edHints').textContent.includes('Stream URL: it contains credentials'), 'L11 G1.6 typing a URL shows the hints');
  p.type('edUrl', '');
  check(p.$('edHints').textContent.includes('No stream URL yet.'), 'L11 G1.6 a URL typed and cleared: "No stream URL yet"');
  p.$('btnEdCancel').click();
  p.$('btnAdd').click();
  check(p.$('edHints').children.length === 0, 'L11 G1.6 a new Add box starts untouched again');
  p.$('btnCheck').click();
  check(p.$('edHints').textContent.includes('No stream URL yet.') && p.$('edLines').textContent.includes('Enter the stream URL first.'), 'L11 G1.6 Check on an empty box shows the hints');
  p.$('btnEdCancel').click();
  const config = baseConfig();
  config.Stations[0].LogoUrl = 'ftp://logo.example.org/a.png';
  const q = await open(config);
  q.rowButton('Edit', 1).click();   // Paradise
  check(q.$('edHints').textContent.includes('Logo URL: only http:// and https:// are allowed'), 'L11 G1.6 Edit shows a stored station\'s hints at once');
});

scenario('L11 C plain words on the page', async () => {
  const markup = html.replace(/<script[\s\S]*?<\/script>/g, '').replace(/<style[\s\S]*?<\/style>/g, '').replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ');
  // The script's string literals, without comments and without camel-case literals (element ids, kinds, routes).
  const script = scripts[0].replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '').replace(/;\s*\/\/.*$/gm, ';');
  const literals = [...script.matchAll(/'((?:[^'\\]|\\.)*)'/g)].map(m => m[1]).filter(t => !/^(?:[a-z]+[A-Z]\w*|[A-Z][a-z]+[A-Z]\w*|Jellywave\/\w+)$/.test(t));
  const words = markup + ' ' + literals.join(' ');
  // "Channels" (the audio field) and "Refresh Channels" (Jellyfin's task name) are plural and stay.
  check((words.match(/\bchannel\b/gi) || []).length === 1 && words.includes('where Jellyfin calls the tile a channel'), 'L11 C "channel" once, in Who can see it');
  check(!/\bpoll(er|ing| interval)?\b/i.test(words), 'L11 C no "poll", "poller" or "poll interval" on the page');
  check(!/\b(not )?listed\b/i.test(words), 'L11 C no "listed" / "not listed" on the page');
  check(!words.includes('Carry access over') && !words.includes('Refresh status') && !/\bKind\b/.test(words), 'L11 C no "Carry access over", "Refresh status" or "Kind"');
  check(html.includes('<th scope="col">Type</th>') && html.includes('<span>Reload status</span>') && html.includes('<span>Give them access to the renamed tile</span>')
    && html.includes('for="NowPlayingPollSeconds">Update every (seconds)</label>'), 'L11 C Type, Reload status, Give them access to the renamed tile, Update every (seconds)');
  const p = await open();
  check(p.rows()[0].includes(' | SUB/WAVE | ') && p.rows()[1].includes(' | Stream | '), 'L11 C the Type column: "SUB/WAVE" and "Stream"');
  p.$('btnAdd').click();
  check(p.$('btnEdOkText').textContent === 'Add to list' && p.$('edKind').textContent === 'Type: not checked yet. Press Check; the type comes from the station.', 'L11 C "Add to list" and the type line');
  p.type('edUrl', 'https://x.example.org/live.mp3');
  p.$('btnEdOk').click();
  check(p.$('edLines').textContent.includes('press Add to list again to save it as a plain stream'), 'L11 C the unchecked warning names "Add to list"');
  await p.checkWith({ Ok: true, Kind: 'Stream', StreamUrl: 'https://x.example.org/live.mp3', Codec: 'mp3', Container: 'mp3', Lines: [{ Level: 'ok', Text: 'Radio stream.' }] });
  check(p.$('edKind').textContent === 'Type: stream (checked).' && p.$('edLines').textContent === '✓ Radio stream.', 'L11 C a checked plain stream: "✓ Radio stream." and its type');
  p.$('btnEdCancel').click();
  p.rowButton('Edit', 0).click();
  check(p.$('btnEdOkText').textContent === 'Apply to list' && p.$('edKind').textContent === 'Type: SUB/WAVE station.', 'L11 C "Apply to list" and a SUB/WAVE station\'s type');
  await p.save();
  check(p.$('saveNote').textContent === 'Finish the station dialog first (Apply to list or Cancel), then Save.', 'L11 C the save note names "Apply to list"');
  const q = await open(null, env => { env.health.ChannelStatus = 'another plugin\'s tile has the same name'; });
  check(q.$('channelStatus').textContent === '✕ Another plugin\'s tile is also called "Live Radio". Jellywave does nothing until you rename it (Advanced → Tile name).', 'L11 C the collision line says "tile" (L13 fix round: the sketch\'s text)');
  const r = await open(null, env => { env.carry = { Pending: 1, PreviousNames: ['Old'], TileName: 'Live Radio', SkippedOtherChannel: 1 }; });
  check(r.$('renameText').textContent === 'Tile renamed: 1 user could see "Old" but not "Live Radio" yet.', 'L11 C the rename notice (L13 fix round: the sketch\'s text): ' + r.$('renameText').textContent);
  check(r.$('carrySkipped').textContent === '1 earlier tile name now belongs to another plugin\'s tile: access to it is not given to this tile, but users blocked from it still do not see this tile.', 'L11 C the skipped line');
});

scenario('L11 B File Transformation registration failed stays red', async () => {
  const p = await open(null, env => { env.webUi = { State: 'Failed' }; });
  checkHead(p.$('webUiStatus'), 'bad', 'Not active', 'L11 B Failed: red');
  const q = await open(null, env => { env.webUi = { State: 'NotInstalled' }; });
  check(!(headOf(q.$('webUiStatus')).className || '').includes('jwBad') && !q.$('webUiStatus').textContent.includes('✕'), 'L11 B NotInstalled: no red, no ✕');
});

// ---- Card L13 A: stations first, an Advanced fold, banners only when they apply, a second Save ----

scenario('L13 A the page order and the Advanced fold', async () => {
  const at = text => html.indexOf(text);
  const order = ['id="pageIntro"', 'id="banners"', '>Stations</h2>', '>Who can see it</h2>', '>Live panel</h2>', 'id="advancedSettings"', 'id="btnSave"'];
  check(order.every(t => at(t) > 0) && order.every((t, i) => i === 0 || at(order[i - 1]) < at(t)), 'L13 A order: intro, banners, Stations, Who can see it, Live panel, Advanced, Save');
  const fold = html.slice(at('<details class="verticalSection" id="advancedSettings">'), html.indexOf('</details>', at('id="advancedSettings"')));
  check(at('<details class="verticalSection" id="advancedSettings">') > 0 && !/<details[^>]*id="advancedSettings"[^>]*\bopen\b/.test(html), 'L13 A Advanced is a fold, closed by default');
  check(fold.includes('<summary><h2 class="sectionTitle jwSummaryTitle">Advanced</h2></summary>'), 'L13 A the fold is called "Advanced"');
  ['id="LibraryName"', 'id="NowPlayingEnabled"', 'id="CoverArtEnabled"', 'id="NowPlayingPollSeconds"', 'id="pollerStatus"']
    .forEach(id => check(fold.includes(id) && html.split(id).length === 2, 'L13 A inside Advanced, once: ' + id));
  check(!fold.includes('id="stationTable"') && !fold.includes('id="btnGrant"') && !fold.includes('id="webUiStatus"'), 'L13 A the stations, the grant button and the File Transformation line stay outside the fold');
  check(!html.includes('>Live Radio tile</h2>') && !html.includes('at the bottom'), 'L13 A no "Live Radio tile" section and no "at the bottom" any more');
  const p = await open();
  check(!p.$('advancedSettings').open, 'L13 A a tile that is fine leaves the fold closed');
  check(p.$('introTileName').textContent === 'Live Radio', 'L13 A the intro names the tile');
});

scenario('L13 A same fields, same defaults', async () => {
  const p = await open({ Stations: [] });
  check(p.$('LibraryName').value === 'Live Radio' && p.$('introTileName').textContent === 'Live Radio', 'L13 A a blank tile name loads as "Live Radio"');
  check(p.$('CoverArtEnabled').checked && !p.$('NowPlayingEnabled').checked && p.$('NowPlayingPollSeconds').value === '', 'L13 A the now-playing fields load as before');
  await p.save();
  check(p.env.saved.LibraryName === 'Live Radio' && p.env.saved.NowPlayingPollSeconds === 20 && p.env.saved.CoverArtEnabled === true && p.env.saved.NowPlayingEnabled === false,
    'L13 A Save sends the same defaults: ' + JSON.stringify([p.env.saved.LibraryName, p.env.saved.NowPlayingPollSeconds, p.env.saved.CoverArtEnabled, p.env.saved.NowPlayingEnabled]));
  const c = baseConfig(); c.LibraryName = 'Radio <b>2</b>';
  const q = await open(c);
  q.$('LibraryName').value = 'Changed';
  check(q.$('introTileName').textContent === 'Radio <b>2</b>', 'L13 A the intro names the saved tile, as text');
});

scenario('L13 A banners show only when they apply', async () => {
  const banners = ['channelStatus', 'renameNotice', 'carrySkipped', 'configWarning'];
  const p = await open();
  banners.forEach(id => check(p.$(id).style.display === 'none', 'L13 A nothing to say: ' + id + ' hidden'));
  const q = await open(null, env => {
    env.health.ChannelStatus = 'another plugin\'s tile has the same name';
    env.health.ConfigurationWarning = 'the configuration file could not be loaded';
    env.carry = { Pending: 2, PreviousNames: ['Old'], TileName: 'Live Radio', SkippedOtherChannel: 1 };
  });
  banners.forEach(id => check(q.$(id).style.display === '', 'L13 A when it applies: ' + id + ' shown'));
  check(q.$('advancedSettings').open, 'L13 A a name collision opens Advanced, where the tile name is');
  checkHead(q.$('configWarning'), 'warn', 'Jellywave could not read its settings file and runs on defaults.', 'L13 A the settings-file banner is yellow');
  check(q.$('configWarning').textContent === '⚠ Jellywave could not read its settings file and runs on defaults.' && q.$('configWarning').className === 'jwBanner jwBannerWarn',
    'L13 A the settings-file banner text: ' + q.$('configWarning').textContent);
  check(q.$('renameHead').textContent === '⚠ ' && q.$('renameHead').className === 'jwHead jwWarn' && q.$('renameNotice').className === 'jwBanner jwBannerWarn', 'L13 A the rename notice is a yellow banner');
  q.env.answers['Jellywave/CarryAccess'] = { CarriedGrants: 2, CarriedBlocks: 0, AlreadyCurrent: 0, Untouched: 3 };
  q.env.carry = { Pending: 0, PreviousNames: ['Old'], TileName: 'Live Radio' };
  q.$('btnCarry').click(); await settle();
  check(q.$('renameHead').textContent === '✓ ' && q.$('renameNotice').className === 'jwBanner jwBannerOk' && q.$('renameNotice').style.display === '', 'L13 A after the press the notice stays, green');
  // Health answers again without the collision or the warning: both banners go.
  q.env.health = baseHealth();
  q.$('btnHealth').click(); await settle();
  check(q.$('channelStatus').style.display === 'none' && q.$('channelStatus').textContent === '' && q.$('configWarning').style.display === 'none', 'L13 A a banner goes when its state goes');
});

scenario('L9 fix a fresh install whose first save failed says it could not save, not read', async () => {
  // No file and no old tile: the load-failure words ("could not read", "defaults") would send the admin the wrong way.
  const p = await open(null, env => {
    env.health.ConfigurationWarning = 'the new configuration could not be saved at startup';
    env.health.ConfigurationUnsaved = true;
  });
  checkHead(p.$('configWarning'), 'warn', 'Jellywave could not save its settings file.', 'L9 fix the unsaved banner is yellow');
  check(p.$('configWarning').textContent === '⚠ Jellywave could not save its settings file. The stations are kept in memory until a save works (see the server log).'
    && p.$('configWarning').className === 'jwBanner jwBannerWarn' && p.$('configWarning').style.display === '',
    'L9 fix the unsaved banner text: ' + p.$('configWarning').textContent);
  // The flag alone, without a warning, shows nothing (the server sets both together).
  const q = await open(null, env => { env.health.ConfigurationUnsaved = true; });
  check(q.$('configWarning').style.display === 'none', 'L9 fix no warning, no banner');
});

// ---- Card R1: an unreadable settings file is kept, and a save it refuses says why ----

scenario('R1 the settings-file banner names the kept copy, or says saves are refused', async () => {
  const copy = 'Jellyfin.Plugin.Jellywave.xml.unreadable-20261030T123456789Z.xml';
  const p = await open(null, env => {
    env.health.ConfigurationWarning = 'the configuration file could not be loaded at startup';
    env.health.ConfigurationUnreadableCopy = copy;
  });
  checkHead(p.$('configWarning'), 'warn', 'Jellywave could not read its settings file and runs on defaults.', 'R1 a kept copy: the banner is yellow');
  check(p.$('configWarning').textContent === '⚠ Jellywave could not read its settings file and runs on defaults. The file was kept as ' + copy
    + ' in the plugin configurations folder; Save writes a new settings file.', 'R1 a kept copy: the banner names it: ' + p.$('configWarning').textContent);
  const q = await open(null, env => {
    env.health.ConfigurationWarning = 'the configuration file could not be loaded at startup and could not be copied aside';
    env.health.ConfigurationSaveRefusal = 'no copy could be kept';
  });
  checkHead(q.$('configWarning'), 'bad', 'Jellywave could not read its settings file and could not keep a copy of it.', 'R1 no copy: the banner is red');
  check(q.$('configWarning').textContent.includes('Save is refused until the copy works, so the file is never overwritten.')
    && q.$('configWarning').textContent.includes('Check that Jellyfin can read the settings file and write to its folder (see the server log).')
    && !q.$('configWarning').textContent.includes('.xml') && q.$('configWarning').className === 'jwBanner jwBannerBad',
    'R1 no copy: no file is named, and it says saves are refused: ' + q.$('configWarning').textContent);
  // A copy name with markup stays text.
  const r = await open(null, env => {
    env.health.ConfigurationWarning = 'x';
    env.health.ConfigurationUnreadableCopy = '<b>x</b>.xml';
  });
  check(r.$('configWarning').textContent.includes('kept as <b>x</b>.xml in'), 'R1 the copy name is written as text');
});

scenario('R1 a save the server refuses shows its reason next to both Saves', async () => {
  const reason = 'the settings file could not be read at startup and no copy of it could be kept, so Jellywave does not write over it';
  const p = await open(null, env => {
    env.saveFails = 500;
    env.health.ConfigurationWarning = 'x';
    env.health.ConfigurationSaveRefusal = reason;
  });
  p.rowButton('↓', 0).click();
  await p.save();
  checkHead(p.$('saveNote'), 'bad', 'Not saved', 'R1 the refusal is red');
  check(p.$('saveNote').textContent === '✕ Not saved: ' + reason + '. Your changes are still on this page; press Save to try again.'
    && p.$('saveNoteNear').textContent === p.$('saveNote').textContent, 'R1 the server\'s reason, beside both Saves: ' + p.$('saveNote').textContent);
  check(p.env.loading === 0 && p.env.saved === null && p.rows()[1].includes('IHateRadio'), 'R1 the spinner goes and the page keeps the change');
  // Once the copy works the server takes the save; the note goes.
  p.env.saveFails = null;
  p.env.health = baseHealth();
  await p.save();
  check(p.env.saved && p.$('saveNote').textContent === '', 'R1 the next Save goes through and clears the note');

  // A failed save without a refusal keeps the plain reason.
  const q = await open(null, env => { env.saveFails = 500; });
  q.rowButton('↓', 0).click();
  await q.save();
  check(q.$('saveNote').textContent === '✕ Not saved: Request failed: HTTP 500. Your changes are still on this page; press Save to try again.', 'R1 no refusal: the HTTP reason stays');
});

scenario('R1 the page sends no change time of its own', async () => {
  const c = baseConfig(); c.LastChangedUtc = '2020-01-01T00:00:00Z';
  const p = await open(c);
  p.rowButton('↓', 0).click();
  await p.save();
  check(p.env.saved && p.env.saved.LastChangedUtc === '2020-01-01T00:00:00Z', 'R1 LastChangedUtc goes back as read; the server stamps it: ' + (p.env.saved && p.env.saved.LastChangedUtc));
});

scenario('L13 A a second Save beside the table, only while there are unsaved changes', async () => {
  const p = await open();
  const near = () => p.$('saveNear').style.display === '';
  check(!near() && p.$('saveNoteNear').style.display === 'none', 'L13 A saved: no second Save');
  p.rowButton('↓', 0).click();
  check(near(), 'L13 A a move: the second Save shows');
  p.rowButton('↑', 1).click();
  check(!near(), 'L13 A moved back: it goes');
  const box = p.rowBox(1); box.checked = false; box.fire('change');
  check(near() && p.$('tableNote').textContent === '', 'L13 A an untick: it shows (the table note stays empty)');
  p.$('LibraryName').value = 'Other';
  p.$('btnSaveNear').click(); await settle();
  check(p.env.saved === null && p.$('saveNoteNear').textContent.startsWith('Renaming makes a new tile.'), 'R2b a renamed tile asks first beside the second Save');
  p.$('saveNoteNear').all().find(b => b.tagName === 'button' && b.textContent === 'Rename').click(); await settle();
  check(p.env.saved && p.env.saved.Stations.find(s => s.Name === 'Paradise').Enabled === false && p.env.saved.LibraryName === 'Other', 'L13 A the second Save saves the whole page');
  check(!near(), 'L13 A after the save it goes');
  p.rowButton('✕', 2).click(); p.rowButton('Remove', 2).click();
  check(near() && p.$('tableNote').textContent === 'Station removed: press Save to apply.', 'L13 A a removal: the note and the second Save');
  p.env.saveFails = 500;
  p.$('btnSaveNear').click(); await settle();
  check(near() && p.$('saveNoteNear').style.display === '' && p.$('saveNoteNear').textContent === p.$('saveNote').textContent
    && p.$('saveNoteNear').textContent.startsWith('✕ Not saved: Request failed: HTTP 500.'), 'L13 A a failed save says why beside the second Save too');
  p.rowButton('Edit', 0).click();
  p.$('btnSaveNear').click(); await settle();
  check(p.$('saveNoteNear').textContent.startsWith('Finish the station dialog first'), 'L13 A the second Save refuses while the dialog is open');
  p.$('btnEdCancel').click();
  p.env.saveFails = null;
  p.$('btnSaveNear').click(); await settle();
  check(!near() && p.$('saveNote').textContent === '' && p.env.saved.Stations.length === 2, 'L13 A then it saves and goes');
  check(html.includes('<button is="emby-button" type="button" id="btnSaveNear"'), 'L13 A the second Save is a plain button (it runs the same save, no second form submit)');
});

// ---- Card L13 D: a plain stream's last title read in the status column ----

scenario('L13 D a plain stream: answered, no answer, or in the tile', async () => {
  const minutes = n => new Date(Date.now() - n * 60000).toISOString();
  const at = async (fields, level, words, text, name) => {
    const p = await open(null, env => { Object.assign(env.health.Stations[1], fields); });
    checkHead(statusCell(p, 1), level, words, 'L13 D ' + name);
    check(statusCell(p, 1).textContent === text, 'L13 D ' + name + ' text: ' + statusCell(p, 1).textContent);
    return p;
  };
  await at({ LastReadOutcome: 'Answered', LastReadUtc: minutes(4) }, 'ok', 'answered', '✓ answered 4 min ago · own logo', 'answered: green with the time');
  await at({ LastReadOutcome: 'NoAnswer', LastReadUtc: minutes(120) }, 'warn', 'no answer', '⚠ no answer 2 h ago · own logo', 'no answer: yellow with the time');
  await at({ LastReadOutcome: 'Moved', LastReadUtc: minutes(4) }, 'moved', 'moved', '↪ moved 4 min ago: Edit → Check → Use it to read titles · own logo', 'moved (fix round S2): yellow ↪ with what to do');
  await at({ LastReadOutcome: 'Answered', LastReadUtc: minutes(3 * 24 * 60) }, 'ok', 'answered', '✓ answered 3 d ago · own logo', 'fix round N1: days');
  await at({ LastReadOutcome: 'Answered', LastReadUtc: minutes(47 * 60) }, 'ok', 'answered', '✓ answered 47 h ago · own logo', 'fix round N1: hours up to 48');
  await at({ LastReadOutcome: 'NoAnswer', LastReadUtc: minutes(-30) }, 'warn', 'no answer', '⚠ no answer 0 s ago · own logo', 'fix round N1: a time in the future reads 0 s ago');
  await at({ LastReadOutcome: 'Answered' }, 'ok', 'in the tile', '✓ in the tile · own logo', 'an outcome without a time: in the tile');
  await at({}, 'ok', 'in the tile', '✓ in the tile · own logo', 'never read: in the tile');
  await at({ LastReadOutcome: null, LastReadUtc: null }, 'ok', 'in the tile', '✓ in the tile · own logo', 'null fields: in the tile');
  await at({ LastReadOutcome: 'Answered', LastReadUtc: minutes(4), Logo: 'failed' }, 'warn', 'answered', '⚠ answered 4 min ago · ⚠ logo failed; retried within 30 min', 'answered with a failed logo: yellow');
  // An HLS stream is never read: the server sends no read fields, and the row says "in the tile".
  const c = baseConfig(); c.Stations[0].StreamUrl = 'https://s.example.org/live/index.m3u8'; c.Stations[0].Container = 'hls';
  const p = await open(c);
  check(statusCell(p, 1).textContent === '✓ in the tile · own logo', 'L13 D an HLS stream: "in the tile"');
  // A SUB/WAVE row ignores read fields.
  const q = await open(null, env => { Object.assign(env.health.Stations[0], { LastReadOutcome: 'NoAnswer', LastReadUtc: minutes(1) }); });
  check(statusCell(q, 0).textContent.startsWith('⚠ failing: timed out'), 'L13 D a SUB/WAVE row keeps its own status');
});

// ---- Card L13 E: who can see the tile, and who of them cannot play ----

scenario('L13 E the count and the transcoding warning', async () => {
  const p = await open();
  check(p.$('viewerCount').textContent === '4 of 6 active users can see the tile.', 'L13 E the count (fix round: active users): ' + p.$('viewerCount').textContent);
  check(p.$('noTranscode').style.display === 'none' && p.$('noTranscode').textContent === '', 'L13 E nobody without transcoding: no warning');
  const at = html.indexOf.bind(html);
  check(at('id="viewerCount"') < at('id="btnGrant"') && at('id="btnGrant"') < at('id="noTranscode"') && at('id="noTranscode"') < at('>Live panel</h2>'),
    'L13 E the count above the button, the warning below it, all under Who can see it');
  const one = await open(null, env => { env.viewers = { Users: 6, Visible: 4, NoAudioTranscoding: ['testB'] }; });
  checkHead(one.$('noTranscode'), 'warn', '', 'L13 E one user: a yellow warning');
  check(one.$('noTranscode').style.display === '' && one.$('noTranscode').textContent === '⚠ testB can see the tile but can\'t play stations: "Allow audio playback that requires transcoding" is off for them (Dashboard → Users → testB → Profile → Media playback).',
    'L13 E one user text: ' + one.$('noTranscode').textContent);
  const two = await open(null, env => { env.viewers = { Users: 1, Visible: 1, NoAudioTranscoding: ['<b>a</b>', 'b'] }; });
  check(two.$('viewerCount').textContent === '1 of 1 active user can see the tile.', 'L13 E one user in all: "user"');
  check(two.$('noTranscode').textContent === '⚠ <b>a</b>, b can see the tile but can\'t play stations: "Allow audio playback that requires transcoding" is off for them (Dashboard → Users → each user → Profile → Media playback).',
    'L13 E two users, names as text: ' + two.$('noTranscode').textContent);
  const names = n => Array.from({ length: n }, (_, i) => 'u' + (i + 1));
  const ten = await open(null, env => { env.viewers = { Users: 10, Visible: 10, NoAudioTranscoding: names(10) }; });
  check(ten.$('noTranscode').textContent.startsWith('⚠ u1, u2, u3, u4, u5, u6, u7, u8, u9, u10 can see the tile'), 'L13 E fix round: ten names, all shown');
  const many = await open(null, env => { env.viewers = { Users: 40, Visible: 40, NoAudioTranscoding: names(13) }; });
  check(many.$('noTranscode').textContent.startsWith('⚠ u1, u2, u3, u4, u5, u6, u7, u8, u9, u10 and 3 more can see the tile but can\'t play stations') && !many.$('noTranscode').textContent.includes('u11'),
    'L13 E fix round: at most ten names, then "and N more": ' + many.$('noTranscode').textContent);
});

scenario('L13 E an error, a failed request, and the reads after a press', async () => {
  const p = await open(null, env => { env.viewers = { Error: 'the tile does not exist yet (add a station and save)', NoAudioTranscoding: ['ghost'] }; });
  check(p.$('viewerCount').textContent === 'Not counted: the tile does not exist yet (add a station and save).' && p.$('noTranscode').style.display === 'none',
    'L13 E an error: not counted, no warning: ' + p.$('viewerCount').textContent);
  const q = await open(null, env => { env.fail.push('Jellywave/Viewers'); });
  checkHead(q.$('viewerCount'), 'warn', 'Couldn\'t count', 'L13 E a failed request: yellow');
  check(q.$('viewerCount').textContent === '⚠ Couldn\'t count. Request failed: HTTP 500.' && q.$('noTranscode').style.display === 'none', 'L13 E a failed request: text, no warning');
  const r = await open(null, env => { env.viewers = { Users: 6, Visible: 4, NoAudioTranscoding: ['testB'] }; });
  check(r.env.viewerReads === 1, 'L13 E read once when the page shows');
  r.env.viewers = { Users: 6, Visible: 6, NoAudioTranscoding: [] };
  r.$('btnGrant').click(); await settle();
  check(r.env.viewerReads === 2 && r.$('viewerCount').textContent === '6 of 6 active users can see the tile.' && r.$('noTranscode').style.display === 'none' && r.$('noTranscode').textContent === '',
    'L13 E the grant press reads again, and a warning that no longer applies goes');
  await r.save();
  check(r.env.viewerReads === 3, 'L13 E a save reads again (a rename makes a new tile)');
  r.$('btnCarry').click(); await settle();
  check(r.env.viewerReads === 4, 'L13 E the rename button reads again');
  check(!r.env.posts.some(x => x.url === 'Jellywave/Viewers'), 'L13 E the page only reads (no request but a GET)');
});

// ---- Card L13 fix round ----

scenario('L13 fix S1 both Saves check the interval field and open Advanced for it', async () => {
  for (const press of ['bottom', 'near']) {
    const p = await open();
    p.rowButton('↓', 0).click();   // a change, so the second Save shows
    p.$('NowPlayingPollSeconds').value = '3';
    p.$('NowPlayingPollSeconds').invalid = true;   // the browser refuses 3 (min 5)
    if (press === 'bottom') { await p.save(); } else { p.$('btnSaveNear').click(); await settle(); }
    check(p.env.saved === null && p.$('advancedSettings').open && p.$('NowPlayingPollSeconds').reported === 1 && p.env.loading === 0,
      'L13 fix S1 ' + press + ' Save: nothing posted, Advanced opened, the field reports why');
    p.$('NowPlayingPollSeconds').invalid = false; p.$('NowPlayingPollSeconds').value = '30';
    if (press === 'bottom') { await p.save(); } else { p.$('btnSaveNear').click(); await settle(); }
    check(p.env.saved && p.env.saved.NowPlayingPollSeconds === 30, 'L13 fix S1 ' + press + ' Save: a valid value saves');
  }
  check(/<form id="JellywaveConfigForm" novalidate>/.test(html), 'L13 fix S1 the form leaves the check to save() (the field may sit in a closed fold)');
});

scenario('L13 fix S3 the second Save after a failed reload posts nothing (A1); a good load clears both notes (A3)', async () => {
  const p = await open();
  p.rowButton('↓', 0).click();
  p.env.fail.push('config');
  p.$('JellywaveConfigPage').fire('pageshow'); await settle();
  p.rowButton('↓', 0).click();
  // A failed load leaves no saved list, so the second Save is hidden; press it anyway (a stale click, a script).
  p.$('btnSaveNear').click(); await settle();
  check(p.env.saved === null && p.$('saveNoteNear').textContent === '✕ Not saved: the settings did not load. Reload the page before you save.',
    'L13 fix A1 the second Save keeps the failed-load guard: ' + p.$('saveNoteNear').textContent);
  p.env.fail = [];
  p.$('JellywaveConfigPage').fire('pageshow'); await settle();
  check(p.$('saveNote').textContent === '' && p.$('saveNoteNear').textContent === '', 'L13 fix A3 a good load clears the note beside both Saves');
});

scenario('L13 fix N3 the "finish the dialog" note goes with the dialog', async () => {
  const p = await open();
  p.rowButton('↓', 0).click();
  p.rowButton('Edit', 0).click();
  p.$('btnSaveNear').click(); await settle();
  check(p.$('saveNoteNear').textContent.startsWith('Finish the station dialog first') && p.$('saveNote').textContent.startsWith('Finish'), 'L13 fix N3 the note shows beside both Saves');
  p.$('btnEdCancel').click();
  check(p.$('saveNoteNear').textContent === '' && p.$('saveNote').textContent === '', 'L13 fix N3 Cancel clears it');
  p.rowButton('Edit', 0).click();
  await p.save();
  p.$('stationEditor').fire('keydown', { key: 'Escape', preventDefault() {} });
  check(p.$('saveNote').textContent === '', 'L13 fix N3 Escape clears it');
  p.rowButton('Edit', 0).click();
  await p.save();
  p.$('btnEdOk').click();
  check(p.$('saveNote').textContent === '', 'L13 fix N3 Apply to list clears it');
  p.env.saveFails = 500;
  await p.save();
  p.rowButton('Edit', 0).click(); p.$('btnEdCancel').click();
  check(p.$('saveNote').textContent.startsWith('✕ Not saved:'), 'L13 fix N3 a failed-save note is not about the dialog: it stays');
});

scenario('L13 fix N2 an older Viewers answer never replaces a newer one', async () => {
  const p = await open(null, env => { env.viewerHold = true; });
  p.$('btnGrant').click(); await settle();
  check(p.env.viewerHeld.length === 2, 'L13 fix N2 two reads in flight (page show, then the grant)');
  p.env.viewerHeld[1]({ Users: 6, Visible: 6, NoAudioTranscoding: [] }); await settle();
  p.env.viewerHeld[0]({ Users: 6, Visible: 4, NoAudioTranscoding: ['old'] }); await settle();
  check(p.$('viewerCount').textContent === '6 of 6 active users can see the tile.' && p.$('noTranscode').style.display === 'none', 'L13 fix N2 the newer answer stays: ' + p.$('viewerCount').textContent);
});

scenario('L13 fix sketch: table columns and the Live panel heading', async () => {
  check(html.includes('<th scope="col">On</th>') && !html.includes('<th scope="col">Enabled</th>') && !html.includes('<th scope="col">Format</th>'), 'L13 fix the table: "On", no Format column');
  const p = await open();
  check(p.rows()[1] === '↑↓ | Paradise | Stream |  | ✓ in the tile · own logo | Edit✕', 'L13 fix a row has no format text: ' + JSON.stringify(p.rows()[1]));
  check(p.rowBox(1).attrs['aria-label'] === 'Enabled: Paradise', 'L13 fix the box keeps its spoken label');
  const flat = html.replace(/\s+/g, ' ');
  check(flat.includes('<h2 class="sectionTitle">Live panel</h2> <div class="fieldDescription">Optional. Install <a is="emby-linkbutton" class="button-link" href="https://github.com/IAmParadox27/jellyfin-plugin-file-transformation" target="_blank" rel="noopener noreferrer">File Transformation</a> to get a live panel on the tile\'s page. Everything else works without it.</div>')
    && !html.includes('>Station page</h2>'), 'L13 fix "Live panel" with HI\'s option C text and the link');
});

scenario('L14 an idle SUB/WAVE station reads idle, and Reload status asks every station once', async () => {
  const sub = id => ({ Id: id, Name: id, Kind: 'SubWave', Enabled: true, BaseUrl: 'http://' + id + '.example.org', StreamPath: '/stream.mp3', Codec: 'mp3', Container: 'mp3' });
  const config = baseConfig();
  config.Stations = ['idle', 'old', 'checked', 'checkfail', 'oldfail', 'asked'].map((id, i) => Object.assign(sub(id), { SortOrder: i }));
  const at = s => new Date(Date.now() - s * 1000).toISOString();
  const p = await open(config, env => {
    env.health.Stations = [
      { Id: 'idle', Kind: 'SubWave', Enabled: true, Listed: true, Reachable: null, IdleSinceUtc: at(30), Cover: 'logo' },
      // Last heard 5 min ago, idle since 4 min ago: never the old "on air 5 min ago".
      { Id: 'old', Kind: 'SubWave', Enabled: true, Listed: true, Reachable: true, LastSuccessUtc: at(300), IdleSinceUtc: at(240), Cover: 'logo' },
      { Id: 'checked', Kind: 'SubWave', Enabled: true, Listed: true, Reachable: true, LastSuccessUtc: at(3), IdleSinceUtc: at(240), Cover: 'logo' },
      { Id: 'checkfail', Kind: 'SubWave', Enabled: true, Listed: true, Reachable: false, LastError: 'timed out', LastErrorUtc: at(2), LastSuccessUtc: at(300), IdleSinceUtc: at(240) },
      // A failure before the idle time is old news: idle, not failing.
      { Id: 'oldfail', Kind: 'SubWave', Enabled: true, Listed: true, Reachable: false, LastError: 'timed out', LastErrorUtc: at(300), IdleSinceUtc: at(240) },
      { Id: 'asked', Kind: 'SubWave', Enabled: true, Listed: true, Reachable: true, LastSuccessUtc: at(4), IdleSinceUtc: null, Cover: 'track' }
    ];
  });
  const expect = [
    ['off', 'idle', 'idle: nobody listening · image: logo', 'idle: grey, no symbol'],
    ['off', 'idle', 'idle: nobody listening · image: logo', 'idle after an old success: no old "on air" time'],
    ['ok', 'answered', '✓ answered when checked 3 s ago · idle: nobody listening · image: logo', 'idle, then Reload status reached it: green with the check'],
    ['warn', 'failing', '⚠ failing: timed out when checked 2 s ago · idle: nobody listening', 'idle, then Reload status could not reach it: yellow'],
    ['off', 'idle', 'idle: nobody listening', 'idle after an old failure: grey, not failing'],
    ['ok', 'on air', '✓ on air · 4 s ago · image: track cover', 'asked by the poller: on air as before']
  ];
  expect.forEach(([level, words, text, name], i) => {
    checkHead(statusCell(p, i), level, words, 'L14 ' + name);
    check(statusCell(p, i).textContent === text, 'L14 ' + name + ' text: ' + statusCell(p, i).textContent);
  });
  check(p.env.checks === 0 && !p.env.posts.some(r => r.url === 'Jellywave/Health/Check'), 'L14 opening the page asks no station (GET Health only)');

  // Reload status: one POST, the button waits for it, and the answer it brings is shown.
  p.env.health.Stations[0] = { Id: 'idle', Kind: 'SubWave', Enabled: true, Listed: true, Reachable: true, LastSuccessUtc: at(1), IdleSinceUtc: at(30), Cover: 'logo' };
  p.$('btnHealth').click();
  check(p.$('btnHealth').disabled === true, 'L14 Reload status: the button waits while the stations are asked');
  await settle();
  check(p.env.checks === 1 && p.$('btnHealth').disabled === false, 'L14 Reload status posts Health/Check once, then the button is back (' + p.env.checks + ')');
  check(statusCell(p, 0).textContent === '✓ answered when checked 1 s ago · idle: nobody listening · image: logo', 'L14 Reload status shows what the check found: ' + statusCell(p, 0).textContent);
  const post = p.env.posts.find(r => r.url === 'Jellywave/Health/Check');
  check(post && post.type === 'POST' && post.data === undefined, 'L14 the check sends no body');

  // A failed check: the table says it could not check (no stale view), and the button is back.
  p.env.fail.push('Jellywave/Health/Check');
  p.$('btnHealth').click(); await settle();
  checkHead(statusCell(p, 0), 'warn', 'couldn\'t check', 'L14 a failed check drops the stale view');
  check(p.$('btnHealth').disabled === false, 'L14 the button is back after a failed check');

  // Fix round: the button comes back even when showing the failure throws too (here: the poller line is gone).
  // Recorded here, so a rejection the page leaves unhandled fails this check instead of ending the run.
  const line = p.env.byId.pollerStatus;
  const rejections = [];
  const onRejection = e => rejections.push(e);
  process.on('unhandledRejection', onRejection);
  delete p.env.byId.pollerStatus;
  p.$('btnHealth').click(); await settle();
  process.off('unhandledRejection', onRejection);
  check(p.$('btnHealth').disabled === false && rejections.length === 0,
    'L14 fix the button is back when showing a failed check throws, and nothing is left unhandled (' + rejections.length + ')');
  p.env.byId.pollerStatus = line;
});

scenario('L14 fix round: the poller line with every SUB/WAVE station idle', async () => {
  const idle = new Date(Date.now() - 60000).toISOString();
  let p = await open(null, env => {
    env.health.LastFetchSeconds = null;
    env.health.Stations[0].IdleSinceUtc = idle;
  });
  checkHead(p.$('pollerStatus'), 'ok', 'running', 'L14 fix every SUB/WAVE station idle: green');
  check(p.$('pollerStatus').textContent === 'Now playing updates: ✓ running. No SUB/WAVE station is in use (nobody plays one or has its page open), so none is asked.',
    'L14 fix every SUB/WAVE station idle: no old fetch time, and why (' + p.$('pollerStatus').textContent + ')');
  p = await open(null, env => {
    env.health.LastFetchSeconds = null;
    env.health.Stations[0].IdleSinceUtc = idle;
    env.health.Stations.push({ Id: 'off', Kind: 'SubWave', Enabled: false, Listed: false, NotListedReason: 'it is disabled' });
  });
  check(p.$('pollerStatus').textContent.endsWith('so none is asked.'), 'L14 fix a SUB/WAVE station left out of the tile is never asked, so it does not count as in use');
  p = await open(null, env => {
    env.health.LastFetchSeconds = null;
    env.health.Stations.push({ Id: 'two', Kind: 'SubWave', Enabled: true, Listed: true, IdleSinceUtc: idle });
  });
  check(p.$('pollerStatus').textContent === 'Now playing updates: ✓ running.', 'L14 fix one SUB/WAVE station in use: no idle sentence (' + p.$('pollerStatus').textContent + ')');
  p = await open(null, env => {
    env.health.LastFetchSeconds = null;
    env.health.Stations = env.health.Stations.filter(s => s.Kind !== 'SubWave');
  });
  check(p.$('pollerStatus').textContent === 'Now playing updates: ✓ running.', 'L14 fix no SUB/WAVE station listed: no idle sentence');
});

// ---- Card R2b: the exposure warnings and the rename question (HI decisions C and D2, texts HI-approved) ----

const EXPOSURE = 'Tile access only hides the tile. On Jellyfin 12.4, any signed-in user can still read every station\'s address and'
  + ' play it through Jellyfin\'s own API. Anyone who has a station\'s item id can play it without signing in. Jellyfin works this way'
  + ' for all media, not only Jellywave. Don\'t add private streams, or addresses that contain passwords or keys.';
const SECRET = 'Every signed-in user can read this address, even users the tile is hidden from. Leave secrets out of it.';
const textOf = markup => markup.replace(/<[^>]+>/g, '').replace(/\s+/g, ' ').trim();

scenario('R2b the warning near the station list', async () => {
  const at = html.indexOf('id="exposureWarning"');
  check(at > html.indexOf('>Stations</h2>') && at < html.indexOf('id="stationTable"'), 'R2b it sits under the Stations heading, above the list');
  const markup = html.slice(html.lastIndexOf('<div', at), html.indexOf('</div>', at) + 6);
  check(textOf(markup) === '⚠ ' + EXPOSURE, 'R2b the HI-approved text, word for word: ' + textOf(markup));
  check(!/<(?!\/?(div|span)\b)/.test(markup) && !/href|on\w+=/.test(markup), 'R2b plain text in fixed markup (no link, no handler)');
  const p = await open();
  check(p.$('exposureWarning').style.display !== 'none' && !scripts[0].includes('exposureWarning'), 'R2b always shown; the script never touches it');
});

scenario('R2b the dialog warns about a query parameter named like a secret; Add still works', async () => {
  const warned = async url => {
    const p = await open();
    p.$('btnAdd').click(); p.type('edUrl', url);
    const lines = p.$('edHints').children.map(li => li.textContent);
    return lines.filter(t => t === '⚠ ' + SECRET).length;
  };
  const positive = ['https://s.example.org/live.mp3?key=abc', 'https://s.example.org/live.mp3?Token=abc', 'https://s.example.org/live.mp3?a=1&PASSWORD=x',
    'https://s.example.org/live.mp3?sid=1', 'https://s.example.org/live.mp3?Signature=x', 'https://s.example.org/live.mp3?api_key=x',
    'https://s.example.org/live.mp3?accessToken=x', 'https://s.example.org/live.mp3?X-Auth=x', 'https://s.example.org/live.mp3?session_id=x',
    'https://s.example.org/live.mp3?sig=x&secret=y&pass', 'https://s.example.org/live.mp3?SessionID=x']
    // Fix round: joined names, an acronym before a word, digits, and long words at the end of a name.
    .concat(['apikey', 'APIKey', 'APIKEY', 'accesstoken', 'authtoken', 'passkey', 'sessionid', 'PHPSESSID', 'pwd', 'passwd', 'token2', 'key1', 'sig2',
      'X-Amz-Credential', 'hdnts', 'mytoken', 'clientSecret', 'userpassword', 'PHP_SESS_ID', 'XAuth', 'HMACSig'].map(n => 'https://s.example.org/live.mp3?a=1&' + n + '=x'));
  const negative = ['https://s.example.org/live.mp3?monkey=1', 'https://s.example.org/live.mp3?keyboard=1', 'https://s.example.org/live.mp3?passive=1',
    'https://s.example.org/live.mp3?sidebar=1', 'https://s.example.org/live.mp3?q=token', 'https://s.example.org/token/abc/live.mp3',
    'https://s.example.org/live.mp3', 'not a url ?token=1', 'https://s.example.org/live.mp3?bitrate=128&format=mp3&id=7&station=jazz'];
  for (const url of positive) { check(await warned(url) === 1, 'R2b warns once: ' + url); }
  for (const url of negative) { check(await warned(url) === 0, 'R2b no warning: ' + url); }

  // A playlist's stream (the URL a check found) carries the secret: the warning shows after the check; Add still works.
  const p = await open();
  p.$('btnAdd').click(); p.type('edUrl', 'https://p.example.org/list.pls');
  check(!p.$('edHints').textContent.includes(SECRET), 'R2b the playlist URL itself has none');
  await p.checkWith(plainProbe({ StreamUrl: 'https://p.example.org/live.mp3?token=abc' }));
  check(p.$('edHints').children.filter(li => li.textContent === '⚠ ' + SECRET).length === 1, 'R2b the checked stream\'s query does');
  p.$('btnEdOk').click();
  check(p.$('stationEditor').style.display === 'none' && p.rows().length === 4, 'R2b Add to list still works');
  await p.save();
  check(p.env.saved && p.env.saved.Stations[3].StreamUrl === 'https://p.example.org/live.mp3?token=abc', 'R2b and Save sends it as it is');
  const q = await open();
  q.rowButton('Edit', 0).click();
  check(!q.$('edHints').textContent.includes(SECRET), 'R2b editing a station without one: no warning');
  q.type('edUrl', 'https://s.example.org/x.aac?auth=1');
  check(q.$('edHints').textContent.includes(SECRET), 'R2b typing one in: the warning');
  q.type('edUrl', 'https://s.example.org/x.aac?author=1');
  check(!q.$('edHints').textContent.includes(SECRET), 'R2b and it goes when the parameter does');
});

scenario('R2b Save with a changed tile name asks first; Cancel saves nothing', async () => {
  const question = (from, to) => 'Renaming makes a new tile. Users who had "' + from + '" ticked by name will not see "' + to
    + '" until you press Give them access to the renamed tile. Rename? ';
  const noteButton = (p, id, text) => p.$(id).all().find(b => b.tagName === 'button' && b.textContent === text);
  const p = await open();
  p.rowButton('↓', 0).click();
  p.$('LibraryName').value = '  New Radio ';
  await p.save();
  check(p.env.saved === null && p.env.loading === 0, 'R2b nothing is saved before the answer, no spinner');
  check(p.$('saveNote').children[0].textContent === question('Live Radio', 'New Radio'), 'R2b the HI-approved question with the old and new names: ' + p.$('saveNote').children[0].textContent);
  check(p.$('saveNoteNear').children[0].textContent === question('Live Radio', 'New Radio'), 'R2b beside the second Save too');
  check(noteButton(p, 'saveNote', 'Rename') && noteButton(p, 'saveNote', 'Cancel') && noteButton(p, 'saveNote', 'Rename').type === 'button', 'R2b Rename and Cancel, plain buttons (no form submit)');
  noteButton(p, 'saveNote', 'Cancel').click(); await settle();
  check(p.env.saved === null && p.$('saveNote').textContent === '' && p.$('saveNoteNear').textContent === '', 'R2b Cancel: nothing saved, the question goes');
  check(p.$('LibraryName').value === '  New Radio ' && p.$('saveNear').style.display === '', 'R2b and the page stays as it was: the typed name, the unsaved move');
  await p.save();
  noteButton(p, 'saveNote', 'Rename').click(); await settle();
  check(p.env.saved && p.env.saved.LibraryName === 'New Radio' && p.env.saved.Stations[0].Name === 'Paradise', 'R2b Rename: the whole page is saved with the new name');
  check(p.$('introTileName').textContent === 'New Radio' && p.$('saveNote').textContent === '', 'R2b after the save the page shows the new tile name');
  const saves = JSON.stringify(p.env.saved);
  await p.save();
  check(p.$('saveNote').textContent === '' && JSON.stringify(p.env.saved) === saves, 'R2b the same name again: no question');

  const q = await open();
  q.$('LibraryName').value = '';
  await q.save();
  check(q.env.saved && q.env.saved.LibraryName === 'Live Radio', 'R2b a blank name is the default tile: no question');
  const r = await open();
  r.$('LibraryName').value = 'Other';
  await r.save();
  r.type('LibraryName', 'Other 2');
  check(r.$('saveNote').textContent === '' && r.env.saved === null, 'R2b typing in the tile name drops the question');
  await r.save();
  r.$('LibraryName').value = 'Third';   // changed without an input event after the question
  noteButton(r, 'saveNote', 'Rename').click(); await settle();
  check(r.env.saved === null && r.$('saveNote').children[0].textContent === question('Live Radio', 'Third'), 'R2b Rename for a name changed since asks about the new one');
  const c = baseConfig(); c.LibraryName = 'Radio <b>1</b>';
  const t = await open(c);
  t.$('LibraryName').value = '<img src=x onerror=alert(1)>';
  await t.save();
  check(t.$('saveNote').children[0].textContent === question('Radio <b>1</b>', '<img src=x onerror=alert(1)>'), 'R2b the names go in as text');
  const u = await open();
  u.$('LibraryName').value = 'Other';
  u.rowButton('Edit', 0).click();
  await u.save();
  check(u.$('saveNote').textContent.startsWith('Finish the station dialog first'), 'R2b an open dialog is asked about first');
});

scenario('R2b fix: the rename question: focus, the server\'s reading of the name, and a late Health answer', async () => {
  const noteButton = (p, id, text) => p.$(id).all().find(b => b.tagName === 'button' && b.textContent === text);
  const p = await open();
  p.$('LibraryName').value = 'New Radio';
  await p.save();
  noteButton(p, 'saveNote', 'Cancel').click();
  check(p.env.focused === p.$('LibraryName'), 'R2b fix: Cancel puts the focus on the tile name');
  await p.save();
  noteButton(p, 'saveNote', 'Rename').click(); await settle();
  check(p.env.saved && p.env.focused === p.$('btnSave'), 'R2b fix: Rename puts it back on the bottom Save that was pressed');
  const q = await open();
  q.rowButton('↓', 0).click();
  q.$('LibraryName').value = 'New Radio';
  q.$('btnSaveNear').click(); await settle();
  noteButton(q, 'saveNoteNear', 'Rename').click(); await settle();
  check(q.env.saved && q.env.focused === q.$('btnSaveNear'), 'R2b fix: or on the Save beside the table');

  // The server trims the name and reads a blank one as "Live Radio": a save that changes nothing never asks.
  for (const [saved, typed] of [['  Live Radio ', 'Live Radio'], ['', '  Live Radio'], ['   ', ''], ['Radio 2', ' Radio 2 ']]) {
    const c = baseConfig(); c.LibraryName = saved;
    const r = await open(c);
    r.$('LibraryName').value = typed;
    await r.save();
    check(r.env.saved && r.$('saveNote').textContent === '', 'R2b fix: saved ' + JSON.stringify(saved) + ', typed ' + JSON.stringify(typed) + ': no question');
  }

  // A failed save reads Health for the server's reason; a rename question asked meanwhile stays when that answer arrives.
  const t = await open(null, env => { env.health.ConfigurationSaveRefusal = 'the settings file could not be kept'; });
  t.env.healthHold = true;
  t.env.saveFails = 500;
  await t.save();
  check(t.$('saveNote').textContent.startsWith('✕ Not saved') && t.env.healthHeld.length === 1, 'a failed save waits for Health');
  t.$('LibraryName').value = 'New Radio';
  await t.save();
  check(t.$('saveNote').textContent.startsWith('Renaming makes a new tile.'), 'the rename question replaces the note');
  t.env.healthHeld.shift()(); await settle();
  check(t.$('saveNote').textContent.startsWith('Renaming makes a new tile.') && t.$('saveNoteNear').textContent.startsWith('Renaming'), 'R2b fix: the late Health answer leaves the question alone');
});

(async () => {
  for (const [name, fn] of scenarios) {
    try { await fn(); } catch (e) { failures++; console.log('FAIL ' + name + ' threw: ' + (e && e.stack || e)); }
  }
  console.log(`${passes} passed, ${failures} failed`);
  process.exitCode = failures ? 1 : 0;
})();
