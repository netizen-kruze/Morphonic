#!/usr/bin/env bash
source "$(dirname "$0")/_common.sh"
N=s07_inst; D="$(fresh_data $N)"
start_app $N --run-seconds 25; first=$APP_PID
sleep 4
t0=$(date +%s)
"$BIN" --data-dir "$D" --run-seconds 5 >"$RUNS/$N/second.out" 2>"$RUNS/$N/second.err"; code=$?
dt=$(( $(date +%s) - t0 ))
echo "second instance: exit=$code after ${dt}s; stderr: $(cat "$RUNS/$N/second.err")"
check "second instance exits 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "second instance exits at once (< 4 s)" "$([ $dt -lt 4 ] && echo 1 || echo 0)"
check "second instance says already running" "$(grep -q 'already running' "$RUNS/$N/second.err" && echo 1 || echo 0)"
check "first instance still alive" "$(kill -0 $first 2>/dev/null && echo 1 || echo 0)"
wait $first; code=$?
show_logs $N
check "first instance exits 0 on its own" "$([ $code = 0 ] && echo 1 || echo 0)"
check "instance.lock released (a third run works)" "$(run_app $N --run-seconds 6 >/dev/null; [ $? = 0 ] && has_log $N 'page connected')"
finish $N
