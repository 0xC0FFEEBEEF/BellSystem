#!/usr/bin/env bash
# Probe: at a no-user boot, is session-0 audio audible at all? (Windows SoundPlayer from SYSTEM,
# then a SchoolBell ring), then the same after one sign-in + sign-out. Restores the lab account.
set -uo pipefail
LAB=${1:?lab dir}; REPO=$(cd "$(dirname "$0")/../.." && pwd); VX="$REPO/scripts/lab/vmexec.py"
A=/home/nbull/VMs/schoolbell-lab/audio/out.wav; LOG=$REPO/docs/evidence/gate-a-matrix.txt
agent() { until "$VX" --timeout 10 -c '1' >/dev/null 2>&1; do sleep 5; done; }
conn() { until "$LAB/api" GET /api/devices | grep -q '"connected": true'; do sleep 3; done; }
probe() { # label
  local s0; s0=$(stat -c %s "$A")
  "$VX" -c '(New-Object Media.SoundPlayer "C:\Windows\Media\Alarm01.wav").PlaySync(); "played via SoundPlayer (SYSTEM, session 0)"' | sed 's/^/  /'
  echo "  [$1] Windows SoundPlayer from session 0:"; "$REPO/scripts/lab/wavanalyze.py" "$A" --from-byte "$s0" | sed 's/^/  /'
  s0=$(stat -c %s "$A")
  "$LAB/api" POST /api/bells/ring "{\"target\":{\"all\":true},\"bell_name\":\"$1\"}" >/dev/null; sleep 6
  echo "  [$1] SchoolBell ring via the service renderer:"; "$REPO/scripts/lab/wavanalyze.py" "$A" --from-byte "$s0" | sed 's/^/  /'
}
{
echo "--- [noboot-probe] $(date -u +%FT%TZ)"
TP=$(openssl rand -hex 8)
"$VX" -c "net user User $TP | Out-Null; Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' AutoAdminLogon '0'; shutdown /r /t 1" >/dev/null
sleep 40; agent; conn; sleep 5
"$VX" -c '"  sessions: " + ((query user 2>&1) -join " ")'
probe "no-user boot"
"$VX" -c "net user User '' | Out-Null; Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' AutoAdminLogon '1'; '  restored blank password + auto sign-in'; shutdown /r /t 1"
sleep 40; agent; conn
} 2>&1 | tee -a "$LOG"
