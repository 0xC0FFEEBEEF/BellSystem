#!/usr/bin/env bash
# Compile install/windows/SchoolBellSetup.iss with Inno Setup inside the Windows lab VM.
# Usage: iscc-vm.sh DOMAIN VERSION STAGE_WINDOWS_DIR OUT_DIR
set -euo pipefail
DOM=$1; VER=$2; STAGEWIN=$3; OUT=$4
REPO=$(cd "$(dirname "$0")/../.." && pwd)
SHARE=/home/nbull/VMs/$DOM/share
[ -d "$SHARE" ] || { echo "no share for $DOM at $SHARE" >&2; exit 1; }
rm -rf "$SHARE/build"; mkdir -p "$SHARE/build/install/windows" "$SHARE/build/stage"
cp "$REPO"/install/windows/* "$SHARE/build/install/windows/"
rsync -a "$STAGEWIN/" "$SHARE/build/stage/windows/"
"$REPO/scripts/lab/vmexec.py" --domain "$DOM" --timeout 1800 -c "
\$ErrorActionPreference = 'Stop'
\$w = 'C:\\sbbuild'; if (Test-Path \$w) { Remove-Item -Recurse -Force \$w }
robocopy Z:\\build \$w /E /NFL /NDL /NJH /NJS /NP | Out-Null
\$iscc = 'C:\\Program Files (x86)\\Inno Setup 6\\ISCC.exe'
if (-not (Test-Path \$iscc)) { throw 'Inno Setup 6 is not installed in the VM' }
Set-Location \$w\\install\\windows
& \$iscc /Q /DMyAppVersion=$VER /DStage=\$w\\stage\\windows /O\$w\\out SchoolBellSetup.iss *> \$w\\iscc.log
\$rc = \$LASTEXITCODE
Copy-Item \$w\\iscc.log Z:\\build\\iscc.log -Force
if (\$rc -ne 0) { Get-Content \$w\\iscc.log -Tail 30; exit \$rc }
Copy-Item \$w\\out\\SchoolBellSetup.exe Z:\\build\\SchoolBellSetup.exe -Force
'compiled ' + (Get-Item \$w\\out\\SchoolBellSetup.exe).Length + ' bytes'
"
mkdir -p "$OUT"
cp "$SHARE/build/SchoolBellSetup.exe" "$OUT/SchoolBellSetup.exe"
cp "$SHARE/build/iscc.log" "$OUT/build-installer.log"
