# sourced by every scenario: helpers + a tiny check framework
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../env" && pwd)/scenario.sh"
REPO=/home/user/Morphonic
FAILS=0
check() { if [ "$2" = 1 ]; then echo "  [PASS] $1"; else echo "  [FAIL] $1"; FAILS=$((FAILS + 1)); fi; }
has_log() { grep -q -E -- "$2" "$RUNS/$1/data/last_boot.log" 2>/dev/null && echo 1 || echo 0; }
has_file() { [ -e "$1" ] && echo 1 || echo 0; }
errlog_empty() { [ ! -s "$RUNS/$1/data/error.log" ] && echo 1 || echo 0; }
live_settings() { # live_settings DATA VIRTUALMIC(true|false) [BLOCK] [EXTRA]
  echo "{ \"VoiceId\": \"sample-voice-40k.onnx\", \"Acceleration\": \"cpu\", \"PitchSemitones\": 3, \"VirtualMic\": $2, \"BlockMs\": ${3:-500}, \"ExtraMs\": ${4:-1000} }" > "$1/settings.json"
}
finish() { echo; echo "SCENARIO $1: $([ $FAILS = 0 ] && echo PASSED || echo "FAILED ($FAILS)")"; }
