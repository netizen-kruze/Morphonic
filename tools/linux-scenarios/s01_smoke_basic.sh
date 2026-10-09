#!/usr/bin/env bash
source "$(dirname "$0")/_common.sh"
cd "$REPO"
out="$(timeout 120 bash tools/smoke.sh "$BIN" 2>&1)"; code=$?
echo "$out" | grep -E "^\s+\[(PASS|FAIL)\]|SMOKE TEST"
check "smoke.sh without models exits 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "reports SMOKE TEST PASSED" "$(echo "$out" | grep -q 'SMOKE TEST PASSED' && echo 1 || echo 0)"
finish s01_smoke_basic
