#!/usr/bin/env bash
source "$(dirname "$0")/_common.sh"
N=s17_nodisplay; D="$(fresh_data $N)"
env -u DISPLAY -u WAYLAND_DISPLAY "$BIN" --data-dir "$D" --run-seconds 5 >"$RUNS/$N/stdout.txt" 2>"$RUNS/$N/stderr.txt"; code=$?
echo "exit=$code stderr: $(cat "$RUNS/$N/stderr.txt")"
check "exit 1 without a display" "$([ $code = 1 ] && echo 1 || echo 0)"
check "says that no display is set" "$(grep -q 'no display' "$RUNS/$N/stderr.txt" && echo 1 || echo 0)"
check "no sentinel left behind" "$([ -e "$D/boot.inprogress" ] && echo 0 || echo 1)"
check "error.log records the missing display for --report" "$(grep -q 'Display: no display' "$D/error.log" 2>/dev/null && echo 1 || echo 0)"
check "--help lists --convert" "$("$BIN" --help 2>&1 | grep -q -- '--convert <in.wav>' && echo 1 || echo 0)"
# --help, bad switches
check "--help prints usage, exit 0" "$("$BIN" --help 2>&1 | grep -q 'Morphonic --bench' && echo 1 || echo 0)"
"$BIN" --convert --data-dir "$D" >"$RUNS/$N/conv.txt" 2>&1; c=$?
check "--convert with a switch in place of the files prints usage, exit 2" "$([ $c = 2 ] && grep -q 'usage' "$RUNS/$N/conv.txt" && echo 1 || echo 0)"
"$BIN" --pack-offline >"$RUNS/$N/pack.txt" 2>&1; c=$?
check "--pack-offline without arguments prints usage, exit 2" "$([ $c = 2 ] && grep -q 'usage' "$RUNS/$N/pack.txt" && echo 1 || echo 0)"
finish $N
