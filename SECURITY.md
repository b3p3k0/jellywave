# Security policy

## Reporting a vulnerability

Report it privately through GitHub's private vulnerability reporting on
[b3p3k0/jellywave](https://github.com/b3p3k0/jellywave): open the repository's **Security** tab and
choose **Report a vulnerability**. GitHub's guide to the form is
[here](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability).

Please do not open a public issue for a vulnerability. There is no security email address; the
GitHub form is the only private channel.

A useful report names the plugin version, the Jellyfin version, whether File Transformation is
installed, the delivery mode (Relay or Direct), and the steps or request that show the problem.

## Supported versions

Only the latest release gets fixes. Older versions are not patched; update through the plugin
repository in the Jellyfin dashboard.

## Scope

In scope, meaning code in this repository:

- The plugin's HTTP endpoints under `/Jellywave` on the Jellyfin server: the station routes used by
  the station page (feed, requests, likes, lyrics, schedule, DJ portrait), the admin routes used by
  the configuration page, and the two anonymous assets `ui.js` and `ui.css`.
- The plugin's outbound calls to the configured SUB/WAVE station: URL handling, redirect refusal,
  body and image limits.
- The script and stylesheet the plugin injects into jellyfin-web through File Transformation.
- The access grant (the admin button "Add the station to every user now") and what it writes to user policies.
- The release pipeline in `.github/workflows/` and the published `manifest.json`.

Out of scope, report these upstream:

- Jellyfin itself (server, jellyfin-web, the native clients):
  [jellyfin/jellyfin security policy](https://github.com/jellyfin/jellyfin/security).
- SUB/WAVE itself: [perminder-klair/subwave](https://github.com/perminder-klair/subwave).
- The File Transformation plugin:
  [IAmParadox27/jellyfin-plugin-file-transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation).

A risk the threat model lists as accepted is known and documented; a report that shows it is worse
than described there is still welcome.

## Threat model

[`docs/THREAT-MODEL.md`](docs/THREAT-MODEL.md) lists what the plugin protects, where it draws its
trust boundaries, the controls in place, and the risks accepted on purpose.
