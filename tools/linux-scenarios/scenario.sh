#!/usr/bin/env bash
# Shared helpers for the Linux runtime scenarios. Source this file.
#   source scenario.sh; fresh_data NAME [with-models]; run_app NAME [args...]
# Every scenario gets its own data folder under RUNS/NAME (single-instance
# lock is per data folder), the models hard-linked in (instant, no copy).
set -u
ENVDIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$ENVDIR/env.sh"
SCRATCH="$(cd "$ENVDIR/.." && pwd)"
export BIN="${BIN:-/home/user/Morphonic/releases/Morphonic-1.0.1-linux-x64}"
export MODELS="$SCRATCH/release/models-data/models"
export VOICES="$SCRATCH/release/models-data/voices"
export RUNS="$SCRATCH/runs"
mkdir -p "$RUNS"

# fresh_data NAME [with-models] -> echoes the data dir
fresh_data() {
  local name="$1" with="${2:-}"
  local d="$RUNS/$name/data"
  rm -rf "$RUNS/$name"; mkdir -p "$d"
  if [ "$with" = "with-models" ]; then
    mkdir -p "$d/models" "$d/voices"
    cp -l "$MODELS/contentvec-768-layer12.onnx" "$MODELS/rmvpe.onnx" "$MODELS/models_manifest.json" "$d/models/"
    cp -l "$VOICES/sample-voice-40k.onnx" "$d/voices/"
    echo '{ "VoiceId": "sample-voice-40k.onnx", "Acceleration": "cpu", "PitchSemitones": 3, "VirtualMic": false }' > "$d/settings.json"
  fi
  echo "$d"
}

# run_app NAME [args...]: runs the binary with --data-dir RUNS/NAME/data in the
# foreground, stdout/stderr to RUNS/NAME/{stdout,stderr}.txt, prints the exit code.
run_app() {
  local name="$1"; shift
  local d="$RUNS/$name/data"
  "$BIN" --data-dir "$d" "$@" >"$RUNS/$name/stdout.txt" 2>"$RUNS/$name/stderr.txt"
  local code=$?
  echo "exit=$code"
  return $code
}

# start_app NAME [args...]: same, in the background; sets APP_PID
start_app() {
  local name="$1"; shift
  local d="$RUNS/$name/data"
  # job control on: a plain '&' from a script starts the child with SIGINT ignored
  set -m
  "$BIN" --data-dir "$d" "$@" >"$RUNS/$name/stdout.txt" 2>"$RUNS/$name/stderr.txt" &
  APP_PID=$!
  set +m
  echo "pid=$APP_PID"
}

# wait_for NAME 'regex' SECONDS: waits until last_boot.log matches
wait_for() {
  local name="$1" re="$2" secs="${3:-30}"
  local log="$RUNS/$name/data/last_boot.log"
  for _ in $(seq 1 "$secs"); do
    if [ -f "$log" ] && grep -q -E -- "$re" "$log"; then return 0; fi
    sleep 1
  done
  return 1
}

show_logs() {
  local name="$1"
  echo "--- last_boot.log"; cat "$RUNS/$name/data/last_boot.log" 2>/dev/null
  echo "--- error.log"; cat "$RUNS/$name/data/error.log" 2>/dev/null || echo "(none)"
  echo "--- stderr (tail)"; tail -n 15 "$RUNS/$name/stderr.txt" 2>/dev/null
}
