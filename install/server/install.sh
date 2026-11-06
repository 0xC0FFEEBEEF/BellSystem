#!/usr/bin/env bash
# SchoolBell server installer for Ubuntu 24.04 LTS x86-64. Run from the extracted release bundle:
#
#   sudo bash install.sh --hostname bells.school.example --timezone America/Chicago
#
# Options
#   --hostname NAME|IP     address classrooms and the office use (DNS name recommended). Repeatable.
#   --timezone IANA        school timezone (e.g. America/Chicago). Never guessed from this machine.
#   --tls-cert F --tls-key F   use the school's certificate instead of Caddy's internal CA.
#   --server-name NAME     shown in the office UI.
#   --no-firewall          do not touch ufw (default: if ufw is active, allow 443/tcp only).
#   --upgrade              non-interactive upgrade of an existing install (used by the updater).
#   --rollback             switch back to the previous release and restore its pre-upgrade database.
#
# Layout: /opt/schoolbell/releases/<version> (+ current -> symlink), /etc/schoolbell (config, TLS,
# release key), /var/lib/schoolbell (database, sounds, recordings, backups, Caddy state). Re-running
# keeps every setting and all data. Uninstall: sudo bash /opt/schoolbell/current/uninstall.sh
set -euo pipefail

PREFIX=/opt/schoolbell
ETC=/etc/schoolbell
VAR=/var/lib/schoolbell
PROXYVAR=/var/lib/schoolbell-proxy   # Caddy state (internal CA, certificates), owned by schoolbell-proxy
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION="$(cat "$HERE/VERSION" 2>/dev/null || echo unknown)"
HOSTS=(); TZ_ARG=""; TLS_CERT=""; TLS_KEY=""; SERVER_NAME=""; FIREWALL=1; MODE=install

while [ $# -gt 0 ]; do
  case "$1" in
    --hostname) HOSTS+=("$2"); shift 2;;
    --timezone) TZ_ARG=$2; shift 2;;
    --tls-cert) TLS_CERT=$2; shift 2;;
    --tls-key) TLS_KEY=$2; shift 2;;
    --server-name) SERVER_NAME=$2; shift 2;;
    --no-firewall) FIREWALL=0; shift;;
    --upgrade) MODE=upgrade; shift;;
    --rollback) MODE=rollback; shift;;
    -h|--help) sed -n '2,20p' "$0"; exit 0;;
    *) echo "unknown option: $1 (see --help)" >&2; exit 2;;
  esac
done

say() { echo "== $*"; }
die() { echo "ERROR: $*" >&2; exit 1; }
[ "$(id -u)" = 0 ] || die "run as root: sudo bash install.sh ..."
command -v systemctl >/dev/null || die "systemd is required (Ubuntu 24.04 LTS)"
[ "$(uname -m)" = x86_64 ] || die "x86-64 only (this bundle is linux-x64)"
# read os-release in a subshell: it defines VERSION, which must not replace ours
OS=$( [ -r /etc/os-release ] && . /etc/os-release && echo "${ID:-}|${VERSION_ID:-}|${PRETTY_NAME:-unknown}" || echo "||unknown")
[ "${OS%%|*}" = ubuntu ] && [ "$(echo "$OS" | cut -d'|' -f2)" = 24.04 ] || echo "WARNING: tested on Ubuntu 24.04; this is ${OS##*|}"

health() { # health URL expected-version seconds
  local url=$1 want=$2 n=$3 got
  for _ in $(seq 1 "$n"); do
    got=$(curl -sf --max-time 3 "$url" 2>/dev/null | sed -n 's/.*"version":"\([^"]*\)".*/\1/p') || true
    [ -n "$got" ] && { [ -z "$want" ] || [ "$got" = "$want" ]; } && { echo "$got"; return 0; }
    sleep 1
  done
  return 1
}

current_version() { [ -L "$PREFIX/current" ] && basename "$(readlink -f "$PREFIX/current")" || true; }

backup_db() { # label -> prints backup dir
  local dir="$VAR/backups/$1-$(date -u +%Y%m%d-%H%M%S)"
  install -d -m 0750 -o schoolbell -g schoolbell "$dir"
  for f in schoolbell.db schoolbell.db-wal schoolbell.db-shm; do [ -f "$VAR/$f" ] && cp -a "$VAR/$f" "$dir/"; done
  [ -d "$VAR/keys" ] && cp -a "$VAR/keys" "$dir/"
  echo "$dir"
}

