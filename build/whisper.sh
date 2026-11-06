#!/usr/bin/env bash
# Build the pinned whisper.cpp loopback server and stage it with the verified small.en model.
# Usage: build/whisper.sh DESTINATION
set -euo pipefail
cd "$(dirname "$0")/.."

DEST=${1:?usage: build/whisper.sh DESTINATION}
SRC=.build/whisper/src
MODEL=.build/whisper/models/ggml-small.en.bin
BUILD=.build/whisper/build-portable
TAG=v1.9.5
COMMIT=d1be6fde11ac6e0407606b4e42fe72d34add8037
MODEL_SHA=c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d

if [ ! -f "$SRC/CMakeLists.txt" ]; then
  mkdir -p "$(dirname "$SRC")"
  git clone --filter=blob:none --depth 1 --branch "$TAG" https://github.com/ggml-org/whisper.cpp "$SRC"
fi
[ "$(git -C "$SRC" describe --tags --exact-match 2>/dev/null || true)" = "$TAG" ] || {
  echo "whisper.cpp must be the pinned $TAG source" >&2; exit 1;
}
[ "$(git -C "$SRC" rev-parse HEAD)" = "$COMMIT" ] || {
  echo "whisper.cpp $TAG source does not match pinned commit $COMMIT" >&2; exit 1;
}
if [ ! -f "$MODEL" ]; then
  mkdir -p "$(dirname "$MODEL")"
  curl -fL --retry 3 -o "$MODEL.download" \
    https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin
  echo "$MODEL_SHA  $MODEL.download" | sha256sum -c - >/dev/null || {
    rm -f "$MODEL.download"; echo "downloaded small.en model failed checksum verification" >&2; exit 1;
  }
  mv "$MODEL.download" "$MODEL"
fi
echo "$MODEL_SHA  $MODEL" | sha256sum -c - >/dev/null || {
  echo "small.en model missing or checksum mismatch: $MODEL" >&2; exit 1;
}

cmake -S "$SRC" -B "$BUILD" -DCMAKE_BUILD_TYPE=Release \
  -DWHISPER_BUILD_TESTS=OFF -DWHISPER_BUILD_EXAMPLES=ON -DWHISPER_BUILD_SERVER=ON \
  -DGGML_NATIVE=OFF -DBUILD_SHARED_LIBS=ON -DGGML_BACKEND_DL=ON \
  -DGGML_CPU_ALL_VARIANTS=ON -DGGML_OPENMP=OFF -DGGML_CUDA=OFF -DGGML_VULKAN=OFF
cmake --build "$BUILD" --target whisper-server -j "$(nproc)"

rm -rf "$DEST"
install -d "$DEST/bin" "$DEST/models"
install -m 0755 "$BUILD/bin/whisper-server" "$DEST/bin/whisper-server"
cp -a "$BUILD"/bin/*.so* "$DEST/bin/"
install -m 0644 "$MODEL" "$DEST/models/ggml-small.en.bin"
install -m 0644 "$SRC/LICENSE" "$DEST/LICENSE.whisper.cpp"

LD_LIBRARY_PATH="$DEST/bin" "$DEST/bin/whisper-server" --help >/dev/null 2>&1
echo "whisper.cpp $TAG staged in $DEST"
