#!/usr/bin/env bash
source "$(dirname "$0")/_common.sh"
N=s20_spaces; rm -rf "$RUNS/$N"; D="$RUNS/$N/data dir ü (test)"; mkdir -p "$D/models" "$D/voices"
link_in "$MODELS/contentvec-768-layer12.onnx" "$MODELS/rmvpe.onnx" "$D/models/"; link_in "$VOICES/sample-voice-40k.onnx" "$D/voices/"
live_settings "$D" false 500 1000
"$BIN" --data-dir "$D" --auto-start --run-seconds 45 >"$RUNS/$N/stdout.txt" 2>"$RUNS/$N/stderr.txt"; code=$?
echo "--- last_boot.log"; cat "$D/last_boot.log"; echo "--- error.log"; cat "$D/error.log" 2>/dev/null || echo "(none)"
check "exit 0 with spaces and non-ASCII in the data folder path" "$([ $code = 0 ] && echo 1 || echo 0)"
check "page connected" "$(grep -q 'page connected' "$D/last_boot.log" && echo 1 || echo 0)"
check "voice started" "$(grep -q 'voice started' "$D/last_boot.log" && echo 1 || echo 0)"
check "no error.log entries" "$([ ! -s "$D/error.log" ] && echo 1 || echo 0)"
out="$("$BIN" --report --data-dir "$D" 2>&1)"; echo "$out"
check "--report works there too" "$(echo "$out" | grep -q 'report written' && echo 1 || echo 0)"
finish $N