if [ "$MODE" = rollback ]; then
  PREV=$(cat "$PREFIX/previous" 2>/dev/null || true)
  [ -n "$PREV" ] && [ -d "$PREFIX/releases/$PREV" ] || die "no previous release recorded"
  BK=$(cat "$PREFIX/previous-backup" 2>/dev/null || true)
  say "rolling back to $PREV"
  systemctl stop schoolbell || true
  if [ -n "$BK" ] && [ -f "$BK/schoolbell.db" ]; then
    say "restoring the database taken before the upgrade ($BK)"
    rm -f "$VAR/schoolbell.db-wal" "$VAR/schoolbell.db-shm"
    cp -a "$BK"/schoolbell.db* "$VAR/"
  fi
  ln -sfn "releases/$PREV" "$PREFIX/current.new" && mv -Tf "$PREFIX/current.new" "$PREFIX/current"
  if [ -x "$PREFIX/current/whisper/bin/whisper-server" ]; then
    systemctl restart schoolbell-whisper
  else
    systemctl disable --now schoolbell-whisper 2>/dev/null || true
    systemctl reset-failed schoolbell-whisper 2>/dev/null || true
  fi
  systemctl start schoolbell
  health http://127.0.0.1:5001/api/health "$PREV" 90 >/dev/null || die "rollback started but $PREV is not answering — journalctl -u schoolbell"
  say "rolled back to $PREV"
  exit 0
fi

[ -x "$HERE/app/SchoolBell.Server" ] || die "bundle incomplete: $HERE/app/SchoolBell.Server missing"
[ -x "$HERE/whisper/bin/whisper-server" ] || die "bundle incomplete: Whisper server missing"
[ -f "$HERE/whisper/models/ggml-small.en.bin" ] || die "bundle incomplete: Whisper small.en model missing"
FROM=$(current_version)
say "SchoolBell $VERSION ($([ -n "$FROM" ] && echo "upgrade from $FROM" || echo "new install"))"

say "service accounts and directories"
id -u schoolbell >/dev/null 2>&1 || useradd --system --home-dir "$VAR" --no-create-home --shell /usr/sbin/nologin schoolbell
id -u schoolbell-proxy >/dev/null 2>&1 || useradd --system --home-dir "$PROXYVAR" --no-create-home --shell /usr/sbin/nologin schoolbell-proxy
install -d -m 0755 "$PREFIX" "$PREFIX/releases" "$PREFIX/bin"
install -d -m 0755 -o root -g root "$ETC"   # files inside carry their own modes (env 0640, tls key 0640)
install -d -m 0750 -o schoolbell -g schoolbell "$VAR" "$VAR/backups" "$VAR/update"
install -d -m 0700 -o schoolbell-proxy -g schoolbell-proxy "$PROXYVAR"
chown -R schoolbell-proxy:schoolbell-proxy "$PROXYVAR"

# release signing public key: pinned at first install from the bundle the administrator verified
if [ ! -f "$ETC/release-signing.pub.pem" ]; then install -m 0644 "$HERE/release-signing.pub.pem" "$ETC/release-signing.pub.pem"
elif ! cmp -s "$HERE/release-signing.pub.pem" "$ETC/release-signing.pub.pem"; then
  echo "WARNING: this bundle carries a different release-signing key than the installed one; keeping the installed key"
fi

PREV_BK=""
if [ -f "$VAR/schoolbell.db" ] && [ -n "$FROM" ] && [ "$FROM" != "$VERSION" ]; then
  say "stopping and backing up before the upgrade"
  systemctl stop schoolbell 2>/dev/null || true
  PREV_BK=$(backup_db "pre-upgrade-$FROM-to-$VERSION")
  echo "   backup: $PREV_BK"
fi

say "installing release $VERSION"
DEST="$PREFIX/releases/$VERSION"
rm -rf "$DEST.tmp"; mkdir -p "$DEST.tmp"
cp -a "$HERE/." "$DEST.tmp/"
chown -R root:root "$DEST.tmp"; chmod -R go-w "$DEST.tmp"
rm -rf "$DEST"; mv "$DEST.tmp" "$DEST"
install -m 0755 "$HERE/bin/caddy" "$PREFIX/bin/caddy.new" && mv -f "$PREFIX/bin/caddy.new" "$PREFIX/bin/caddy"
install -m 0755 "$HERE/schoolbell-update.sh" /usr/local/sbin/schoolbell-update

say "configuration (existing values kept)"
ENVF="$ETC/env"
if [ ! -f "$ENVF" ]; then
  cat > "$ENVF" <<EOF
