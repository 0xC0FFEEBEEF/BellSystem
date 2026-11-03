#!/usr/bin/env bash
# Gate A matrix on the schoolbell-lab Windows VM (real Windows audio stack; the VM's sound card
# output is captured to a WAV on the host by QEMU's file audio backend).
# Usage: gate-a.sh LABDIR [scenario...]   scenarios: session locked crash signedout svccrash output noboot
set -uo pipefail
LAB=${1:?lab dir}; shift
REPO=$(cd "$(dirname "$0")/../.." && pwd)
A=/home/nbull/VMs/schoolbell-lab/audio/out.wav
EV=$REPO/docs/evidence; mkdir -p "$EV"
LOG=$EV/gate-a-matrix.txt
API="$LAB/api"
VX="$REPO/scripts/lab/vmexec.py"
say() { echo "$*" | tee -a "$LOG"; }
vol() { "$VX" --timeout 60 -c "\$Action='x'; \$Volume=$1; \$Mute=$2; $(sed 's/^param(.*$//' "$REPO/scripts/lab/coreaudio.ps1")"; }
asuser() { "$VX" --timeout 120 -c "\$UserCode=@'
$1
'@
$(cat "$REPO/scripts/lab/as-user.ps1")"; }
sess() { asuser "\$ProcName='$1'; \$SetMute=$2; $(cat "$REPO/scripts/lab/naudio-sessions.ps1")"; }
svclog() { "$VX" --timeout 60 -c "Get-Content C:\\ProgramData\\SchoolBell\\logs\\service.log -Tail ${1:-8}" | cut -c1-200; }
login() { "$API" POST /api/auth/login "{\"username\":\"labadmin\",\"password\":\"$(cat "$LAB/admin-password.txt")\"}" >/dev/null; }
ring() { "$API" POST /api/bells/ring "{\"target\":{\"all\":true},\"bell_name\":\"$1\"}" | tr -d '\n '; echo; }
ringresult() { "$API" GET '/api/bells/rings?limit=1' | python3 -c 'import sys,json; r=json.load(sys.stdin)[0]; print("  ring result:", r["bell_name"], [(d["label"], d["state"], d.get("error_code")) for d in r["devices"]])' | tee -a "$LOG"; }
devstate() { "$API" GET /api/devices | python3 -c 'import sys,json; d=json.load(sys.stdin)["items"][0]; print("  server device state:", d["state"], "|", d["label"], "| connected", d["connected"])' | tee -a "$LOG"; }
audible() { "$REPO/scripts/lab/wavanalyze.py" "$A" --from-byte "$1" | tee -a "$LOG"; }
waitfor() { # waitfor "pattern" seconds
  for i in $(seq 1 "$2"); do svclog 60 | grep -q "$1" && return 0; sleep 1; done; return 1; }

login
say ""
say "=== Gate A matrix $(date -u +%FT%TZ) — VM schoolbell-lab (Windows 11 Pro 25H2 build 26200, HDA audio, 3 GB) ==="

for sc in "$@"; do
case $sc in
session)
  say "--- [session] SchoolBell's own app session muted in the Volume Mixer + master 50%"
  vol 0.50 0 | sed 's/^/  before: /' | tee -a "$LOG"
  sess SchoolBellCompanion 1 | sed 's/^/  before: /' | tee -a "$LOG"
  S0=$(stat -c %s "$A"); ring "session-mute test"
  sleep 1.3; sess SchoolBellCompanion -1 | sed 's/^/  during: /' | tee -a "$LOG"; vol -1 -1 | sed 's/^/  during: /' | tee -a "$LOG"
  sleep 5; sess SchoolBellCompanion -1 | sed 's/^/  after:  /' | tee -a "$LOG"; vol -1 -1 | sed 's/^/  after:  /' | tee -a "$LOG"
  audible "$S0"; ringresult
  sess SchoolBellCompanion 0 >/dev/null ;;
locked)
  say "--- [locked] console session locked (Win+L equivalent), companion keeps the renderer"
  asuser 'rundll32.exe user32.dll,LockWorkStation; "locked"' | sed 's/^/  /' | tee -a "$LOG"
  sleep 4; "$VX" -c '"  logonui running: " + [bool](Get-Process LogonUI -ErrorAction SilentlyContinue)' | tee -a "$LOG"
  S0=$(stat -c %s "$A"); ring "locked-session test"; sleep 6
  audible "$S0"; ringresult; devstate
  svclog 30 | grep -E "ring|renderer" | tail -3 | sed 's/^/  /' | tee -a "$LOG" ;;
