#!/usr/bin/env bash
# Server lifecycle test in the sb-ubuntu systemd container (0.9.0 already installed):
#   repeat install -> data survives; self-update 0.9.0 -> 0.9.1 through the real path (GitHub-API
#   mock over HTTPS, /admin check + apply, path unit, root updater, signature, backup, health);
#   tampered release refused; signed-but-broken 0.9.2 rolled back automatically; uninstall keeps data.
set -uo pipefail
REPO=$(cd "$(dirname "$0")/../.." && pwd)
W=${1:?work dir}; mkdir -p "$W"
LOG=$REPO/docs/evidence/ubuntu-lifecycle.txt
exec > >(tee "$LOG") 2>&1
C="podman exec sb-ubuntu"
KEY=${SCHOOLBELL_SIGNING_KEY:-$HOME/.config/schoolbell-release/release-signing.key.pem}
echo "=== server lifecycle test $(date -u +%FT%TZ) (Ubuntu 24.04.5 systemd container)"

# office API client against the container (443 mapped to 127.0.0.1:9443)
podman cp sb-ubuntu:/etc/schoolbell/school-ca.cer "$W/ca.cer"
cat > "$W/api" <<EOF
#!/bin/sh
exec python3 $REPO/scripts/lab/sbapi.py --base https://localhost:9443 --ca $W/ca.cer --jar $W/jar.txt "\$@"
EOF
chmod +x "$W/api"; rm -f "$W/jar.txt"
CODE=$($C journalctl -u schoolbell -o cat --no-pager | grep -o 'SETUP CODE: [0-9a-f]*' | tail -1 | awk '{print $3}')
PW=$(openssl rand -hex 12)
"$W/api" POST /api/auth/setup "{\"code\":\"$CODE\",\"username\":\"admin\",\"password\":\"$PW\"}" >/dev/null
"$W/api" POST /api/auth/login "{\"username\":\"admin\",\"password\":\"$PW\"}" >/dev/null
"$W/api" POST /api/schedules '{"name":"Regular Day","target":{"all":true},"weekdays":[1,2,3,4,5],"periods":[{"name":"Period 1","start_hm":"08:30","end_hm":"09:20","warn_min":0,"sound_id":"std-bell","volume_policy":"announcement","duration_s":3}]}' >/dev/null
"$W/api" POST /api/admin/enrollment-tokens '{"label":"lifecycle","ttl_hours":2,"max_uses":5}' >/dev/null
echo "--- data created: 1 schedule, 1 enrollment token"

echo "--- 1. repeat install (no options): settings and data must survive"
$C bash -c 'cd /root/sb/schoolbell-0.9.0 && bash install.sh 2>&1 | grep -E "^== SchoolBell|ERROR" '
"$W/api" GET /api/schedules | python3 -c 'import sys,json; print("  schedules after repeat install:", [t["name"] for t in json.load(sys.stdin)])'

