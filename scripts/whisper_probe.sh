#!/usr/bin/env bash
# whisper_probe.sh — feasibility probe: build whisper.cpp on the cluster node,
# download small.en model, synthesize a real speech sample with piper TTS,
# transcribe it with whisper.cpp, verify content. Also records node capability
# facts for install.sh planning. All output logged; PASS/FAIL summary last;
# exits 1 if any step failed.
set -u
fail=0; summary=""
step() {
  local name=$1; shift
  echo "===== $name: $*"
  "$@" 2>&1; local rc=$?
  if [ $rc -eq 0 ]; then summary+="PASS  $name"$'\n'; else summary+="FAIL  $name (exit $rc)"$'\n'; fail=1; fi
}

echo "===== node capabilities"
id
uname -a
command -v git cmake make g++ python3 || true
systemctl --version 2>&1 | head -1 || echo "NO systemd"
sudo -n true 2>&1 && echo "HAS passwordless sudo" || echo "NO passwordless sudo"

step clone    git clone --depth 1 https://github.com/ggml-org/whisper.cpp .probe-whisper
step build    bash -c 'cd .probe-whisper && cmake -B build -DCMAKE_BUILD_TYPE=Release > cmake-configure.log 2>&1 && cmake --build build -j8 --target whisper-cli whisper-server 2>&1 | tail -3 && ls -l build/bin/'

step model    bash -c 'mkdir -p .probe-whisper/models && curl -sSL -o .probe-whisper/models/ggml-small.bin https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin'
step model_ok bash -c 'ls -l .probe-whisper/models/ggml-small.bin && [ $(stat -c%s .probe-whisper/models/ggml-small.bin) -gt 400000000 ]'

step tts      bash -c 'python3 -m venv .probe-tts && .probe-tts/bin/pip install -q piper-tts'
step tts_model bash -c 'mkdir -p .probe-voices && cd .probe-voices && curl -sSL -O https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/medium/en_US-lessac-medium.onnx && curl -sSL -O https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/medium/en_US-lessac-medium.onnx.json'
step synth    bash -c 'echo "The first school bell rings at eight thirty in the morning." | .probe-tts/bin/piper --model .probe-voices/en_US-lessac-medium.onnx --output_file probe.wav'
step synth_ok bash -c 'ls -l probe.wav && [ $(stat -c%s probe.wav) -gt 10000 ]'

if [ $fail -eq 0 ]; then
  step transcribe bash -c '.probe-whisper/build/bin/whisper-cli -m .probe-whisper/models/ggml-small.bin -f probe.wav -l en -t 8 -ow'
  step verify_text bash -c 'grep -iE "bell|thirty|morning" probe.wav.txt'
else
  echo "===== transcribe skipped: earlier step failed"
  fail=1; summary+="FAIL  transcribe (skipped)"$'\n'
fi

# carry the useful artifacts back (dist/ is gitignored -> not re-uploaded by later jobs)
if [ $fail -eq 0 ]; then
  step collect bash -c 'mkdir -p dist/whisper && cp .probe-whisper/build/bin/whisper-cli dist/whisper/whisper-cli && cp .probe-whisper/build/bin/whisper-server dist/whisper/whisper-server && cp .probe-whisper/models/ggml-small.bin dist/whisper/ggml-small.bin && cp probe.wav dist/whisper/probe.wav && cp probe.wav.txt dist/whisper/probe.txt'
fi

printf '\n===== summary\n%s' "$summary"
exit $fail
