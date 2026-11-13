#!/usr/bin/env bash
# Root-only helper triggered by schoolbell-whisper-control.path. The request is untrusted input from
# the web app: validate it before writing a systemd override or invoking systemctl.
set -euo pipefail
DIR=/var/lib/schoolbell/whisper-control
REQ=$DIR/request.json
STATUS=$DIR/status.json
DROPIN=/etc/systemd/system/schoolbell-whisper.service.d/zz-schoolbell-admin.conf

write_status() { # state cores message
  python3 - "$1" "$2" "$3" "$STATUS" <<'PY'
import json, os, sys, time
state, cores, message, path = sys.argv[1:]
tmp = path + ".tmp"
with open(tmp, "w") as f:
    json.dump({"state": state, "cpu_cores": int(cores), "message": message,
               "at": int(time.time() * 1000)}, f)
os.replace(tmp, path)
PY
  chown schoolbell:schoolbell "$STATUS" 2>/dev/null || true
}

[ -f "$REQ" ] || exit 0
CORES=$(python3 - "$REQ" <<'PY'
import json, sys
try:
    value = json.load(open(sys.argv[1])).get("cpu_cores")
    if type(value) is not int: raise ValueError()
    print(value)
except Exception:
    raise SystemExit(2)
PY
) || { write_status failed 0 "invalid control request"; rm -f "$REQ"; exit 1; }

AVAILABLE=$(nproc)
MAX=$((AVAILABLE > 2 ? AVAILABLE - 2 : 1))
[ "$MAX" -gt 16 ] && MAX=16
if [ "$CORES" -lt 1 ] || [ "$CORES" -gt "$MAX" ]; then
  write_status failed "$CORES" "cpu_cores must be between 1 and $MAX"
  rm -f "$REQ"
  exit 1
fi

install -d -m 0755 /etc/systemd/system/schoolbell-whisper.service.d
TMP=$(mktemp /etc/systemd/system/schoolbell-whisper.service.d/.schoolbell-admin.XXXXXX)
cat > "$TMP" <<EOF
[Service]
ExecStart=
ExecStart=/opt/schoolbell/current/whisper/bin/whisper-server --host 127.0.0.1 --port 8178 --threads $CORES --processors 1 --model /opt/schoolbell/current/whisper/models/ggml-small.en.bin --no-gpu --no-timestamps
EOF
chmod 0644 "$TMP"
mv -f "$TMP" "$DROPIN"
systemctl daemon-reload
systemctl set-property schoolbell-whisper.service "CPUQuota=${CORES}00%"
systemctl restart schoolbell-whisper.service

READY=no
for _ in $(seq 1 60); do
  if curl -sf --max-time 2 http://127.0.0.1:8178/health >/dev/null; then READY=yes; break; fi
  sleep 1
done
if [ "$READY" != yes ]; then
  write_status failed "$CORES" "Whisper did not become ready; check journalctl -u schoolbell-whisper"
  rm -f "$REQ"
  exit 1
fi
write_status applied "$CORES" "Whisper is running with $CORES CPU core(s)"
rm -f "$REQ"
