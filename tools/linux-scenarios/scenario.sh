#!/usr/bin/env bash
# Shared helpers for the Linux runtime scenarios. Source this file.
#   source scenario.sh; fresh_data NAME [with-models]; run_app NAME [args...]
# Every scenario gets its own data folder under RUNS/NAME (single-instance
# lock is per data folder), the models hard-linked in (instant, no copy).
#
# All optional:
#   BIN                  binary under test; default releases/Morphonic-<csproj version>-linux-x64
#   MODELS_DATA          a data folder the app filled with `--fetch-models --data-dir`
#                        (models/ and voices/ inside); default $MORPHONIC_SCENARIOS/models-data
#   MORPHONIC_SCENARIOS  where start-audio.sh writes env.sh and the runs go;
#                        default ${TMPDIR:-/tmp}/morphonic-scenarios
#
# Keep MODELS_DATA and MORPHONIC_SCENARIOS on one file system. Across two,
# every run folder gets a full copy of the models (850 MB) instead of hard
# links, and where /tmp is a RAM-backed tmpfs (Fedora) the 28 scenarios fill
# it. The simple choice there: both under $HOME.
set -u
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
export MORPHONIC_SCENARIOS="${MORPHONIC_SCENARIOS:-${TMPDIR:-/tmp}/morphonic-scenarios}"
[ -f "$MORPHONIC_SCENARIOS/env.sh" ] && source "$MORPHONIC_SCENARIOS/env.sh"
VER="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$REPO/src/Morphonic/Morphonic.csproj" | head -n 1)"
export BIN="${BIN:-$REPO/releases/Morphonic-$VER-linux-x64}"
export MODELS_DATA="${MODELS_DATA:-$MORPHONIC_SCENARIOS/models-data}"
export MODELS="$MODELS_DATA/models"
export VOICES="$MODELS_DATA/voices"
export RUNS="$MORPHONIC_SCENARIOS/runs"
mkdir -p "$RUNS"

# hard links when the models sit on the same file system, copies otherwise
link_in() { cp -l "$@" 2>/dev/null || cp "$@"; }

# fresh_data NAME [with-models] -> echoes the data dir
fresh_data() {
  local name="$1" with="${2:-}"
  local d="$RUNS/$name/data"
  rm -rf "$RUNS/$name"; mkdir -p "$d"
  if [ "$with" = "with-models" ]; then
    mkdir -p "$d/models" "$d/voices"
    link_in "$MODELS/contentvec-768-layer12.onnx" "$MODELS/rmvpe.onnx" "$MODELS/models_manifest.json" "$d/models/"
    link_in "$VOICES/sample-voice-40k.onnx" "$d/voices/"
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
