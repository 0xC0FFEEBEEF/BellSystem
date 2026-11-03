#!/usr/bin/env bash
# publish.sh — build deploy artifacts into dist/publish/ (gitignored).
# Server: linux-x64 self-contained single file (no runtime needed on target).
# Clients: win-x64 self-contained single file (no .NET runtime needed on classrooms).
# Run from the repo root: bash build/publish.sh
set -u
export PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$(dirname "$0")/.."
fail=0; summary=""
step() {
  local name=$1; shift
  echo "===== $name"
  "$@" 2>&1 | tail -5; local rc=${PIPESTATUS[0]}
  if [ $rc -eq 0 ]; then summary+="PASS  $name"$'\n'; else summary+="FAIL  $name (exit $rc)"$'\n'; fail=1; fi
}

step server   dotnet publish src/Server/Server.csproj -c Release -r linux-x64 --self-contained true \
               -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
               -p:AssemblyName=SchoolBellServer -m:1 -nodeReuse:false -o dist/publish/server
step service  dotnet publish src/Client.Service/Client.Service.csproj -c Release -r win-x64 --self-contained true \
               -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
               -p:AssemblyName=SchoolBellService -m:1 -nodeReuse:false -o dist/publish/client-service
step companion dotnet publish src/Client.Desktop/Client.Desktop.csproj -c Release -r win-x64 --self-contained true \
               -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
               -m:1 -nodeReuse:false -o dist/publish/client-companion
step enroll   dotnet publish tools/EnrollTool/EnrollTool.csproj -c Release -r win-x64 --self-contained true \
               -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
               -m:1 -nodeReuse:false -o dist/publish/enroll-tool

printf '\n===== summary\n%s' "$summary"
[ -d dist/publish/server ] && ls -l dist/publish/server | head -5
exit $fail
