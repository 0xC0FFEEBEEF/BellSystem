#!/usr/bin/env bash
# Ubuntu 24.04 ships glibc 2.39. Reject binaries that require anything newer.
set -euo pipefail
DIR=${1:?usage: verify-whisper-portability.sh WHISPER_DIR}
LIMIT=2.39

mapfile -t FILES < <(find "$DIR/bin" -maxdepth 1 -type f -print)
[ ${#FILES[@]} -gt 0 ] || { echo "no Whisper binaries found in $DIR/bin" >&2; exit 1; }

for file in "${FILES[@]}"; do
  readelf -h "$file" >/dev/null 2>&1 || continue
  required=$(readelf --version-info "$file" 2>/dev/null | grep -oE 'GLIBC_[0-9.]+' | cut -d_ -f2 | sort -Vu | tail -1)
  [ -z "$required" ] && continue
  highest=$(printf '%s\n%s\n' "$LIMIT" "$required" | sort -V | tail -1)
  if [ "$highest" != "$LIMIT" ]; then
    echo "$file requires GLIBC_$required; Ubuntu 24.04 supports through GLIBC_$LIMIT" >&2
    exit 1
  fi
done

echo "Whisper binaries are compatible with Ubuntu 24.04 (GLIBC <= $LIMIT)"
