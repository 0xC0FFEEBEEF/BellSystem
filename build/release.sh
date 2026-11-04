#!/usr/bin/env bash
# Build a complete, signed SchoolBell release into dist/:
#   dist/server/schoolbell-server-linux-x64.tar.gz   server bundle (self-contained .NET, office UI,
#                                                    pinned Caddy, systemd units, installer, updater,
#                                                    and the matching signed Windows client)
#   dist/server/install.sh                           copy of the bundle's installer
#   dist/windows/SchoolBellSetup.exe                 Inno Setup installer (+ app/ binaries, build log)
#   dist/release-manifest.json(.sig)                 publisher-signed list of the release files
#   dist/SHA256SUMS, dist/LICENSES.md
#
#   bash build/release.sh 0.9.0 [--iscc "wine ISCC.exe" | --iscc-vm schoolbell-lab] [--skip-installer]
#
# Signing: ECDSA P-256 key at $SCHOOLBELL_SIGNING_KEY (default ~/.config/schoolbell-release/
# release-signing.key.pem). Its public half must equal build/release-signing.pub.pem, which is what
# servers and clients pin. The private key never enters the repository or a release.
set -euo pipefail
cd "$(dirname "$0")/.."
REPO=$(pwd)
VERSION=${1:?usage: release.sh VERSION [--iscc CMD | --iscc-vm DOMAIN] [--skip-installer]}; shift
ISCC=""; ISCC_VM=""; SKIP_INSTALLER=0
while [ $# -gt 0 ]; do
  case "$1" in
    --iscc) ISCC=$2; shift 2;;
    --iscc-vm) ISCC_VM=$2; shift 2;;
    --skip-installer) SKIP_INSTALLER=1; shift;;
    *) echo "unknown option $1" >&2; exit 2;;
  esac
done
KEY=${SCHOOLBELL_SIGNING_KEY:-$HOME/.config/schoolbell-release/release-signing.key.pem}
CADDY_VERSION=2.11.7
export PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
COMMIT=$(git rev-parse HEAD)
[ -z "$(git status --porcelain)" ] || echo "WARNING: building from a dirty tree; the release records commit $COMMIT"
[ -f "$KEY" ] || { echo "signing key not found: $KEY" >&2; exit 1; }
[ "$(openssl ec -in "$KEY" -pubout 2>/dev/null)" = "$(cat build/release-signing.pub.pem)" ] || { echo "signing key does not match build/release-signing.pub.pem" >&2; exit 1; }

DIST=${RELEASE_OUT:-dist}
STAGE=$DIST/stage; rm -rf "$DIST"; mkdir -p "$STAGE" "$DIST/server" "$DIST/windows"
LOG=$DIST/build.log; : > "$LOG"
step() { echo "== $*"; }
sign() { openssl dgst -sha256 -sign "$KEY" "$1" | base64 -w0 > "$1.sig"; openssl dgst -sha256 -verify build/release-signing.pub.pem -signature <(base64 -d "$1.sig") "$1" >/dev/null; }
manifest() { # out file1:path1 ...
  local out=$1; shift
  python3 - "$VERSION" "$COMMIT" "$out" "$@" <<'PY'
import hashlib, json, os, sys, time
ver, commit, out, *pairs = sys.argv[1:]
files = {}
for p in pairs:
    src, name = p.split(":", 1)
    files[name] = {"sha256": hashlib.sha256(open(src, "rb").read()).hexdigest(), "bytes": os.path.getsize(src)}
json.dump({"product": "SchoolBell", "version": ver, "commit": commit, "created_at": int(time.time() * 1000), "files": files},
          open(out, "w"), indent=1, sort_keys=True)
PY
}

step "office web UI"
(cd web && npm ci --no-audit --no-fund >>"$REPO/$LOG" 2>&1 && npm run build >>"$REPO/$LOG" 2>&1)

step "server (linux-x64, self-contained)"
SRV=$STAGE/server/schoolbell-$VERSION
dotnet restore SchoolBell.sln --locked-mode >>"$LOG" 2>&1
dotnet publish src/Server/Server.csproj -c Release -r linux-x64 --self-contained true -p:Version="$VERSION" \
  -p:SourceRevisionId="$COMMIT" -p:DebugType=none -p:ContinuousIntegrationBuild=true -o "$SRV/app" >>"$LOG" 2>&1
cp -r web/dist "$SRV/app/wwwroot"
rm -rf "$SRV/app/config" "$SRV/app/appsettings.Development.json"

step "Windows client (win-x64, self-contained)"
WIN=$STAGE/windows
for p in src/Client.Service/Client.Service.csproj tools/EnrollTool/EnrollTool.csproj src/Client.Desktop/Client.Desktop.csproj; do
  dotnet publish "$p" -c Release -r win-x64 --self-contained true -p:Version="$VERSION" -p:SourceRevisionId="$COMMIT" \
    -p:DebugType=none -p:ContinuousIntegrationBuild=true -o "$WIN/app" >>"$LOG" 2>&1
