#!/usr/bin/env bash
source "$(dirname "$0")/_common.sh"
N=s15_sentinel; D="$(fresh_data $N with-models)"; live_settings "$D" false 500 1000
echo "garbage" > "$D/boot.inprogress"
run_app $N --auto-start --run-seconds 15; code=$?
show_logs $N
check "exit 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "previous unfinished start reported in last_boot.log" "$(has_log $N 'never reached the window')"
check "error.log has the PreviousStart record" "$(grep -q 'PreviousStart' "$D/error.log" && echo 1 || echo 0)"
check "--auto-start ignored on a safe boot" "$( [ "$(has_log $N 'auto-start requested')" = 0 ] && echo 1 || echo 0)"
check "sentinel cleared after the page connected" "$([ -e "$D/boot.inprogress" ] && echo 0 || echo 1)"
run_app $N --auto-start --run-seconds 15; code=$?
check "next start is normal (exit 0, auto-start honored)" "$([ $code = 0 ] && has_log $N 'auto-start requested')"
check "next start has no sentinel warning" "$( [ "$(has_log $N 'never reached the window')" = 0 ] && echo 1 || echo 0)"
finish $N
