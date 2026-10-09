#!/usr/bin/env bash
# "--after <pid>": the restarted process waits for its predecessor to
# release the single-instance lock instead of reporting "already running".
source "$(dirname "$0")/_common.sh"
N=s24_after; D="$(fresh_data $N)"
start_app $N --run-seconds 15; A=$APP_PID
sleep 3
"$BIN" --data-dir "$D" --after $A --run-seconds 8 >"$RUNS/$N/b.out" 2>"$RUNS/$N/b.err" &
B=$!
wait $A; ca=$?; t_a=$(date +%s)
wait $B; cb=$?; t_b=$(date +%s)
echo "A exit $ca; B exit $cb; B outlived A by $((t_b - t_a)) s; B stderr: $(cat "$RUNS/$N/b.err" | grep -v -E 'dbind|libEGL' )"
show_logs $N
check "A exits 0" "$([ $ca = 0 ] && echo 1 || echo 0)"
check "B waited for A and ran (exit 0, no 'already running')" "$([ $cb = 0 ] && ! grep -q 'already running' "$RUNS/$N/b.err" && echo 1 || echo 0)"
check "B's boot log says it connected (it is the one that wrote last)" "$(has_log $N 'page connected')"
check "B's args carry --after" "$(has_log $N "args: .*--after $A")"
finish $N
