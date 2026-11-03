#!/usr/bin/env bash
# Windows installer test on the lab VM: clean -> silent install (documented command line) ->
# verify -> ring -> repair -> uninstall via the Apps & Features entry -> verify clean -> reinstall.
set -uo pipefail
LAB=${1:?lab dir}; REPO=$(cd "$(dirname "$0")/../.." && pwd); VX="$REPO/scripts/lab/vmexec.py"
SH=/home/nbull/VMs/schoolbell-lab/share; A=/home/nbull/VMs/schoolbell-lab/audio/out.wav
LOG=$REPO/docs/evidence/windows-installer.txt
exec > >(tee -a "$LOG") 2>&1
echo; echo "=== Windows installer test $(date -u +%FT%TZ) — SchoolBellSetup.exe $(sha256sum "$REPO/dist/windows/SchoolBellSetup.exe" | cut -c1-16)…"
mkdir -p "$SH/Deploy"; cp "$REPO/dist/windows/SchoolBellSetup.exe" "$SH/Deploy/"; cp "$LAB/lab-ca.crt" "$SH/Deploy/school-ca.cer"
"$LAB/api" --out "$SH/Deploy/schoolbell-enroll.json" POST /api/admin/enrollment-file '{"label":"installer test","max_uses":3,"expires_hours":4}' >/dev/null

echo "--- 1. remove the manual lab deployment (service, task, files, data, lab CA)"
"$VX" --timeout 300 -c '
Stop-Service SchoolBell -ErrorAction SilentlyContinue; sc.exe delete SchoolBell | Out-Null
Unregister-ScheduledTask -TaskName SchoolBellCompanion -Confirm:$false -ErrorAction SilentlyContinue
Get-Process SchoolBellCompanion -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item -Recurse -Force "C:\Program Files\SchoolBell","C:\ProgramData\SchoolBell" -ErrorAction SilentlyContinue
Get-ChildItem Cert:\LocalMachine\Root | ? Subject -like "*Caddy Local Authority*" | Remove-Item
New-Item -ItemType Directory -Force C:\Deploy | Out-Null; Copy-Item Z:\Deploy\* C:\Deploy\ -Force
"  clean: service=" + [bool](Get-Service SchoolBell -ErrorAction SilentlyContinue) + " files=" + (Test-Path "C:\Program Files\SchoolBell") + " caddy CA trusted=" + [bool](Get-ChildItem Cert:\LocalMachine\Root | ? Subject -like "*Caddy*")'

echo "--- 2. silent install: SchoolBellSetup.exe --silent --server 192.168.122.1:8443 --enrollment-file C:\Deploy\schoolbell-enroll.json --ca-file C:\Deploy\school-ca.cer"
"$VX" --timeout 900 -c '
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process C:\Deploy\SchoolBellSetup.exe -ArgumentList "--silent","--server","192.168.122.1:8443","--enrollment-file","C:\Deploy\schoolbell-enroll.json","--ca-file","C:\Deploy\school-ca.cer","--log","C:\Deploy\install.log" -Wait -PassThru
"  exit code " + $p.ExitCode + " after " + [int]$sw.Elapsed.TotalSeconds + " s"'

