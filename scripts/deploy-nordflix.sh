#!/usr/bin/env bash
# Install a staged Jellywave build into a native (systemd) Jellyfin on this host.
# Run it ON the Jellyfin host, next to the staged folder it installs:
#
#   bash deploy-nordflix.sh              # install dist folder found beside this script
#   bash deploy-nordflix.sh --rollback   # restore the most recent backup
#
# Install, in order: find the running Jellyfin and its plugin root; show the plan;
# ask; stop Jellyfin; tar any existing Jellywave_* folder to ~ as a backup; swap in
# the staged folder; chown to the jellyfin user; start; tail the log for the plugin.
# Nothing outside <plugins>/Jellywave_* is touched. Sudo is used for the service and
# the plugin folder only.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
PLUGROOT="${PLUGROOT:-/var/lib/jellyfin/plugins}"
LOGDIR="${LOGDIR:-/var/log/jellyfin}"
BACKUP_PREFIX="$HOME/jellywave-plugin-backup"
SERVICE="jellyfin"
STARTUP_WAIT_SECONDS=8   # time for Jellyfin to load plugins before we read the log

say() { printf '\n== %s\n' "$*"; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

systemctl is-active --quiet "$SERVICE" || die "systemd unit '$SERVICE' is not active; this script only handles native installs"
[ -d "$PLUGROOT" ] || die "plugin root '$PLUGROOT' not found (override with PLUGROOT=...)"
OWNER="$(stat -c %U "$PLUGROOT")"

if [ "${1:-}" = "--rollback" ]; then
    LATEST="$(ls -1t "$BACKUP_PREFIX"-*.tar.gz 2>/dev/null | head -1 || true)"
    [ -n "$LATEST" ] || die "no backup matching $BACKUP_PREFIX-*.tar.gz"
    say "Rolling back from $LATEST"
    read -rp "Stop $SERVICE and restore? [y/N] " a; [ "$a" = y ] || [ "$a" = Y ] || die "aborted"
    sudo systemctl stop "$SERVICE"
    sudo rm -rf "$PLUGROOT"/Jellywave_*
    sudo tar -xzf "$LATEST" -C "$PLUGROOT"
    sudo systemctl start "$SERVICE"
    say "Rollback complete; $SERVICE restarted"
    exit 0
fi

STAGE="$(ls -d "$HERE"/Jellywave_*/ 2>/dev/null | sort -V | tail -1 || true)"   # newest version wins
[ -n "$STAGE" ] || die "no Jellywave_<version>/ folder next to this script ($HERE)"
STAGE="${STAGE%/}"
[ -f "$STAGE/Jellyfin.Plugin.Jellywave.dll" ] && [ -f "$STAGE/meta.json" ] || die "$STAGE is missing the dll or meta.json"
NEWDIR="$PLUGROOT/$(basename "$STAGE")"
EXISTING="$(ls -d "$PLUGROOT"/Jellywave_*/ 2>/dev/null || true)"

say "Plan"
echo "  service        : $SERVICE (active)"
echo "  plugin root    : $PLUGROOT (owner $OWNER)"
echo "  staged build   : $STAGE"
echo "  install to     : $NEWDIR"
echo "  existing       : ${EXISTING:-none}"
echo "  backup to      : $BACKUP_PREFIX-<timestamp>.tar.gz (only if an existing folder is found)"
echo
read -rp "Stop $SERVICE, back up, install, restart? [y/N] " a; [ "$a" = y ] || [ "$a" = Y ] || die "aborted"

say "Stopping $SERVICE"
sudo systemctl stop "$SERVICE"

if [ -n "$EXISTING" ]; then
    BACKUP="$BACKUP_PREFIX-$(date +%Y%m%d-%H%M%S).tar.gz"
    say "Backing up existing Jellywave_* to $BACKUP"
    (cd "$PLUGROOT" && sudo tar -czf "$BACKUP" Jellywave_*)
    sudo chown "$(id -u):$(id -g)" "$BACKUP"
    sudo rm -rf "$PLUGROOT"/Jellywave_*
fi

say "Installing $NEWDIR"
sudo cp -r "$STAGE" "$NEWDIR"
sudo chown -R "$OWNER:$OWNER" "$NEWDIR"
sudo chmod 755 "$NEWDIR"; sudo find "$NEWDIR" -type f -exec chmod 644 {} +

say "Starting $SERVICE"
sudo systemctl start "$SERVICE"
sleep "$STARTUP_WAIT_SECONDS"

say "Log lines mentioning Jellywave"
# Jellyfin's default logging writes log_<date>.log; the Debian package writes jellyfin<date>.log.
LOG="$(ls -t "$LOGDIR"/log_*.log "$LOGDIR"/jellyfin*.log 2>/dev/null | head -1 || true)"
if [ -n "$LOG" ] && [ -r "$LOG" ]; then
    grep -i 'jellywave' "$LOG" | tail -20 || echo "  (none yet; try: grep -i jellywave $LOG)"
else
    echo "  no readable log in $LOGDIR; try: sudo journalctl -u $SERVICE --since '1 min ago' | grep -i jellywave"
fi
say "Done. Dashboard > Plugins should list Jellywave; set the station base URL there, then refresh My Media."