echo "--- 2. self-update 0.9.0 -> 0.9.1 from a GitHub-API mock (HTTPS, signed release)"
M=$W/mock; rm -rf "$M"; mkdir -p "$M/www/repos/0xC0FFEEBEEF/BellSystem/releases" "$M/www/assets/0.9.1" "$M/storage"
cp "$REPO/.build/rel-0.9.1/release-manifest.json" "$REPO/.build/rel-0.9.1/release-manifest.json.sig" "$REPO/.build/rel-0.9.1/server/schoolbell-server-linux-x64.tar.gz" "$M/www/assets/0.9.1/"
mkrelease() { # version dir
  cat > "$M/www/repos/0xC0FFEEBEEF/BellSystem/releases/latest" <<EOF
{"tag_name":"v$1","html_url":"https://example.invalid/v$1","assets":[
 {"name":"release-manifest.json","browser_download_url":"https://host.containers.internal:9500/assets/$2/release-manifest.json"},
 {"name":"release-manifest.json.sig","browser_download_url":"https://host.containers.internal:9500/assets/$2/release-manifest.json.sig"},
 {"name":"schoolbell-server-linux-x64.tar.gz","browser_download_url":"https://host.containers.internal:9500/assets/$2/schoolbell-server-linux-x64.tar.gz"}]}
EOF
}
mkrelease 0.9.1 0.9.1
cat > "$M/Caddyfile" <<EOF
{
	admin off
	skip_install_trust
	storage file_system $M/storage
	auto_https disable_redirects
}
https://host.containers.internal:9500 {
	bind 0.0.0.0
	tls internal
	root * $M/www
	file_server
}
EOF
CADDY=${CADDY:-caddy}
"$CADDY" run --config "$M/Caddyfile" --adapter caddyfile > "$M/caddy.log" 2>&1 & MOCK=$!
for i in $(seq 1 20); do [ -f "$M/storage/pki/authorities/local/root.crt" ] && break; sleep 0.5; done
podman cp "$M/storage/pki/authorities/local/root.crt" sb-ubuntu:/usr/local/share/ca-certificates/github-mock.crt
$C bash -c 'update-ca-certificates >/dev/null 2>&1; echo "SchoolBell__UpdateApiBase=https://host.containers.internal:9500" >> /etc/schoolbell/env; systemctl restart schoolbell; for i in $(seq 1 60); do curl -sf http://127.0.0.1:5001/api/health >/dev/null && break; sleep 1; done'
"$W/api" POST /api/auth/login "{\"username\":\"admin\",\"password\":\"$PW\"}" >/dev/null
"$W/api" POST /api/admin/updates/server/check | python3 -c 'import sys,json; d=json.load(sys.stdin); print("  check: running", d["current_version"], "| latest", (d.get("latest") or {}).get("version"), "verified", (d.get("latest") or {}).get("verified"), "newer", (d.get("latest") or {}).get("newer"), "| error", d.get("last_error"))'
"$W/api" POST /api/admin/updates/server/apply '{"version":"0.9.1"}' | python3 -c 'import sys,json; print("  apply:", json.load(sys.stdin))'
for i in $(seq 1 120); do st=$($C cat /var/lib/schoolbell/update/status.json 2>/dev/null); echo "$st" | grep -qE '"state": "(done|failed|rolled_back)"' && break; sleep 2; done
echo "  updater status: $st"
$C bash -c 'echo "  current -> $(readlink /opt/schoolbell/current); releases: $(ls /opt/schoolbell/releases | tr "\n" " "); backups: $(ls /var/lib/schoolbell/backups | tr "\n" " ")"; curl -s http://127.0.0.1:5001/api/health; echo'
"$W/api" POST /api/auth/login "{\"username\":\"admin\",\"password\":\"$PW\"}" >/dev/null
"$W/api" GET /api/schedules | python3 -c 'import sys,json; print("  schedules after update:", [t["name"] for t in json.load(sys.stdin)])'

echo "--- 3. tampered release: manifest edited after signing -> must be refused"
TV=0.9.2; mkdir -p "$M/www/assets/tampered"
python3 - "$REPO/.build/rel-0.9.1/release-manifest.json" "$M/www/assets/tampered/release-manifest.json" <<'PY'
import json, sys
m = json.load(open(sys.argv[1])); m["version"] = "0.9.2"
json.dump(m, open(sys.argv[2], "w"), indent=1, sort_keys=True)
PY
cp "$REPO/.build/rel-0.9.1/release-manifest.json.sig" "$REPO/.build/rel-0.9.1/server/schoolbell-server-linux-x64.tar.gz" "$M/www/assets/tampered/"
mkrelease 0.9.2 tampered
"$W/api" POST /api/admin/updates/server/check | python3 -c 'import sys,json; d=json.load(sys.stdin); print("  app check:", d.get("last_error"))'
# the root updater must refuse even if a compromised web app requests it directly
$C bash -c 'cat > /var/lib/schoolbell/update/request.json <<EOF
{"version":"0.9.2","manifest_url":"https://host.containers.internal:9500/assets/tampered/release-manifest.json","signature_url":"https://host.containers.internal:9500/assets/tampered/release-manifest.json.sig","tarball_url":"https://host.containers.internal:9500/assets/tampered/schoolbell-server-linux-x64.tar.gz","requested_by":"test"}
EOF
chown schoolbell:schoolbell /var/lib/schoolbell/update/request.json'
sleep 8
echo "  updater status: $($C cat /var/lib/schoolbell/update/status.json)"
echo "  still running: $($C curl -s http://127.0.0.1:5001/api/health)"

