#!/usr/bin/env bash
# uninstall.sh — remove SchoolBell service + binaries. State/config preserved by
# default (school data survives an uninstall/reinstall cycle); --purge-data to remove
# /var/lib/schoolbell and /etc/schoolbell. Run as root.
set -u
PURGE=0
[ "${1:-}" = "--purge-data" ] && PURGE=1
if [ "$(id -u)" != 0 ]; then echo "run as root" >&2; exit 1; fi

systemctl stop schoolbell schoolbell-caddy 2>/dev/null || true
systemctl disable schoolbell schoolbell-caddy 2>/dev/null || true
rm -f /etc/systemd/system/schoolbell.service /etc/systemd/system/schoolbell-caddy.service
systemctl daemon-reload
rm -rf /opt/schoolbell

if [ "$PURGE" = 1 ]; then
  rm -rf /var/lib/schoolbell /etc/schoolbell /var/log/schoolbell
  echo "removed service, binaries, and all data"
else
  echo "removed service and binaries; /var/lib/schoolbell and /etc/schoolbell preserved"
fi
