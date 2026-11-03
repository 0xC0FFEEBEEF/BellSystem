#!/usr/bin/env bash
# Remove the SchoolBell server. Data and configuration are KEPT unless --purge-data is given
# (an uninstall/reinstall cycle keeps devices, schedules and history).
#   sudo bash /opt/schoolbell/current/uninstall.sh [--purge-data]
set -u
PURGE=0; [ "${1:-}" = "--purge-data" ] && PURGE=1
[ "$(id -u)" = 0 ] || { echo "run as root" >&2; exit 1; }
systemctl disable --now schoolbell-update.path schoolbell schoolbell-caddy 2>/dev/null || true
rm -f /etc/systemd/system/schoolbell.service /etc/systemd/system/schoolbell-caddy.service \
      /etc/systemd/system/schoolbell-update.service /etc/systemd/system/schoolbell-update.path
systemctl daemon-reload
rm -f /usr/local/sbin/schoolbell-update
rm -rf /opt/schoolbell
if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "443/tcp"; then
  echo "note: the ufw rule for 443/tcp was left in place (remove with: ufw delete allow 443/tcp)"
fi
if [ "$PURGE" = 1 ]; then
  rm -rf /var/lib/schoolbell /etc/schoolbell
  userdel schoolbell 2>/dev/null || true; userdel schoolbell-proxy 2>/dev/null || true
  echo "SchoolBell removed, including all data and configuration."
else
  echo "SchoolBell removed. Data kept in /var/lib/schoolbell and settings in /etc/schoolbell (use --purge-data to delete)."
fi
