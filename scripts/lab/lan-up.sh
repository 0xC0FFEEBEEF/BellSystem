#!/usr/bin/env bash
# Lab only: serve the lab server to the LAN (Caddy on 0.0.0.0:PORT with internal-CA certificates
# for each listed address). Reuses LABDIR's database and Caddy CA. Never a production setup.
# Usage: lan-up.sh LABDIR SERVERDIR PORT ADDR [ADDR...]
set -euo pipefail
LAB=$1; SRV=$2; PORT=$3; shift 3; ADDRS=("$@")
REPO=$(cd "$(dirname "$0")/../.." && pwd)
for p in caddy server; do [ -f "$LAB/$p.pid" ] && kill "$(cat "$LAB/$p.pid")" 2>/dev/null || true; done
sleep 1
export SchoolBell__DataDir="$LAB/data" SchoolBell__Urls=http://127.0.0.1:5001 ASPNETCORE_ENVIRONMENT=Production
export SchoolBell__ReleaseKeyPath="$REPO/build/release-signing.pub.pem" SchoolBell__WebRoot="$REPO/web/dist"
export SchoolBell__MinPasswordLength=${MIN_PW:-12}
i=0; for a in "${ADDRS[@]}"; do export "SchoolBell__AllowedWsOrigins__$i=https://$a:$PORT"; i=$((i+1)); done
(cd "$SRV" && exec setsid ./SchoolBell.Server) >>"$LAB/logs/server.log" 2>&1 &
echo $! > "$LAB/server.pid"
SITES=$(printf 'https://%s:%s, ' "${ADDRS[@]/%/}" | sed "s/:, /:$PORT, /g")
SITES=""; for a in "${ADDRS[@]}"; do SITES+="https://$a:$PORT, "; done; SITES=${SITES%, }
cat > "$LAB/caddy/Caddyfile" <<CADDY
{
	admin off
	skip_install_trust
	storage file_system $LAB/caddy/storage
	auto_https disable_redirects
}
$SITES {
	bind 0.0.0.0
	tls internal
	reverse_proxy 127.0.0.1:5001 {
		header_up X-Forwarded-Host {hostport}
	}
}
CADDY
(exec setsid "${CADDY:-caddy}" run --config "$LAB/caddy/Caddyfile" --adapter caddyfile) >>"$LAB/logs/caddy.log" 2>&1 &
echo $! > "$LAB/caddy.pid"
for i in $(seq 1 60); do curl -sf http://127.0.0.1:5001/api/health >/dev/null && break; sleep 1; done
for a in "${ADDRS[@]}"; do echo "$a: $(curl -s --max-time 5 --cacert "$LAB/lab-ca.crt" "https://$a:$PORT/api/health")"; done
