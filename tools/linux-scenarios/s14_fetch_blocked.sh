#!/usr/bin/env bash
# Hugging Face is unreachable from this box (the proxy refuses the CONNECT):
# the download must fail with a sentence, exit 1, and leave the folder sane.
source "$(dirname "$0")/_common.sh"
N=s14_fetch; D="$(fresh_data $N)"
out="$(timeout 300 "$BIN" --fetch-models --data-dir "$D" 2>&1)"; code=$?; echo "$out" | tr '\r' '\n' | tail -12
check "--fetch-models exits 1 when the download fails" "$([ $code = 1 ] && echo 1 || echo 0)"
check "the failure is a sentence, not a stack trace" "$(echo "$out" | grep -q 'FAILED: .*download failed' && ! echo "$out" | grep -q '   at ' && echo 1 || echo 0)"
check "every component reported, nothing verified" "$(echo "$out" | grep -q 'verify: 0 file(s)' && echo 1 || echo 0)"
echo "data folder: $(ls -la "$D" "$D/models" 2>/dev/null | tr '\n' ' ')"
check "no unhandled exception in error.log" "$(grep -q 'Unhandled' "$D/error.log" 2>/dev/null && echo 0 || echo 1)"
finish $N