echo "--- 4. signed but broken 0.9.2 (server exits at start) -> automatic rollback with database restore"
B=$W/broken; rm -rf "$B"; mkdir -p "$B"; tar -xzf "$REPO/.build/rel-0.9.1/server/schoolbell-server-linux-x64.tar.gz" -C "$B"
mv "$B/schoolbell-0.9.1" "$B/schoolbell-0.9.2"; echo 0.9.2 > "$B/schoolbell-0.9.2/VERSION"
printf '#!/bin/sh\necho "deliberately broken test build" >&2\nexit 3\n' > "$B/schoolbell-0.9.2/app/SchoolBell.Server"; chmod +x "$B/schoolbell-0.9.2/app/SchoolBell.Server"
mkdir -p "$M/www/assets/0.9.2"
tar -C "$B" --owner=0 --group=0 -czf "$M/www/assets/0.9.2/schoolbell-server-linux-x64.tar.gz" schoolbell-0.9.2
python3 - "$M/www/assets/0.9.2" <<'PY'
import hashlib, json, os, sys, time
d = sys.argv[1]; t = os.path.join(d, "schoolbell-server-linux-x64.tar.gz")
m = {"product": "SchoolBell", "version": "0.9.2", "commit": "broken-test", "created_at": int(time.time() * 1000),
     "files": {"server/schoolbell-server-linux-x64.tar.gz": {"sha256": hashlib.sha256(open(t, "rb").read()).hexdigest(), "bytes": os.path.getsize(t)}}}
json.dump(m, open(os.path.join(d, "release-manifest.json"), "w"), indent=1, sort_keys=True)
PY
openssl dgst -sha256 -sign "$KEY" "$M/www/assets/0.9.2/release-manifest.json" | base64 -w0 > "$M/www/assets/0.9.2/release-manifest.json.sig"
mkrelease 0.9.2 0.9.2
"$W/api" POST /api/admin/updates/server/check | python3 -c 'import sys,json; d=json.load(sys.stdin); print("  check: latest", (d.get("latest") or {}).get("version"), "verified", (d.get("latest") or {}).get("verified"))'
"$W/api" POST /api/admin/updates/server/apply '{"version":"0.9.2"}' >/dev/null
for i in $(seq 1 150); do st=$($C cat /var/lib/schoolbell/update/status.json 2>/dev/null); echo "$st" | grep -q '0.9.2' && echo "$st" | grep -qE '"state": "(done|failed|rolled_back)"' && break; sleep 2; done
echo "  updater status: $st"
$C bash -c 'echo "  current -> $(readlink /opt/schoolbell/current)"; curl -s http://127.0.0.1:5001/api/health; echo'
"$W/api" POST /api/auth/login "{\"username\":\"admin\",\"password\":\"$PW\"}" >/dev/null
"$W/api" GET /api/schedules | python3 -c 'import sys,json; print("  schedules after rollback:", [t["name"] for t in json.load(sys.stdin)])'
$C tail -5 /var/lib/schoolbell/update/update.log | sed 's/^/  log: /'

echo "--- 5. uninstall keeps data; reinstall picks it up"
$C bash -c 'bash /opt/schoolbell/current/uninstall.sh; echo "  after uninstall: units=$(ls /etc/systemd/system | grep -c schoolbell) opt=$(test -d /opt/schoolbell && echo yes || echo no) data=$(test -f /var/lib/schoolbell/schoolbell.db && echo kept || echo gone)"'
$C bash -c 'cd /root/sb/schoolbell-0.9.0 && bash install.sh 2>&1 | grep -E "^== SchoolBell|ERROR"'
"$W/api" POST /api/auth/login "{\"username\":\"admin\",\"password\":\"$PW\"}" >/dev/null
"$W/api" GET /api/schedules | python3 -c 'import sys,json; print("  schedules after reinstall:", [t["name"] for t in json.load(sys.stdin)])'
kill $MOCK 2>/dev/null
