#!/usr/bin/env bash
# /usr/local/sbin/schoolbell-update — run as root by schoolbell-update.service when /admin writes
# /var/lib/schoolbell/update/request.json. The request comes from the (unprivileged) web app and is
# treated as untrusted: only https URLs are fetched, and nothing is installed unless
#   1. release-manifest.json verifies against the pinned key /etc/schoolbell/release-signing.pub.pem,
#   2. the manifest's version is the requested one and newer than what is installed, and
#   3. the tarball's sha256 matches the signed manifest entry.
# install.sh then backs up the database, installs, health-checks and rolls back automatically.
# Progress is written to /var/lib/schoolbell/update/status.json for the /admin page.
set -uo pipefail
VAR=/var/lib/schoolbell
UPD=$VAR/update
KEY=/etc/schoolbell/release-signing.pub.pem
REQ=$UPD/request.json
STATUS=$UPD/status.json
LOGF=$UPD/update.log
exec >>"$LOGF" 2>&1

status() { # state message
  python3 - "$1" "$2" "${VERSION:-}" "$STATUS" <<'PY'
import json, sys, time
state, msg, ver, path = sys.argv[1:5]
open(path + ".tmp", "w").write(json.dumps({"state": state, "message": msg, "version": ver, "at": int(time.time() * 1000)}))
import os; os.replace(path + ".tmp", path)
PY
  chown schoolbell:schoolbell "$STATUS" 2>/dev/null || true
  echo "$(date -u +%FT%TZ) $1: $2"
}
fail() { status failed "$1"; rm -f "$REQ"; exit 1; }

[ -f "$REQ" ] || exit 0
read_field() { python3 -c 'import json,sys; v=json.load(open(sys.argv[1])).get(sys.argv[2]) or ""; print(v if isinstance(v,str) else "")' "$REQ" "$1"; }
VERSION=$(read_field version); MURL=$(read_field manifest_url); SURL=$(read_field signature_url); TURL=$(read_field tarball_url)
for u in "$MURL" "$SURL" "$TURL"; do case "$u" in https://*) ;; *) fail "refusing non-https URL in the update request";; esac; done
echo "$VERSION" | grep -Eq '^[0-9]+(\.[0-9]+){1,3}(-[A-Za-z0-9.]+)?$' || fail "invalid version in the request"
[ -f "$KEY" ] || fail "no pinned release key at $KEY"

WORK=$(mktemp -d /var/tmp/schoolbell-update.XXXXXX)
trap 'rm -rf "$WORK"' EXIT
status downloading "fetching the signed manifest for $VERSION"
curl -fsSL --proto '=https' --max-time 60 -o "$WORK/release-manifest.json" "$MURL" || fail "manifest download failed"
curl -fsSL --proto '=https' --max-time 60 -o "$WORK/release-manifest.json.sig" "$SURL" || fail "signature download failed"
base64 -d "$WORK/release-manifest.json.sig" > "$WORK/sig.der" 2>/dev/null || fail "signature is not base64"
openssl dgst -sha256 -verify "$KEY" -signature "$WORK/sig.der" "$WORK/release-manifest.json" >/dev/null 2>&1 \
  || fail "the release signature does NOT verify against the pinned key — update refused"

MVER=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$WORK/release-manifest.json")
[ "$MVER" = "$VERSION" ] || fail "signed manifest is for $MVER, not the requested $VERSION"
WANT=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["files"]["server/schoolbell-server-linux-x64.tar.gz"]["sha256"])' "$WORK/release-manifest.json") \
  || fail "signed manifest does not list the server tarball"
CUR=$(basename "$(readlink -f /opt/schoolbell/current)" 2>/dev/null || echo 0)
python3 - "$MVER" "$CUR" <<'PY' || fail "release $VERSION is not newer than the installed $CUR"
import sys
def key(v):
    core, _, suf = v.lstrip("v").partition("-")
    nums = ([int(x) if x.isdigit() else 0 for x in core.split(".")] + [0, 0, 0, 0])[:4]
    return (nums, 1 if suf == "" else 0, suf)   # a release sorts after its pre-releases
sys.exit(0 if key(sys.argv[1]) > key(sys.argv[2]) else 1)
PY

status downloading "fetching the server bundle"
curl -fsSL --proto '=https' --max-time 1800 -o "$WORK/bundle.tar.gz" "$TURL" || fail "bundle download failed"
GOT=$(sha256sum "$WORK/bundle.tar.gz" | awk '{print $1}')
[ "$GOT" = "$WANT" ] || fail "bundle sha256 $GOT does not match the signed manifest"

status verified "signature and checksum verified; installing $VERSION"
mkdir -p "$WORK/x" && tar -xzf "$WORK/bundle.tar.gz" -C "$WORK/x" --no-same-owner || fail "bundle could not be unpacked"
DIR=$(find "$WORK/x" -mindepth 1 -maxdepth 1 -type d | head -1)
[ -f "$DIR/install.sh" ] || fail "bundle has no install.sh"
rm -f "$REQ"
status installing "installing $VERSION (backup, migrate, health check)"
if bash "$DIR/install.sh" --upgrade --no-firewall; then
  status done "SchoolBell $VERSION installed and healthy"
else
  NOW=$(basename "$(readlink -f /opt/schoolbell/current)")
  if [ "$NOW" = "$VERSION" ]; then status failed "install reported an error; $VERSION is running — check journalctl -u schoolbell"
  else status rolled_back "SchoolBell $VERSION failed its health check; rolled back to $NOW (database restored from the pre-upgrade backup)"; fi
  exit 1
fi
