#!/usr/bin/env bash
# uninstall.sh — remove SchoolBell service + binaries. State/config preserved by
# default (school data survives an uninstall/reinstall cycle); --purge-data to remove
# /var/lib/schoolbell and /etc/schoolbell. Run as root.
set -u
PURGE=0
[ "${1:-}" = "--purge-data" ] && PURGE=1
PREFIX=${SB_PREFIX:-/opt/schoolbell}
ETC=${SB_ETC:-/etc/schoolbell}
VAR=${SB_VAR:-/var/lib/schoolbell}
LOG=${SB_LOG:-/var/log/schoolbell}
if [ -f "$PREFIX/server.pid" ]; then kill "$(cat "$PREFIX/server.pid")" 2>/dev/null || true; fi   # sandbox-mode instance
if [ "$(id -u)" != 0 ] && [ -z "$SB_PREFIX" ]; then echo "run as root" >&2; exit 1; fi

systemctl stop schoolbell schoolbell-caddy 2>/dev/null || true
systemctl disable schoolbell schoolbell-caddy 2>/dev/null || true
rm -f /etc/systemd/system/schoolbell.service /etc/systemd/system/schoolbell-caddy.service
systemctl daemon-reload 2>/dev/null || true
rm -rf "$PREFIX"

if [ "$PURGE" = 1 ]; then
  rm -rf "$VAR" "$ETC" "$LOG"
  echo "removed service, binaries, and all data"
else
  echo "removed service and binaries; /var/lib/schoolbell and /etc/schoolbell preserved"
fi
