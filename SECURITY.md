# Security policy

## Reporting a vulnerability

Report it privately through GitHub's private vulnerability reporting on
[b3p3k0/jellywave](https://github.com/b3p3k0/jellywave): open the repository's **Security** tab and
choose **Report a vulnerability**. GitHub's guide to the form is
[here](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability).

Please do not open a public issue for a vulnerability. There is no security email address; the
GitHub form is the only private channel.

A useful report names the plugin version, the Jellyfin version, whether File Transformation is
installed, the kind of station involved (SUB/WAVE or plain stream; before 0.5.0.0 also its delivery
mode, Relay or Direct), and the steps or request that show the problem. Leave real stream URLs and
host names out of the report, or replace them with placeholders.

## Supported versions

Only the latest release gets fixes. Older versions are not patched; update through the plugin
repository in the Jellyfin dashboard.

## Scope

In scope, meaning code in this repository:

- The plugin's HTTP endpoints under `/Jellywave` on the Jellyfin server: the station routes used by
  the station page (station list, feed, requests, likes, lyrics, schedule, DJ portrait), how they
  pick a station, the admin routes used by the configuration page (including the add-station check,
  `POST /Jellywave/Probe`), and the two anonymous assets `ui.js` and `ui.css`.
- The plugin's outbound calls: to SUB/WAVE stations, for plain-stream logos, and the add-station
  check. URL handling, redirect refusal, body and image limits.
- The stream URLs the plugin hands to Jellyfin as media sources (what reaches ffmpeg or the client).
- The script and stylesheet the plugin injects into jellyfin-web through File Transformation, and the
  configuration page's script.
- User policies: what the admin buttons "Add the tile to every user now" and "Give them access to the renamed tile"
  write, and the rename bridge, which keeps blocks on earlier tile names without writing anything.
- The configuration migration from 0.4.x.
- The release pipeline in `.github/workflows/` and the published `manifest.json`.

Out of scope, report these upstream:

- Jellyfin itself (server, jellyfin-web, the native clients):
  [jellyfin/jellyfin security policy](https://github.com/jellyfin/jellyfin/security).
- SUB/WAVE itself: [perminder-klair/subwave](https://github.com/perminder-klair/subwave).
- The radio streams you add, and the servers that host them.
- The File Transformation plugin:
  [IAmParadox27/jellyfin-plugin-file-transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation).

A risk the threat model lists as accepted is known and documented; a report that shows it is worse
than described there is still welcome.

## Threat model

[`docs/THREAT-MODEL.md`](docs/THREAT-MODEL.md) lists what the plugin protects, where it draws its
trust boundaries, the controls in place, and the risks accepted on purpose.
