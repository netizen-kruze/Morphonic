#!/usr/bin/env bash
source "$(dirname "$0")/_common.sh"
cd "$REPO"
out="$(timeout 180 bash tools/smoke.sh "$BIN" "$MODELS" 2>&1)"; code=$?
echo "$out" | grep -E "^\s+\[(PASS|FAIL)\]|SMOKE TEST|voice started|in: |session ended"
check "smoke.sh with models exits 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "reports SMOKE TEST PASSED" "$(echo "$out" | grep -q 'SMOKE TEST PASSED' && echo 1 || echo 0)"
check "capture ran through pw-record (PipeWire path, not the PulseAudio fallback)" "$(echo "$out" | grep -q 'in: pw-record' && echo 1 || echo 0)"
check "playback ran through pw-play" "$(echo "$out" | grep -q 'out: pw-play' && echo 1 || echo 0)"
finish s02_smoke_models
