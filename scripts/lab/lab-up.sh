#!/usr/bin/env bash
# Lab only: run the SchoolBell server on loopback plus Caddy (internal CA) on the libvirt bridge
# so the schoolbell-lab Windows VM reaches https://192.168.122.1:8443. Never for production:
# production uses install.sh (systemd, /etc + /var/lib layout, port 443).
# Usage: lab-up.sh LABDIR [published-server-dir]
set -euo pipefail
LAB=${1:?lab dir}; SRV=${2:-}
REPO=$(cd "$(dirname "$0")/../.." && pwd)
CADDY=${CADDY:-caddy}
BIND=${BIND:-192.168.122.1}; PORT=${PORT:-8443}
mkdir -p "$LAB/data" "$LAB/caddy" "$LAB/logs"
export SchoolBell__DataDir="$LAB/data"
export SchoolBell__AllowedWsOrigins__0="https://$BIND:$PORT"
export SchoolBell__ReleaseKeyPath="$REPO/build/release-signing.pub.pem"
export SchoolBell__WebRoot="${WEBROOT:-$REPO/web/dist}"
export ASPNETCORE_ENVIRONMENT=Production
if [ -n "$SRV" ]; then
  (cd "$SRV" && exec ./SchoolBell.Server) >"$LAB/logs/server.log" 2>&1 &
else
  (cd "$REPO/src/Server" && exec dotnet run -c Release --no-launch-profile) >"$LAB/logs/server.log" 2>&1 &
fi
echo $! > "$LAB/server.pid"
cat > "$LAB/caddy/Caddyfile" <<CADDY
{
	admin off
	skip_install_trust
	storage file_system $LAB/caddy/storage
	auto_https disable_redirects
}
https://$BIND:$PORT {
	bind $BIND
	tls internal
	reverse_proxy 127.0.0.1:5001
}
CADDY
"$CADDY" run --config "$LAB/caddy/Caddyfile" --adapter caddyfile >"$LAB/logs/caddy.log" 2>&1 &
echo $! > "$LAB/caddy.pid"
for i in $(seq 1 60); do
  curl -sf http://127.0.0.1:5001/api/health >/dev/null 2>&1 && break; sleep 1
done
curl -s http://127.0.0.1:5001/api/health; echo
for i in $(seq 1 20); do
  [ -f "$LAB/caddy/storage/pki/authorities/local/root.crt" ] && break; sleep 0.5
done
cp "$LAB/caddy/storage/pki/authorities/local/root.crt" "$LAB/lab-ca.crt" 2>/dev/null || true
curl -s --cacert "$LAB/lab-ca.crt" "https://$BIND:$PORT/api/health"; echo
grep -h 'SETUP CODE' "$LAB/logs/server.log" | tail -1 || true
