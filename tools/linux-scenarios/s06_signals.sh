#!/usr/bin/env bash
# SIGTERM and SIGINT during a live session: clean exit with the session summary.
source "$(dirname "$0")/_common.sh"
for sig in TERM INT; do
  N=s06_sig$sig; D="$(fresh_data $N with-models)"; live_settings "$D" false 500 1000
  start_app $N --auto-start --run-seconds 300
  wait_for $N 'voice started' 70 || echo "(voice did not start within 70 s)"
  sleep 6
  kill -$sig $APP_PID
  for _ in $(seq 1 20); do kill -0 $APP_PID 2>/dev/null || break; sleep 1; done
  if kill -0 $APP_PID 2>/dev/null; then echo "still alive after 20 s, killing"; kill -9 $APP_PID; alive=1; else alive=0; fi
  wait $APP_PID; code=$?
  show_logs $N
  check "SIG$sig: process left within 20 s" "$([ $alive = 0 ] && echo 1 || echo 0)"
  check "SIG$sig: exit code 0" "$([ $code = 0 ] && echo 1 || echo 0)"
  check "SIG$sig: exit requested by SIG$sig logged" "$(has_log $N "exit requested by SIG$sig")"
  check "SIG$sig: session summary written" "$(has_log $N 'voice session ended \\(exiting\\)')"
  check "SIG$sig: no error.log entries" "$(errlog_empty $N)"
  check "SIG$sig: sentinel cleared (no boot.inprogress left)" "$([ -e "$D/boot.inprogress" ] && echo 0 || echo 1)"
done
finish s06_signals
