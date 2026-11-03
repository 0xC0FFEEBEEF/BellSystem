#!/usr/bin/env bash
# install.sh — SchoolBell Ubuntu 24.04 server installer (repeatable, idempotent).
# Layout:   binaries /opt/schoolbell   config /etc/schoolbell   state /var/lib/schoolbell
#           logs in journald; data (db, audio, keys) /var/lib/schoolbell
# Upgrades preserve /etc/schoolbell and /var/lib/schoolbell. Run as root:
#   sudo bash install/server/install.sh [--public-host NAME] [--no-caddy] [--server-name NAME]
set -u
PREFIX=/opt/schoolbell
ETC=/etc/schoolbell
VAR=/var/lib/schoolbell
LOG=/var/log/schoolbell
PUBLIC_HOST=""
WITH_CADDY=1
SERVER_NAME="SchoolBell"
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(dirname "$HERE")"
BUNDLE="$REPO/dist/publish/server"

while [ $# -gt 0 ]; do
  case "$1" in
    --public-host) PUBLIC_HOST=$2; shift 2;;
    --no-caddy) WITH_CADDY=0; shift;;
    --server-name) SERVER_NAME=$2; shift 2;;
    *) echo "unknown option: $1" >&2; exit 2;;
  esac
done

if [ "$(id -u)" != 0 ]; then echo "run as root (sudo bash install/server/install.sh)" >&2; exit 1; fi
if [ ! -x "$BUNDLE/SchoolBellServer" ]; then
  echo "bundle not found at $BUNDLE — run build/publish.sh first" >&2; exit 1
fi
if ! command -v systemctl >/dev/null; then echo "systemd required (Ubuntu 24.04)" >&2; exit 1; fi

echo "== creating user and directories"
id -u schoolbell >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin schoolbell
install -d -m 0755 "$PREFIX" "$PREFIX/server"
install -d -m 0750 -o schoolbell -g schoolbell "$VAR" "$LOG"
install -d -m 0755 "$ETC"

echo "== installing binaries (state and config preserved)"
install -m 0755 "$BUNDLE/SchoolBellServer" "$PREFIX/server/SchoolBellServer.new"
if [ -f "$PREFIX/server/SchoolBellServer" ]; then
  # keep a one-back level for documented rollback
  cp -f "$PREFIX/server/SchoolBellServer" "$PREFIX/server/SchoolBellServer.prev" 2>/dev/null || true
fi
mv -f "$PREFIX/server/SchoolBellServer.new" "$PREFIX/server/SchoolBellServer"
cp -rf "$BUNDLE/." "$PREFIX/server/"

if [ ! -f "$ETC/env" ]; then
  cat > "$ETC/env" <<EOF
# SchoolBell server environment (edit then: systemctl restart schoolbell)
SchoolBell__Urls=http://127.0.0.1:5001
SchoolBell__DataDir=$VAR
SchoolBell__ServerName=$SERVER_NAME
# SchoolBell__SchoolTz=America/Chicago
# SchoolBell__MaxConnections=1000
# SchoolBell__OutboxCap=500
EOF
  echo "   wrote $ETC/env (existing file left untouched on upgrades)"
fi

echo "== installing systemd units"
install -m 0644 "$REPO/infra/systemd/schoolbell.service" /etc/systemd/system/schoolbell.service

if [ "$WITH_CADDY" = 1 ]; then
  if ! command -v caddy >/dev/null; then
    echo "== installing caddy via apt"
    if apt-get install -y caddy >/dev/null 2>&1; then
      :
    else
      echo "WARNING: caddy not installed; running HTTP-only. Install caddy manually and re-run."
      WITH_CADDY=0
    fi
  fi
  if [ "$WITH_CADDY" = 1 ]; then
    HOST=${PUBLIC_HOST:-$(hostname -I 2>/dev/null | awk '{print $1}')}
    if [ ! -f "$ETC/Caddyfile" ]; then
      sed "s/HOSTNAME/$HOST/" "$REPO/infra/caddy/Caddyfile.example" > "$ETC/Caddyfile"
      echo "   wrote $ETC/Caddyfile for https://$HOST (existing file untouched on upgrades)"
    fi
    install -m 0644 "$REPO/infra/systemd/schoolbell-caddy.service" /etc/systemd/system/schoolbell-caddy.service
  fi
fi

systemctl daemon-reload
systemctl enable schoolbell >/dev/null
systemctl restart schoolbell
[ "$WITH_CADDY" = 1 ] && { systemctl enable schoolbell-caddy >/dev/null; systemctl restart schoolbell-caddy; }

echo "== health check"
ok=0
for _ in 1 2 3 4 5; do
  if curl -sf http://127.0.0.1:5001/ >/dev/null; then ok=1; break; fi
  sleep 2
done
if [ "$ok" != 1 ]; then
  echo "ERROR: server not answering on 127.0.0.1:5001 — journalctl -u schoolbell follows:" >&2
  journalctl -u schoolbell -n 25 --no-pager >&2
  exit 1
fi

cat <<EOF

== SchoolBell installed.
   Service: systemctl status schoolbell   (logs: journalctl -u schoolbell -f)
$([ "$WITH_CADDY" = 1 ] && echo "   Office URL: https://${HOST}/  (Caddy local CA root: /var/lib/caddy/.local/share/caddy/pki/authorities/local/root.crt)")
   First-run admin bootstrap: run  journalctl -u schoolbell | grep 'SETUP CODE'
   then open the office URL, complete setup, set the school timezone in /admin,
   create enrollment tokens, and build schedules.
   Windows clients: SchoolBellSetup.exe --server <host> --enrollment-file <token file> --ca-file <CA root>
EOF