# SchoolBell server settings (edit, then: systemctl restart schoolbell)
SchoolBell__Urls=http://127.0.0.1:5001
SchoolBell__DataDir=$VAR
SchoolBell__ReleaseKeyPath=$ETC/release-signing.pub.pem
SchoolBell__UpdateRepo=0xC0FFEEBEEF/BellSystem
SchoolBell__TranscriptionEnabled=true
SchoolBell__WhisperUrl=http://127.0.0.1:8178
# SchoolBell__MaxConnections=1000      (visible, configurable; not an enrollment limit)
# SchoolBell__OutboxCap=500
EOF
fi
setenv() { # key value
  if grep -q "^$1=" "$ENVF"; then sed -i "s|^$1=.*|$1=$2|" "$ENVF"; else echo "$1=$2" >> "$ENVF"; fi
}
# New and upgraded servers get local transcription unless an administrator already made an
# explicit choice in the environment file.
grep -q '^SchoolBell__TranscriptionEnabled=' "$ENVF" || echo 'SchoolBell__TranscriptionEnabled=true' >> "$ENVF"
grep -q '^SchoolBell__WhisperUrl=' "$ENVF" || echo 'SchoolBell__WhisperUrl=http://127.0.0.1:8178' >> "$ENVF"
if [ -n "$TZ_ARG" ]; then
  [ -f "/usr/share/zoneinfo/$TZ_ARG" ] || die "unknown timezone '$TZ_ARG' (use an IANA name like America/Chicago)"
  setenv SchoolBell__SchoolTz "$TZ_ARG"