crash)
  say "--- [crash] tray companion killed 4 s into a 20.6 s recorded announcement (master 40%)"
  vol 0.40 0 | sed 's/^/  before: /' | tee -a "$LOG"
  S0=$(stat -c %s "$A")
  "$API" upload /api/announcements/recorded file=@"$REPO/tests/audio/speech-long.wav" 'target={"all":true}' chime=0 | python3 -c 'import sys,json; print("  announcement", json.load(sys.stdin)["announcement_id"])' | tee -a "$LOG"
  sleep 4; "$VX" -c 'Stop-Process -Name SchoolBellCompanion -Force; "  companion killed"' | tee -a "$LOG"
  sleep 2; vol -1 -1 | sed 's/^/  2 s after kill: /' | tee -a "$LOG"
  sleep 20; vol -1 -1 | sed 's/^/  after end: /' | tee -a "$LOG"
  audible "$S0"
  svclog 40 | grep -E "lost|continues|restored|renderer|finished" | tail -6 | sed 's/^/  /' | tee -a "$LOG"
  "$VX" -c 'Start-ScheduledTask SchoolBellCompanion; "  companion restarted by its logon task"' | tee -a "$LOG"; sleep 20 ;;
signedout)
  say "--- [signedout] user signed out; no companion; the service's session-0 renderer plays (master 35% muted)"
  "$VX" -c 'logoff 1; Start-Sleep 8; "  sessions: " + ((query user 2>&1) -join " ")' | tee -a "$LOG"
  vol 0.35 1 | sed 's/^/  before: /' | tee -a "$LOG"
  sleep 3; devstate
  S0=$(stat -c %s "$A"); ring "signed-out test"
  sleep 1.3; vol -1 -1 | sed 's/^/  during: /' | tee -a "$LOG"
  sleep 5; vol -1 -1 | sed 's/^/  after:  /' | tee -a "$LOG"
  audible "$S0"; ringresult ;;
svccrash)
  say "--- [svccrash] (signed out) service killed 4 s into a recorded announcement it was rendering (master 30%)"
  vol 0.30 0 | sed 's/^/  before: /' | tee -a "$LOG"
  S0=$(stat -c %s "$A")
  "$API" upload /api/announcements/recorded file=@"$REPO/tests/audio/speech-long.wav" 'target={"all":true}' chime=0 >/dev/null
  sleep 4; vol -1 -1 | sed 's/^/  during: /' | tee -a "$LOG"
  "$VX" -c 'Stop-Process -Name SchoolBellService -Force; "  service process killed"' | tee -a "$LOG"
  sleep 25; vol -1 -1 | sed 's/^/  after SCM restart: /' | tee -a "$LOG"
  "$VX" -c '"  service: " + (Get-Service SchoolBell).Status' | tee -a "$LOG"
  svclog 40 | grep -E "restored volume|interrupted|starting|unknown|recovered" | tail -4 | sed 's/^/  /' | tee -a "$LOG"
  audible "$S0" ;;
output)
  say "--- [output] admin configures a speaker name that is not present on this PC"
  DID=$("$API" GET /api/devices | python3 -c 'import sys,json; print(json.load(sys.stdin)["items"][0]["device_id"])')
  "$API" PUT "/api/admin/devices/$DID/settings" '{"output_device":"Classroom Ceiling Speaker"}' >/dev/null
  sleep 5; devstate
  S0=$(stat -c %s "$A"); ring "missing-output test"; sleep 5
  audible "$S0"; ringresult
  "$API" PUT "/api/admin/devices/$DID/settings" '{"use_default_output":true}' >/dev/null; sleep 4; devstate ;;
noboot)
  say "--- [noboot] reboot with automatic sign-in disabled: nobody signed in since boot"
  "$VX" -c 'Set-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" AutoAdminLogon "0"; shutdown /r /t 1; "  rebooting"' | tee -a "$LOG"
  sleep 60
  for i in $(seq 1 60); do "$VX" --timeout 10 -c '1' >/dev/null 2>&1 && break; sleep 5; done
  for i in $(seq 1 60); do "$API" GET /api/devices | grep -q '"connected": true' && break; sleep 5; done
  "$VX" -c '"  sessions: " + ((query user 2>&1) -join " "); "  service: " + (Get-Service SchoolBell).Status' | tee -a "$LOG"
  devstate
  vol 0.50 0 >/dev/null
  S0=$(stat -c %s "$A"); ring "no-user boot test"; sleep 6
  audible "$S0"; ringresult
  "$VX" -c 'Set-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" AutoAdminLogon "1"; "  autologon re-enabled (takes effect next boot)"' | tee -a "$LOG" ;;
esac
done
