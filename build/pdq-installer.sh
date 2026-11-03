#!/usr/bin/env bash
# Build a school-specific EXE from already published Windows binaries.
# Usage: bash build/pdq-installer.sh VERSION VM KIT_DIR [APP_DIR]
# Server address remains a deployment-time --server argument.
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION=${1:?version required}
VM=${2:?VM required}
KIT=${3:?directory containing schoolbell-enroll.json and school-ca.cer required}
APP=${4:-dist/windows/app}
STAGE=.build/pdq-stage
mkdir -p "$STAGE/provisioning"
rm -rf "$STAGE/app"
cp -a "$APP" "$STAGE/app"
cp build/release-signing.pub.pem "$STAGE/"
cp "$KIT/school-ca.cer" "$STAGE/provisioning/"
# Enrollment exports normally contain a server URL. Omit it from this payload:
# this EXE must use the address selected by the deployment administrator.
python3 - "$KIT/schoolbell-enroll.json" "$STAGE/provisioning/schoolbell-enroll.json" <<'PY'
import json, sys
with open(sys.argv[1]) as source:
    enrollment = json.load(source)
enrollment.pop('server', None)
with open(sys.argv[2], 'w') as target:
    json.dump(enrollment, target)
PY
bash scripts/lab/iscc-vm.sh "$VM" "$VERSION" "$STAGE" .build/pdq-output
cp .build/pdq-output/SchoolBellSetup.exe "$KIT/SchoolBellSetup.exe"
