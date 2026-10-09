#!/usr/bin/env bash
# Release smoke test (Linux) — run on ANY desktop, including one without a
# microphone or any model installed.
#
# Boots the Morphonic binary against a throwaway data folder, lets it exit on
# its own (--run-seconds), then checks that the page connected, the
# settings were read, the hardware and component lines were written, and
# nothing crashed. With a models folder as the second argument (holding
# contentvec-768-layer12.onnx, rmvpe.onnx and sample-voice-40k.onnx) it
# also starts the voice on the default PipeWire devices (--auto-start) and
# checks the session summary. Nothing touches the real ~/.local/share/Morphonic.
#
#   tools/smoke.sh                                   # after build.sh (uses publish-linux/Morphonic)
#   tools/smoke.sh ~/Downloads/Morphonic-1.0.0-linux-x64
#   tools/smoke.sh <binary> ~/.local/share/Morphonic/models   # full run with the voice
#
# Needs a display (a desktop session, or run it under xvfb-run).
set -u
EXE="${1:-$(dirname "$0")/../publish-linux/Morphonic}"
MODELS="${2:-}"
WAIT=12

if [ ! -f "$EXE" ]; then echo "binary not found: $EXE"; exit 2; fi
if [ ! -x "$EXE" ]; then echo "not executable: $EXE  (fix: chmod +x \"$EXE\")"; exit 2; fi
EXE="$(readlink -f "$EXE")"
if pgrep -x "$(basename "$EXE" | cut -c1-15)" >/dev/null 2>&1; then
  echo "Morphonic is already running - close it first (single instance)."; exit 2
fi
if [ -z "${WAYLAND_DISPLAY:-}${DISPLAY:-}" ]; then
  echo "no display (WAYLAND_DISPLAY/DISPLAY unset) - run from a desktop session, or: xvfb-run -a $0 $EXE"; exit 2
fi

ROOT="$(mktemp -d "${TMPDIR:-/tmp}/morphonic-smoke-XXXXXX")"
DATA="$ROOT/data"
mkdir -p "$DATA"
FULL=0
if [ -n "$MODELS" ]; then
  mkdir -p "$DATA/models" "$DATA/voices"
  cp "$MODELS/contentvec-768-layer12.onnx" "$MODELS/rmvpe.onnx" "$DATA/models/"
  if [ -f "$MODELS/sample-voice-40k.onnx" ]; then cp "$MODELS/sample-voice-40k.onnx" "$DATA/voices/"; else cp "$MODELS/../voices/sample-voice-40k.onnx" "$DATA/voices/"; fi
  echo '{ "VoiceId": "sample-voice-40k.onnx", "Acceleration": "cpu", "PitchSemitones": 3, "VirtualMic": false }' > "$DATA/settings.json"
  FULL=1
else
  echo '{ "PitchSemitones": 3, "VirtualMic": false }' > "$DATA/settings.json"
fi

T0="$(date '+%Y-%m-%d %H:%M:%S')"
ARGS=(--data-dir "$DATA" --run-seconds "$WAIT")
[ "$FULL" = 1 ] && ARGS+=(--auto-start)
"$EXE" "${ARGS[@]}" >"$ROOT/stdout.txt" 2>"$ROOT/stderr.txt" &
PID=$!
EXITED=0
for _ in $(seq 1 $((WAIT + 20))); do
  if ! kill -0 "$PID" 2>/dev/null; then EXITED=1; break; fi
  sleep 1
done
if [ "$EXITED" = 0 ]; then kill "$PID" 2>/dev/null; sleep 1; kill -9 "$PID" 2>/dev/null; fi

LOG=""; [ -f "$DATA/last_boot.log" ] && LOG="$(cat "$DATA/last_boot.log")"
ERR=""; [ -f "$DATA/error.log" ] && ERR="$(cat "$DATA/error.log")"
CRASHES=0
if command -v coredumpctl >/dev/null 2>&1; then
  CRASHES="$(coredumpctl list --no-pager --no-legend --since="$T0" 2>/dev/null | grep -c "$(basename "$EXE" | cut -c1-15)" || true)"
fi

fail=0
check() { if [ "$2" = 1 ]; then echo "  [PASS] $1"; else echo "  [FAIL] $1"; fail=$((fail + 1)); fi; }
has() { if printf '%s' "$LOG" | grep -q -E -- "$1"; then echo 1; else echo 0; fi; }

check "exited cleanly on --run-seconds"   "$EXITED"
check "boot log written"                  "$([ -n "$LOG" ] && echo 1 || echo 0)"
check "settings file read"                "$(has 'settings from: +settings file')"
check "page connected"                    "$(has 'page connected')"
check "hardware verdict logged"           "$(has 'gpu: ')"
check "components line logged"            "$(has 'components: ')"
check "no core dumps"                     "$([ "$CRASHES" = 0 ] && echo 1 || echo 0)"
check "no unhandled exceptions logged"    "$(printf '%s' "$ERR" | grep -q -E 'Unhandled|SessionWork|UiDispatcher|OnUiMessage|StartSession' && echo 0 || echo 1)"
if [ "$FULL" = 1 ]; then
  check "voice started on the default devices" "$(has 'voice started:')"
  check "session summary written"              "$(has 'voice session ended .* passes')"
fi

echo
echo '--- last_boot.log ---'
echo "$LOG"
if [ -n "$ERR" ]; then echo '--- error.log ---'; echo "$ERR"; fi
if [ -s "$ROOT/stderr.txt" ]; then echo '--- stderr (last 20 lines) ---'; tail -n 20 "$ROOT/stderr.txt"; fi
rm -rf "$ROOT"

if [ "$fail" -gt 0 ]; then echo "SMOKE TEST FAILED ($fail check(s))"; exit 1; fi
echo "SMOKE TEST PASSED"
exit 0