fi
[ -n "$SERVER_NAME" ] && setenv SchoolBell__ServerName "$SERVER_NAME"
if [ ${#HOSTS[@]} -gt 0 ]; then printf '%s\n' "${HOSTS[@]}" > "$ETC/hostnames"; fi
[ -f "$ETC/hostnames" ] || { [ "$MODE" = upgrade ] && die "no hostnames configured"; hostname -I | awk '{print $1}' > "$ETC/hostnames"; echo "   no --hostname given: using $(cat "$ETC/hostnames")"; }
mapfile -t NAMES < "$ETC/hostnames"
i=0; sed -i '/^SchoolBell__AllowedWsOrigins__/d' "$ENVF"
for h in "${NAMES[@]}"; do echo "SchoolBell__AllowedWsOrigins__$i=https://$h" >> "$ENVF"; i=$((i+1)); done
chown root:schoolbell "$ENVF"; chmod 0640 "$ENVF"

if [ -n "$TLS_CERT" ]; then
  [ -f "$TLS_CERT" ] && [ -f "$TLS_KEY" ] || die "--tls-cert and --tls-key must both exist"
  install -d -m 0750 -o root -g schoolbell-proxy "$ETC/tls"
  install -m 0644 "$TLS_CERT" "$ETC/tls/server.crt"; install -m 0640 -g schoolbell-proxy "$TLS_KEY" "$ETC/tls/server.key"
fi
TLS_LINE="tls internal"; [ -f "$ETC/tls/server.crt" ] && TLS_LINE="tls $ETC/tls/server.crt $ETC/tls/server.key"
SITES=$(printf 'https://%s, ' "${NAMES[@]}"); SITES=${SITES%, }
sed -e "s|@SITES@|$SITES|" -e "s|@TLS@|$TLS_LINE|" -e "s|@PROXYVAR@|$PROXYVAR|g" "$HERE/caddy/Caddyfile.tmpl" > "$ETC/Caddyfile.new"
chmod 0644 "$ETC/Caddyfile.new"
# validate as the proxy account: validation provisions the internal CA, which must be owned by it
runuser -u schoolbell-proxy -- env HOME="$PROXYVAR" XDG_DATA_HOME="$PROXYVAR" XDG_CONFIG_HOME="$PROXYVAR" \
  "$PREFIX/bin/caddy" validate --config "$ETC/Caddyfile.new" --adapter caddyfile >/dev/null 2>&1 || die "generated Caddyfile is invalid ($ETC/Caddyfile.new)"
mv -f "$ETC/Caddyfile.new" "$ETC/Caddyfile"; chmod 0644 "$ETC/Caddyfile"

say "systemd units"
for u in schoolbell.service schoolbell-whisper.service schoolbell-caddy.service schoolbell-update.service schoolbell-update.path; do
  install -m 0644 "$HERE/systemd/$u" "/etc/systemd/system/$u"
done
systemctl daemon-reload

echo "$FROM" > "$PREFIX/previous.tmp"; echo "$PREV_BK" > "$PREFIX/previous-backup.tmp"
ln -sfn "releases/$VERSION" "$PREFIX/current.new" && mv -Tf "$PREFIX/current.new" "$PREFIX/current"
[ -n "$FROM" ] && [ "$FROM" != "$VERSION" ] && { mv -f "$PREFIX/previous.tmp" "$PREFIX/previous"; mv -f "$PREFIX/previous-backup.tmp" "$PREFIX/previous-backup"; }
rm -f "$PREFIX/previous.tmp" "$PREFIX/previous-backup.tmp"

systemctl enable schoolbell schoolbell-whisper schoolbell-caddy schoolbell-update.path >/dev/null 2>&1
systemctl restart schoolbell-whisper
WHISPER_OK=no
for _ in $(seq 1 90); do
  if curl -sf --max-time 2 http://127.0.0.1:8178/ >/dev/null; then WHISPER_OK=yes; break; fi
  sleep 1
done
if [ "$WHISPER_OK" != yes ]; then
  journalctl -u schoolbell-whisper -n 30 --no-pager >&2 || true
  echo "ERROR: the local Whisper service did not become ready" >&2
  if [ -n "$FROM" ] && [ "$FROM" != "$VERSION" ] && [ -d "$PREFIX/releases/$FROM" ]; then
    echo "== automatic rollback to $FROM" >&2
    bash "$0" --rollback || true
  fi
  exit 1
fi
systemctl restart schoolbell
say "health check"
if ! GOT=$(health http://127.0.0.1:5001/api/health "$VERSION" 120); then
  echo "ERROR: SchoolBell $VERSION did not become healthy:" >&2
  journalctl -u schoolbell -n 30 --no-pager >&2 || true
  if [ -n "$FROM" ] && [ "$FROM" != "$VERSION" ] && [ -d "$PREFIX/releases/$FROM" ]; then
    echo "== automatic rollback to $FROM" >&2
    bash "$0" --rollback || true
  fi
  exit 1
fi
systemctl restart schoolbell-caddy
systemctl start schoolbell-update.path

if [ "$FIREWALL" = 1 ] && command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "Status: active"; then
  say "ufw is active: allowing 443/tcp (existing rules untouched)"
  ufw allow 443/tcp >/dev/null
fi

# CA certificate for Windows clients (Caddy internal CA unless the school certificate is used)
systemctl is-active --quiet schoolbell-caddy || { sleep 3; systemctl is-active --quiet schoolbell-caddy || { echo "ERROR: Caddy did not start:" >&2; journalctl -u schoolbell-caddy -n 15 --no-pager >&2; }; }
CA_SRC="$PROXYVAR/pki/authorities/local/root.crt"
for _ in $(seq 1 30); do [ -f "$CA_SRC" ] || [ -f "$ETC/tls/server.crt" ] && break; sleep 1; done
[ -f "$CA_SRC" ] && install -m 0644 "$CA_SRC" "$ETC/school-ca.cer"
HTTPS_OK=no
if [ -f "$ETC/school-ca.cer" ]; then
  curl -sf --max-time 5 --cacert "$ETC/school-ca.cer" --resolve "${NAMES[0]}:443:127.0.0.1" "https://${NAMES[0]}/api/health" >/dev/null && HTTPS_OK=yes
else
  curl -sf --max-time 5 --resolve "${NAMES[0]}:443:127.0.0.1" "https://${NAMES[0]}/api/health" >/dev/null && HTTPS_OK=yes
fi

CODE=$(journalctl -u schoolbell --no-pager -o cat 2>/dev/null | grep -o 'SETUP CODE: [0-9a-f]*' | tail -1 | awk '{print $3}' || true)
cat <<EOF

== SchoolBell $GOT is running.
   Office:   $(printf 'https://%s/  ' "${NAMES[@]}")   (HTTPS check from this server: $HTTPS_OK)
   Services: systemctl status schoolbell schoolbell-whisper schoolbell-caddy
   Logs:     journalctl -u schoolbell -f   ·   journalctl -u schoolbell-whisper -f
$( [ -f "$ETC/school-ca.cer" ] && echo "   Windows clients must trust: $ETC/school-ca.cer   (give it to SchoolBellSetup.exe --ca-file)" )
$( [ -n "$CODE" ] && echo "   First sign-in: open the office address, choose 'First-time setup' and enter setup code $CODE" )
   Then in /admin: check the school timezone, create an enrollment file, build and publish schedules.
   Windows: SchoolBellSetup.exe --silent --server ${NAMES[0]} --enrollment-file schoolbell-enroll.json --ca-file school-ca.cer
EOF
