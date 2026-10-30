# Jellywave

Internet radio for Jellyfin 12.4. Jellywave adds a "Live Radio" tile to My Media, beside your
libraries and Live TV. See [Tested on](#tested-on) for the clients tried so far. Each station you add
is one item in the tile. Open the tile, pick a station, press play. Jellywave is a Jellyfin channel
plugin: the tile is its one channel.

- **Plain streams** (any MP3, AAC or HLS radio stream) get a name, a logo and playback. On the
  optional station page they also show the title the stream sends, when it sends one.
- **[SUB/WAVE](https://github.com/perminder-klair/subwave) stations** get every feature: the current
  track, DJ and show on the item, a cover per track, and on the optional station page likes, lyrics,
  today's schedule and listener requests.

## Supported versions

**Jellywave 0.5.0.0 requires Jellyfin 12.4.** It is built against 12.4 and cannot load on 12.3 or
older.

Versions 0.1.0.0 to 0.4.2.0 are withdrawn with the 0.5.0.0 release: they leave the catalog and the
releases page, and they get no fixes. A server that already runs one keeps running it.

See also [Supported streams](#supported-streams) and [Tested on](#tested-on).

## Install

1. Dashboard → Plugins → Repositories → add
   `https://raw.githubusercontent.com/b3p3k0/jellywave/main/manifest.json`.
2. Install **Jellywave** from the catalog.
3. Restart Jellyfin.

The plugin page is at Dashboard → Plugins → Jellywave. Coming from 0.4.x? Read
[Upgrading from 0.4.x](#upgrading-from-04x) first.

## Play your first station

A fresh install comes with five stations, so the tile plays at once: the SUB/WAVE demo station, KEXP
(Seattle), WRUW 91.1 FM and WJCU 88.7 FM (Cleveland college radio) and XCSB (Cleveland).

1. Open **Live Radio** in My Media.
2. Pick a station.
3. Press play.

No tile? See [Give other users access](#give-other-users-access). No sound? See
[Troubleshooting](#troubleshooting).

The starters are ordinary stations. Edit them, untick "On", or remove them with ✕ in the station list
and press Save. A removed starter never comes back, not even when you remove all of them. Only a fresh
install gets them (no Jellywave settings file yet). An upgrade, a 0.4.x install and a reinstall keep
their own list.

With the starters in the list, the server contacts their hosts: each logo once after every restart,
whether or not anyone listens, and the SUB/WAVE demo only while someone plays it or has its station
page open. Remove the ones you do not want.

## Add a station

Dashboard → Plugins → Jellywave → **Stations** → **+ Add station**.

1. **Paste the stream URL**, as the Jellyfin server reaches it. It can be an MP3, AAC or HLS stream, a
   `.pls` or `.m3u` playlist, or a SUB/WAVE station's own address (its web player).
2. **Press Check.** The server reads the address's headers, and a playlist's text or a SUB/WAVE
   station's status when there is one. It never reads the audio. It fills in the format, and the name
   when it finds one. If Check offers **Use it**, press it, then press Check again.
3. **Look at the name** and change it if you want. Logo, genre, notes and website are optional
   ([Station fields](#station-fields)).
4. **Press Add to list.**
5. **Press Save.** Nothing changes on the server until Save.

**Never put secrets in a stream URL.** Any signed-in user can read it. Check warns when the query has
a parameter named like a secret ("Every signed-in user can read this address, even users the tile is
hidden from. Leave secrets out of it."). It is a warning only: Add still works. Check does not see a
secret in the path.

A station that is down right now can still be added: press Add to list twice without a successful
Check. It is saved as a plain stream with the format under Advanced.

## Give other users access

> **Warning.** Tile access only hides the tile. On Jellyfin 12.4, any signed-in user can still read
> every station's address and play it through Jellyfin's own API. Anyone who has a station's item id
> can play it without signing in. Jellyfin works this way for all media, not only Jellywave. Don't add
> private streams, or addresses that contain passwords or keys.

The plugin page shows the same warning under Stations. The
[threat model](docs/THREAT-MODEL.md#accepted-risks) has the details ("Tile access hides the tile but
does not lock it").

**All stations or none.** Jellyfin decides who sees a channel, not who sees each item in it. Everyone
who can see the tile sees every station in it. You cannot hide one station from one user.

**Who sees the tile.** Jellyfin's **Channel access** setting on each user decides (Dashboard → Users →
the user → Access, where Jellyfin calls the tile a channel). Users whose policy allows all channels
see the tile, and so do users whose channel list includes it. Users added in the dashboard, from a
template or by a provisioning tool often have neither: the dashboard's Add User page leaves **Enable
access to all channels** unticked.

**Add the tile to every user now** (under "Who can see it" on the plugin page) adds the tile once to
every user who cannot see it right now, and shows the counts (granted, already visible, blocked by a
block list). It changes nothing else in a policy. In the rare case that someone saves the same user at
that moment, that change can be lost ([threat model](docs/THREAT-MODEL.md#accepted-risks)). It never
removes the tile from anyone. The line above the button says how many active users can see the tile ("4 of 6 active users can see the tile.");
disabled users are left out.

- The button runs only when you press it. New users do not get the tile automatically. Tick **Enable
  access to all channels** or the tile on the Add User form, include the tile in the provisioning
  tool's template, or press the button again.
- Each press adds the tile to everyone who cannot see it, including anyone you unticked earlier. After
  pressing, untick those users again.
- A press skips users whose block list names the tile or an earlier tile name. Jellyfin 12.4 offers
  no way to manage block lists: jellyfin-web has no screen for them, and the policy API ignores the
  field. An existing block stays until the database is edited.

**Users also need "Allow audio playback that requires transcoding"** (Dashboard → Users → the user →
Profile → Media playback, on by default). Every station is relayed through Jellyfin. With this setting
off, Jellyfin offers the user nothing playable, so their clients cannot play stations. A yellow line
under the button names such users (at most ten, then "and N more"). The page changes no setting.

**These playback settings are not locks.** "Allow audio playback that requires transcoding" changes
what clients are offered. On a direct `/Audio/{id}/stream` request it only forces stream copy. "Allow
media playback" only hides Play in clients; the server does not check it.

## Optional: the station page

The station page is a live header above the station grid, on the tile's page, in the web client and
the Android app (which wraps it). The plugin page calls it the "Live panel". Everything else works
without it.

It shows the station that is playing, follows playback, and replaces Pause with Stop. SUB/WAVE
stations add likes, lyrics, today's schedule and listener requests. Plain streams add a "Now:" line
with the title the stream sends. [Station page details](#station-page-details) has the full list.

Jellyfin's web client has no hook for plugin pages that ordinary users can reach, so the page is
injected by the third-party
[File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin,
3.0.x (tested with 3.0.3.0 on Jellyfin 12.4). Its catalog offers 3.0.x for Jellyfin 12.0–12.4.

1. Dashboard → Plugins → Repositories → add
   `https://www.iamparadox.dev/jellyfin/plugins/manifest.json`.
2. Install **File Transformation**.
3. Restart Jellyfin.
4. Open the plugin page. "Live panel" should read "✓ Active".
5. Android app: force-close it and open it again. The app keeps the old web page until it is
   restarted. Browsers pick up the script within a day, or at once on a reload.

The line reads a grey "Not installed" (with these steps) without File Transformation, and a red "✕ Not
active" when its registration failed (see the server log).

## More

- [Upgrading from 0.4.x](#upgrading-from-04x)
- [Troubleshooting](#troubleshooting)
- [Advanced settings](#advanced-settings): tile name, now playing, the settings file
- [Security](#security)

Reference: [Supported streams](#supported-streams) · [Station fields](#station-fields) ·
[The station list](#the-station-list) · [Now playing, covers and logos](#now-playing-covers-and-logos) ·
[Station page details](#station-page-details) · [Limits](#limits) ·
[Known limitations](#known-limitations) · [Tested on](#tested-on) ·
[Build from source](#build-from-source)

[`docs/DESIGN.md`](docs/DESIGN.md) explains how the plugin works. The plan and lessons for contributors
live in [`docs/dev/`](docs/dev/PLAN.md).

---

## Upgrading from 0.4.x

0.5.0.0 replaces the single SUB/WAVE tile of 0.4.x with the Live Radio tile. Do these steps in order,
right after the upgrade.

1. **Install the update and restart Jellyfin.** Your station becomes the first entry in the station
   list, with the same address and kind SUB/WAVE. It keeps now playing, covers and the station page. A
   station set to Direct now plays through Jellyfin, like every station. An install that never had a
   station URL gets an empty list.
2. **Press "Give them access to the renamed tile"** when the plugin page offers it. The tile is now
   called "Live Radio", and Jellyfin derives a channel's id from its name, so this is a new channel.
   Users with **Enable access to all channels** see it at once. Users who had the old tile ticked by
   name see nothing until you press. The press copies each user's ticks and block list entries to the
   new tile, once. It only adds: it never removes access and never ticks "Enable access to all
   channels". For users who had neither, use "Add the tile to every user now".
3. **Run "Refresh Channels"** (Dashboard → Scheduled Tasks, under "Internet Channels") to remove the
   old tile. The task also runs by itself once a day.
4. **Android app:** force-close it and open it again.

The moved station gets a new random id, and any station still on a publicly known id gets one once.
Either way it becomes a new item, so its favourites and play state are reset. For a live stream
there is little to lose.

**Downgrade.** 0.4.2.0 is withdrawn with the 0.5.0.0 release, so you can go back only from a zip you
kept. It then finds the station as it was at the upgrade, in Relay: 0.5.0.0 leaves the old settings in
place but removes Delivery and the client stream URL.

## Troubleshooting

**A user has no tile.** Check their Channel access, or press "Add the tile to every user now" (see
[Give other users access](#give-other-users-access)). A user with a block list entry stays hidden.
Administrators get no exception.

**The tile shows, but a user cannot play.** "Allow audio playback that requires transcoding" is
probably off for them. The plugin page names such users.

**A red notice: "Another plugin's tile is also called …".** Two channel plugins with the same tile name
share one id, so Jellywave stops using it: no item is updated, no access is granted, and the station
page answers 404. Change the name under Advanced → Tile name.

**"Tile renamed: N users could see …".** Press "Give them access to the renamed tile". See
[Renaming the tile](#renaming-the-tile).

**The old tile is still listed.** Run "Refresh Channels" (Dashboard → Scheduled Tasks, under "Internet
Channels"). It also runs once a day by itself. Nothing updates the old tile any more.

**A station row says "✕ left out of the tile:".** The server refuses that station, for example for a
duplicate id or an address the URL rules do not allow. The reason follows the text. The other stations
stay in the tile.

**A plain stream says "↪ moved".** The address redirects. Jellyfin follows it and plays, but the title
reader does not. Press Edit → Check → Use it → Check again → Apply to list, then Save.

**A logo or cover does not show.** The image must be one still JPEG, PNG, GIF or WebP, at most 4 MB and
4096 px. Animated images are refused, because a small file can hold hundreds of frames for Jellyfin's
image processor to decode. So is a JPEG with more than 100 scans. A refused cover shows the Jellywave
logo. A refused logo reads "⚠ logo failed; retried within 30 min" in the status column.

**An HLS station stalls.** Use the station's MP3 or AAC mount if it has one. ffmpeg opens every variant
a master playlist lists, so dead or mixed variants can stall playback.

**Check says it cannot read a stream.** It may be Shoutcast v1 (`ICY 200 OK`). Try the station's
Shoutcast v2 or Icecast address.

**The station page does not show.** It shows only while a station plays, on the tile's page. Look at
"Live panel" on the plugin page. If it reads "✓ Active",
reload the web client. In the Android app, force-close it; if that does not help, use Android Settings
→ Apps → Jellyfin → Storage → Clear cache (not Clear data), which keeps your sign-in. Other apps have no
station page. A user who cannot see the tile gets no header. The page relies on jellyfin-web internals, so after a jellyfin-web
update it may stop working. It is meant to do nothing rather than break the client.

**The station page says "Too many requests." or "The server is busy."** It says when to try again. See
[Limits](#limits).

**"Not saved" beside Save.** The reason shows in red and the page keeps your changes. The settings file
must be a regular file in a folder Jellyfin can write to. A single-file bind mount makes every save
fail. See [Settings file](#settings-file).

**"Jellywave could not read its settings file".** See [Settings file](#settings-file).

**Playback stops after 6 hours.** Press play again. See [Known limitations](#known-limitations).

## Advanced settings

The plugin page starts with the station list. Notices about the tile show at the top only while they
apply. Below the stations are "Who can see it" and "Live panel". **Advanced** is closed by default,
and its defaults suit most servers.

| Setting | Default | What it does |
|---|---|---|
| Tile name | Live Radio | The name in My Media. See below before you change it |
| Show what's playing | on | Now playing for SUB/WAVE stations, and reading titles from plain streams. Off, the server reads no stream and the station page shows no "Now:" line |
| Show the current track's cover art on the item | on | Uses each track's cover as the station's picture |
| Update every (seconds) | 20 (5 to 300) | How often the server asks each SUB/WAVE station that someone needs |

**"Now playing updates"** under Advanced says whether the updates run and how long the last one took:
green up to half the update interval, yellow ("running slow") above half, red ("too slow") above the
whole interval, when stations update less often than set.

### Pick a unique tile name

Two channel plugins with the same tile name share one id, and a generic name like "Live Radio" makes
that likelier. See the red notice under [Troubleshooting](#troubleshooting). Whether names that differ
only in case collide follows the server's item-id setting.

### Renaming the tile

Renaming makes a new channel, so Save first asks: 'Renaming makes a new tile. Users who had "…" ticked
by name will not see "…" until you press Give them access to the renamed tile. Rename?' After a rename:

1. Press "Give them access to the renamed tile" when the plugin page offers it. A second press
   reports 0, and the notice goes away once nobody is waiting.
2. Run "Refresh Channels" to drop the old tile.

The stations become new items in the new tile, so favourites and play state are reset. The old name
is remembered, and its blocks keep applying.

### Settings file

Each save writes a new file in the same folder and swaps it in as one step, so the file is always the
old or the new complete one. The file before it is kept beside it as
`Jellyfin.Plugin.Jellywave.xml.previous`.

A settings file that cannot be read is never written over:

- It is copied aside first, as `….unreadable-<UTC time>.xml` in the plugin configurations folder. The
  plugin runs on defaults, and a yellow notice names the copy. The next Save writes a new settings
  file. The copy stays.
- If the copy fails, a red notice says so and every Save is refused, so the file is never overwritten.
  Check that Jellyfin can read the settings file and write to its folder. No restart is needed.

## Security

Report vulnerabilities privately, as described in [`SECURITY.md`](SECURITY.md). What the plugin trusts
and which risks are accepted is in [`docs/THREAT-MODEL.md`](docs/THREAT-MODEL.md). The two facts every
admin needs are the warning under [Give other users access](#give-other-users-access) and the rule to
keep secrets out of stream URLs ([Supported streams](#supported-streams)).

## Supported streams

| Stream | Supported | Notes |
|---|---|---|
| SUB/WAVE station | yes | Every feature. One or more stations |
| MP3, AAC (Icecast, Shoutcast v2, other HTTP streams) | yes | |
| HLS (`.m3u8`) | yes | Prefer the station's MP3 or AAC mount when it has one (see [Troubleshooting](#troubleshooting)). A single variant's playlist also works |
| `.pls`, `.m3u` | through Check | Check resolves them to the stream they name. Pasted as the stream URL itself and saved, they are refused |
| DASH (`.mpd`) | no | Refused |
| Shoutcast v1 (`ICY 200 OK`) | no | Check cannot read it and says so |

- **Every station plays through Jellyfin.** Jellyfin's ffmpeg fetches the stream and serves it to each
  listener, so clients only need to reach Jellyfin, never the station. The station sees the Jellyfin
  server as the listener.
- Stream URLs must be `http://` or `https://`, with no `user:password@` and no `#fragment`. A `?query`
  is allowed.
- **Never put secrets in a stream URL** (no tokens, keys or passwords, in the path or the query). Every
  stream URL, query included, is part of the item's playback info, which Jellyfin returns to signed-in
  users and to any API key. Relaying the audio does not hide it. The plugin's own routes never return
  it, but Jellyfin's do.
- A SUB/WAVE station's address is its base URL plus its stream path, under stricter rules: no query in
  the base URL, and the stream must stay on the same host and port.

## Station fields

**What Check shows:**

- the audio format (codec, bitrate, sample rate), which fills the format fields;
- **SUB/WAVE detected**, with the station's name and description filled in, or "Radio stream.", with
  the name taken from the stream's `icy-name` header (and the genre, notes and website from `icy-genre`,
  `icy-description` and `icy-url`, when the stream sends them and the fields are empty);
- for a playlist, "Playlist read → …" and the stream it names (the first http or https entry). That
  stream is what gets saved. A playlist that points to another playlist is refused;
- for a redirect, "The address redirected to …; use that address" and a **Use it** button. Check never
  follows a redirect itself: press Use it, then Check again;
- for HLS, a warning that the codec is assumed to be AAC, or why the address cannot be used.

**The fields:**

- **Name.** Filled from the station. The item is called `<name> — Live`. Left blank, a plain stream is
  named after its stream URL's host (`stream.example.org — Live`) and a SUB/WAVE station "SUB/WAVE". The
  dialog warns about a blank name.
- **Description** (SUB/WAVE stations, optional). Filled from the station. The item shows it while nobody
  listens.
- **Logo URL** (plain streams, optional). One still JPEG, PNG, GIF or WebP of at most 4 MB and 4096 px.
  Animated images are refused (see [Troubleshooting](#troubleshooting)). The server downloads the logo.
  Blank shows the Jellywave logo. SUB/WAVE stations show their track covers.
- **Genre** (plain streams, optional). At most 60 characters, shown in the station page header.
- **Notes** (any station, optional). Plain text, at most 280 characters, shown as one line in the
  station page header. A plain stream's notes are also its item's description, so every Jellyfin app
  shows them (there without `<` and `>`, since Jellyfin's apps read a description as formatted text).
- **Website** (plain streams, optional). An `http://` or `https://` link in the header. It opens in a
  new tab, and the server never visits it. A website that is not `http://` or `https://`, contains
  `user:password@` or is longer than 2048 characters is left out of the header. A host with non-Latin
  letters shows in its `xn--…` form, so a look-alike address cannot pass for another.
- **Advanced** holds the format fields (codec, container, bitrate, sample rate, channels), which Check
  fills.

Hints (a URL the server will refuse, a blank name) show once you type a URL or press Check. Editing a
SUB/WAVE station so that it would become a plain stream (a Check found a plain stream, or its URL
changed without a Check) needs a second press of Apply to list, and says that now playing, covers,
likes and requests stop for it.

## The station list

Each row has ↑/↓ to reorder (the tile shows the stations in this order), an "On" box (the station is
enabled), Edit and ✕ (Remove asks in the row first). A disabled or removed station leaves the tile. The
format is not in the table; Edit → Advanced shows it.

- **Save** is beside the station list (while the list has unsaved changes) and at the bottom of the
  page. If Save fails, the reason shows in red next to it and the page keeps your changes. Closing or
  reloading the browser tab with unsaved changes makes the browser ask first. Moving to another page
  inside Jellyfin does not (jellyfin-web offers no way to stop it), so press Save before you leave.
- **Renaming a station** renames its item within one update interval. Jellyfin keeps users' favourites
  and play state on it.
- **Reload status** asks each SUB/WAVE station once and re-reads the status column. It can take a few
  seconds. A second press within 10 seconds asks nothing and shows the last results.

**The status column** shows the server's view of each saved station. Each line starts with a head:
green ✓ when all is well, yellow ⚠ or ↪ for something to watch or a status the page could not fetch
("⚠ couldn't check"), red ✕ for something that does not work. The symbol says the same without colour.
Grey has no symbol: off, disabled or normal waiting.

| Status | Station | Meaning |
|---|---|---|
| ✓ on air · 12 s ago | SUB/WAVE | Answered its last update |
| ⚠ failing: timed out (or could not connect, HTTP 503) | SUB/WAVE | The failure, with its last success |
| idle: nobody listening | SUB/WAVE | The server is not asking it, because nobody plays it or has its page open |
| ✓ answered when checked 3 s ago · idle: nobody listening | SUB/WAVE | After Reload status (or "⚠ failing: … when checked …") |
| waiting for the first update | SUB/WAVE | Normal right after a save |
| now playing is off | SUB/WAVE | "Show what's playing" is off |
| now playing updates not running yet | SUB/WAVE | Yellow: the updates have not started |
| ✓ answered 4 min ago | Plain | The stream answered, even if its title did not arrive in time |
| ↪ moved 4 min ago: Edit → Check → Use it to read titles | Plain | The address redirects. It plays, but shows no title until you save the new address |
| ⚠ no answer 2 h ago | Plain | The stream could not be reached, answered with an error, or timed out |
| ✓ in the tile | Plain | Not read since the last restart. This says the station is listed, not that it plays |
| ⚠ logo failed; retried within 30 min | Plain | Added after the state. The logo could not be used |
| disabled | Any | The "On" box is off |
| ✕ left out of the tile: … | Any | The server refuses the station, with the reason |

A plain stream's status comes only from reading its title. The server reads a stream only while someone
plays it with "Show what's playing" on, never an HLS stream, and forgets everything on a restart.
Nothing is read just for this status.

## Now playing, covers and logos

- **SUB/WAVE stations.** Every update interval the server asks each SUB/WAVE station that someone needs
  what is on air. It writes the track, DJ, show and what is up next into that station's item
  description, and the track's cover as the item's image. A track without art, or a station that does
  not answer, shows the Jellywave logo. A track without art is asked again after 30 minutes. One station
  failing never affects another.
- **Only while someone listens.** A SUB/WAVE station is asked only while a Jellyfin user who can see the
  tile plays it (paused counts) and for 30 seconds after, or while someone had its station page open in
  the last minute. **Nothing is sent to a SUB/WAVE station while nobody listens or has its page open.**
  Its item then shows the Jellywave logo and the station's own text (its Description, else its Notes,
  else the default text), never an old track. Pressing play or opening its station page asks it at
  once. Reload status on the plugin page asks each station once, at most once every 10 seconds.
- **Plain streams** show their logo (or the Jellywave logo) and their notes as the description (a fixed
  text when they have none). Their items are not polled.
- **Titles for plain streams.** Most Icecast and Shoutcast streams send the current title inside the
  stream (`StreamTitle`, usually "Artist - Title"). While a Jellyfin user plays a plain stream, the
  server reads it every 15 seconds: it opens the stream, reads up to the first title (at most about
  68 KB, at most 8 seconds) and closes it. **Each read is a short extra listener on the station**, which
  its listener count may show. Nobody playing the stream: nothing is read. The title is shown as the
  station sends it, on the station page only. It is not written to the item.
- **Streams that send no titles:** HLS streams, streams without an `icy-metaint` header (some CDNs and
  non-Icecast servers), and streams that send an empty title (many talk stations). A stream URL that
  redirects plays, but its title is not read.
- Clients show the new text and image when they reload the list or start playback. The tile in the My
  Media row always shows the Jellywave logo.

## Station page details

What users who can see the tile get, in the web client and the Android app:

- **A header above the station grid while a station plays,** on the tile's page (the tile itself, the
  top-bar entry in the default layout, or the drawer link in the legacy layout).
  - A **SUB/WAVE station**: cover, track, the station's notes, DJ with portrait and tagline, show, what
    is next, listener count, and Stop.
  - A **plain stream**: its logo, name, genre, notes, a "Now:" line with what it plays, a format line
    such as "MP3 · 128 kbps", its website, and Stop.
- **The header follows playback.** Start another station and the header switches to it. Stop and the
  header goes. Nothing playing: no header. There is no station picker: to see another station's header,
  play that station.
- **SUB/WAVE stations, per station:** a like button with that station's count, lyrics, today's schedule
  in the station's time zone, recently played tracks, and a request box. What the station turns off
  stays hidden. While a SUB/WAVE station plays, the playing bar, the item's card and the browser's media
  session follow the track every five seconds, on any page.
- **Plain streams:** the header comes from the station list (an edit shows within a minute). While a
  title shows, the playing bar and the browser's media session show it too, split into artist and title
  at the first " - ". The bar keeps following it on other pages until playback stops or moves. The
  "Now:" line reads:

  | It says | When |
  |---|---|
  | Querying upstream… | Playback just started and the server has no title yet (up to 30 seconds) |
  | the title, as the station sends it | The server read one |
  | Delayed response: is the stream up? | 30 seconds without a title, or the server cannot read the stream |
  | Not supplied by the station | The stream sends no titles; an HLS stream at once |
  | (no line) | "Show what's playing" is off on the plugin page |

- **Stop instead of Pause.** A live station has nothing to resume, and a paused stream would play from
  its buffer, behind what is on air. So a station has only start and stop. While a station is the
  current item, the playing bar shows Stop and no pause button, and a pause from anywhere (media keys,
  the browser's media controls, the lock screen or notification, the Now Playing page, a cast receiver)
  stops it after about a second. Play it again and you are back with what is on air now. Music and video
  keep Jellyfin's pause.
- **The Stop button on phones and tablets.** jellyfin-web hides its own Stop on narrow screens and on
  mobile devices, so there Jellywave adds its own to the playing bar while a station plays.
- **Limits of Stop instead of Pause:**
  - It works only where the station page's script runs. Other apps keep their pause.
  - Stop removes the playing bar, so playing again is a tap on the station.
  - Anything that pauses the player also stops the station: on Android, losing the audio to something
    else (a call, a navigation prompt, a voice assistant), even for a moment; a SyncPlay group pause (it
    stops the station for each member); and pausing a device you control remotely from the web client.

The page talks only to the Jellyfin server, never to a station, and the audio comes through Jellyfin
too, so clients never need to reach a station. This does not make a station private: see the warning
under [Give other users access](#give-other-users-access). A station counts this server as one caller,
so "liked" is shared by everyone on it.

### Requests

- **One caller to the station.** The plugin sends every request from the Jellyfin server, so the station
  sees all Jellyfin users as one caller with one set of limits: a short wait between requests, one
  pending at a time and a few per hour for everyone together. A "try again later" can be about someone
  else's request. The page says so under the box. The plugin also caps each user (see [Limits](#limits)).
  A station that turns a request down answers in the box with its own words.
- **The name is free text.** It is prefilled with the Jellyfin user name, but the listener can type
  anything. The plugin collapses its whitespace, cuts it at 40 characters and passes it on. Nothing
  checks it against the Jellyfin account.
- **The server knows who sent it.** Jellyfin keeps the real user id and the station with each accepted
  request: in the receipt that lets only that user follow the request, and in one Information log line
  (request id, user id and station id, never the name or the text). That is the accounting trail if a
  name is abused. After a Jellyfin restart the page says the station lost track of a pending request.

### Who can use the station page

Signed-in users who can see the tile in Jellyfin, parental controls included. Administrators get no
exception. Everyone else, and every API key, is refused by the plugin's own routes, and the page shows
nothing. Unlike Jellyfin's own routes, the station page's routes do check access. Signing out, or
signing in as another user or on another server, clears everything the page showed.
[`docs/DESIGN.md`](docs/DESIGN.md#station-route-authorization-card-13) has the status codes.

## Limits

The plugin limits the calls users cause to a station:

| Limit | Applies to |
|---|---|
| 60 station calls a minute (likes, lyrics, portraits, requests and their status) | each user |
| 1 song request a minute and 4 an hour, across all stations | each user |
| 10 likes a minute | each user |
| 4 calls at once per station, 16 in all, of which 4 portraits | the whole server |

Normal use stays far below these. Over a limit, the station page says "Too many requests." or "The
server is busy." and "Try again in N s." Nothing reached the station then. Text typed in the request
box stays there, and a song request is never sent again by itself. What the page asks for by itself
says "Asking again in N s." or "Checking again in N s."

A station's own lower limit still wins (see [Requests](#requests)). Counts reset when Jellyfin restarts.
Answers are cached, so like counts can lag by up to 5 seconds and request progress by up to about 8
seconds. These limits do not cover audio (see [Known limitations](#known-limitations)).

## Known limitations

- **One ffmpeg per listener.** Each listener is a separate ffmpeg on the server, with its own connection
  to the station, even when several listen to the same station. For the web client that is usually a
  re-encode to AAC at 256 kbps in HLS segments. When the station's codec already suits the client,
  Jellyfin may copy the audio instead. The plugin does not throttle audio, so on a small server count
  your likely listeners at once. Jellyfin's own per-user settings (Dashboard → Users → the user →
  Profile) act at sign-in and in what clients are offered: "Maximum number of simultaneous user
  sessions" is checked at sign-in, "Internet streaming bitrate limit (Mbps)" applies to clients outside
  the local network, and the two playback settings are under
  [Give other users access](#give-other-users-access). None of them refuses a direct
  `/Audio/{id}/stream` request.
- **Do not seek.** Jellyfin's audio HLS path has no live mode, so the plugin advertises a nominal 6-hour
  runtime to get a working playlist. The player therefore shows a timeline, and dragging it restarts the
  transcode with a seek on a live input. Play and stop are fine. After 6 hours press play again.
- **The playing bar outside the station page.** Standard clients copy the item when playback starts and
  never re-read it, so the playing bar keeps the track that was on at that moment. Only the station page
  repaints it: for SUB/WAVE stations on any page, for a plain stream once its header has shown a title
  during this playback. Roku, Android TV and other native clients do not.
- **Names and order follow within one update.** After a rename or a reorder the plugin rewrites names and
  sort names on its next update (at most one update interval, also with now playing off). A client that
  sorts the grid another way (date added, random) does not follow the station order.
- **No per-station visibility** (see [Give other users access](#give-other-users-access)).
- **No station pictures on Roku.** The Jellyfin Roku app shows no picture for stations. Its source
  suggests that it drops the picture of every audio item in a grid, so the plugin cannot fix this. The
  tile, the station list and playback work.
- **The station page relies on jellyfin-web internals** that can move between releases. It is then meant
  to do nothing rather than break the client.

## Tested on

| Plugin | Jellyfin | Client | Result | Station page | Date |
|---|---|---|---|---|---|
| 0.5.0.0 | 12.4.0 | Web client (desktop browser) | All features | Works | 2026-10-30 |
| 0.5.0.0 | 12.4.0 | Android app 2.7.3, Pixel 9 (GrapheneOS) | All features | Works | 2026-10-30 |
| 0.5.0.0 | 12.4.0 | Android app 2.7.3, Galaxy Tab S6 Lite (Android 14) | Playback, station card and Stop | Works | 2026-10-30 |
| 0.5.0.0 | 12.4.0 | Jellyfin Roku 3.1.0, Roku Express (3960X) | Tile, list and playback work. No station pictures (the app's source suggests this affects all audio items) | No | 2026-10-30 |
| 0.5.0.0 | 12.4.0 | Chromecast | Not tested on 0.5.0.0 | Not tested | — |
| — | — | macOS, iOS, Windows, Android TV | Nobody has tried these; reports welcome | — | — |

## Build from source

```bash
scripts/build.sh          # dist/Jellywave_<version>/ and .zip (manifest.json is written by the release workflow)
scripts/check-manifest.sh # GUID, version and targetAbi agree across all declarations, and targetAbi
                          # names the Jellyfin packages the plugin is compiled against
dotnet test Jellyfin.Plugin.Jellywave.Tests -c Release   # unit tests
node Jellyfin.Plugin.Jellywave.Tests/js/configPage.test.js   # configuration page script (plain node)
node Jellyfin.Plugin.Jellywave.Tests/js/stationPage.test.js  # station page script (plain node)
```

Requires the .NET 10 SDK, Node.js, `jq`, and `zip`. How releases are made is in
[`docs/DESIGN.md`](docs/DESIGN.md#packaging).

## License

MIT.