echo "--- 3. verify the installation"
"$VX" --timeout 300 -c '
$u = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*" | ? DisplayName -eq "SchoolBell"
"  Apps & Features: " + $u.DisplayName + " " + $u.DisplayVersion + " by " + $u.Publisher + " | icon " + $u.DisplayIcon + " | size " + $u.EstimatedSize + " KB"
"  uninstall string: " + $u.UninstallString
$s = Get-CimInstance Win32_Service -Filter "Name=''SchoolBell''"
"  service: " + $s.State + ", " + $s.StartMode + ", runs as " + $s.StartName + " -> " + $s.PathName
"  recovery: " + ((sc.exe qfailure SchoolBell | Select-String "RESTART") -join "; ").Trim()
"  logon task: " + (Get-ScheduledTask SchoolBellCompanion).State + " (principal group " + (Get-ScheduledTask SchoolBellCompanion).Principal.GroupId + ")"
Start-Sleep 20
"  companion processes: " + ((Get-Process SchoolBellCompanion -ErrorAction SilentlyContinue | % { "pid " + $_.Id + " session " + $_.SessionId }) -join ", ")
"  ProgramData ACL: " + ((icacls C:\ProgramData\SchoolBell | Select -First 3) -join " | ").Trim()
"  config.json ACL: " + ((icacls C:\ProgramData\SchoolBell\config.json | Select -First 3) -join " | ").Trim()
"  CA thumbprint recorded for uninstall: " + (Get-ItemProperty HKLM:\Software\SchoolBell -ErrorAction SilentlyContinue).InstalledCaThumbprint
"  installer copy for update rollback: " + (Test-Path C:\ProgramData\SchoolBell\updates\installed-setup.exe)'
for i in $(seq 1 30); do "$LAB/api" GET '/api/devices?page_size=200' | grep -q '"connected": true' && break; sleep 3; done
"$LAB/api" GET '/api/devices?page_size=200' | python3 -c 'import sys,json; [print("  server sees:", d["label"], d["state"], "client", d.get("client_version")) for d in json.load(sys.stdin)["items"] if d["connected"]]'
S0=$(stat -c %s "$A"); "$LAB/api" POST /api/bells/ring '{"target":{"all":true},"bell_name":"after installer"}' >/dev/null; sleep 6
"$REPO/scripts/lab/wavanalyze.py" "$A" --from-byte "$S0" | sed 's/^/  /'

echo "--- 4. repair: run the same installer again (identity must be kept)"
DID1=$("$VX" -c '(Get-Content C:\ProgramData\SchoolBell\config.json -Raw | ConvertFrom-Json).device_id')
"$VX" --timeout 900 -c '$p = Start-Process C:\Deploy\SchoolBellSetup.exe -ArgumentList "--silent" -Wait -PassThru; "  repair exit code " + $p.ExitCode; "  service: " + (Get-Service SchoolBell).Status'
DID2=$("$VX" -c '(Get-Content C:\ProgramData\SchoolBell\config.json -Raw | ConvertFrom-Json).device_id')
echo "  device id before $DID1 after $DID2 ($( [ "$DID1" = "$DID2" ] && echo kept || echo CHANGED))"

echo "--- 5. uninstall via the Apps & Features uninstall command (silent)"
"$VX" --timeout 600 -c '
$u = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*" | ? DisplayName -eq "SchoolBell"
$exe = $u.UninstallString.Trim([char]34)
$p = Start-Process $exe -ArgumentList "/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART" -Wait -PassThru
"  uninstall exit code " + $p.ExitCode
Start-Sleep 5
"  after: Apps entry=" + [bool](Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*" | ? DisplayName -eq "SchoolBell") + " service=" + [bool](Get-Service SchoolBell -ErrorAction SilentlyContinue) + " task=" + [bool](Get-ScheduledTask SchoolBellCompanion -ErrorAction SilentlyContinue)
"  after: program files=" + (Test-Path "C:\Program Files\SchoolBell") + " programdata=" + (Test-Path C:\ProgramData\SchoolBell) + " user logs=" + (Test-Path C:\Users\User\AppData\Local\SchoolBell) + " companion running=" + [bool](Get-Process SchoolBellCompanion -ErrorAction SilentlyContinue)
"  after: school CA still trusted=" + [bool](Get-ChildItem Cert:\LocalMachine\Root | ? Subject -like "*Caddy Local Authority*") + " registry key=" + (Test-Path HKLM:\Software\SchoolBell) + " event source=" + [System.Diagnostics.EventLog]::SourceExists("SchoolBell")'

echo "--- 6. reinstall for further lab tests"
"$LAB/api" --out "$SH/Deploy/schoolbell-enroll.json" POST /api/admin/enrollment-file '{"label":"installer test reinstall","max_uses":1,"expires_hours":4}' >/dev/null
"$VX" --timeout 900 -c 'Copy-Item Z:\Deploy\schoolbell-enroll.json C:\Deploy\ -Force; $p = Start-Process C:\Deploy\SchoolBellSetup.exe -ArgumentList "--silent","--server","192.168.122.1:8443","--enrollment-file","C:\Deploy\schoolbell-enroll.json","--ca-file","C:\Deploy\school-ca.cer" -Wait -PassThru; "  reinstall exit code " + $p.ExitCode'