done
cp build/release-signing.pub.pem "$WIN/release-signing.pub.pem"
rm -f "$WIN/app/appsettings.Development.json"

if [ "$SKIP_INSTALLER" = 0 ]; then
  step "Windows installer (Inno Setup)"
  if [ -n "$ISCC" ]; then
    (cd install/windows && $ISCC /DMyAppVersion="$VERSION" /DStage="..\\..\\$STAGE\\windows" SchoolBellSetup.iss) > $DIST/windows/build-installer.log 2>&1
  elif [ -n "$ISCC_VM" ]; then
    bash scripts/lab/iscc-vm.sh "$ISCC_VM" "$VERSION" "$WIN" $DIST/windows
  else
    echo "no Inno Setup compiler given (--iscc or --iscc-vm); see OPERATIONS.md" >&2; exit 1
  fi
  [ -f $DIST/windows/SchoolBellSetup.exe ] || { echo "installer was not produced; see $DIST/windows/build-installer.log" >&2; exit 1; }
  cp -r "$WIN/app" $DIST/windows/app
  step "client release (signed) inside the server bundle"
  mkdir -p "$SRV/app/client-release/windows"
  cp $DIST/windows/SchoolBellSetup.exe "$SRV/app/client-release/windows/"
  step "reusable Windows deployment launcher"
  dotnet publish tools/DeploymentBootstrap/DeploymentBootstrap.csproj -c Release -r win-x64 --self-contained true \
    -p:Version="$VERSION" -p:SetupPath="$REPO/$DIST/windows/SchoolBellSetup.exe" -p:ContinuousIntegrationBuild=true \
    -o "$DIST/deployment" >>"$LOG" 2>&1
  mkdir -p "$SRV/app/client-release/deployment"
  cp "$DIST/deployment/SchoolBellDeploy.exe" "$SRV/app/client-release/deployment/"
  manifest "$SRV/app/client-release/release-manifest.json" "$DIST/windows/SchoolBellSetup.exe:windows/SchoolBellSetup.exe" \
    "$DIST/deployment/SchoolBellDeploy.exe:deployment/SchoolBellDeploy.exe"
  sign "$SRV/app/client-release/release-manifest.json"
fi

step "Caddy $CADDY_VERSION (pinned, checksum-verified)"
CC=.build/caddy-$CADDY_VERSION; mkdir -p "$CC"
if [ ! -f "$CC/caddy" ]; then
  T=caddy_${CADDY_VERSION}_linux_amd64.tar.gz
  curl -fsSL -o "$CC/$T" "https://github.com/caddyserver/caddy/releases/download/v$CADDY_VERSION/$T"
  curl -fsSL -o "$CC/checksums.txt" "https://github.com/caddyserver/caddy/releases/download/v$CADDY_VERSION/caddy_${CADDY_VERSION}_checksums.txt"
  (cd "$CC" && grep " $T\$" checksums.txt | sha512sum -c - >/dev/null) || { echo "caddy checksum mismatch" >&2; exit 1; }
  tar -xzf "$CC/$T" -C "$CC" caddy LICENSE
fi
install -D -m 0755 "$CC/caddy" "$SRV/bin/caddy"; cp "$CC/LICENSE" "$SRV/bin/caddy.LICENSE"

step "server bundle"
cp install/server/install.sh install/server/uninstall.sh install/server/schoolbell-update.sh "$SRV/"
cp -r install/server/systemd install/server/caddy "$SRV/"
cp build/release-signing.pub.pem "$SRV/"
echo "$VERSION" > "$SRV/VERSION"
cp README.md OPERATIONS.md KNOWN-ISSUES.md LICENSES.md "$SRV/" 2>/dev/null || true
chmod 0755 "$SRV/install.sh" "$SRV/uninstall.sh" "$SRV/schoolbell-update.sh"
tar -C "$STAGE/server" --owner=0 --group=0 -czf $DIST/server/schoolbell-server-linux-x64.tar.gz "schoolbell-$VERSION"
cp install/server/install.sh $DIST/server/install.sh

step "signed release manifest + checksums"
ARGS=("$DIST/server/schoolbell-server-linux-x64.tar.gz:server/schoolbell-server-linux-x64.tar.gz" "$DIST/server/install.sh:server/install.sh")
[ -f $DIST/windows/SchoolBellSetup.exe ] && ARGS+=("$DIST/windows/SchoolBellSetup.exe:windows/SchoolBellSetup.exe")
manifest $DIST/release-manifest.json "${ARGS[@]}"
sign $DIST/release-manifest.json
cp LICENSES.md $DIST/ 2>/dev/null || true
(cd dist && find . -type f ! -name SHA256SUMS ! -path './stage/*' -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS)
rm -rf "$STAGE"
echo
echo "release $VERSION ($COMMIT) built:"
ls -la $DIST/server $DIST/windows | grep -v '^total'
echo "unsigned Authenticode: SchoolBellSetup.exe is NOT code-signed (no school certificate available); integrity comes from SHA256SUMS + release-manifest.json.sig"
