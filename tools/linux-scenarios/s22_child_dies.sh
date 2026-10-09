#!/usr/bin/env bash
# The capture tool, then the playback tool, is killed mid-session: the app
# must end the session with the right reason and keep running.
source "$(dirname "$0")/_common.sh"
for which in pw-record pw-play; do
  N=s22_$which; D="$(fresh_data $N with-models)"; live_settings "$D" false 500 1000
  start_app $N --auto-start --run-seconds 150
  wait_for $N 'voice started' 70 || echo "(voice did not start within 70 s)"
  sleep 6
  child=$(pgrep -P $APP_PID -x $which | head -1); echo "killing $which pid $child"
  kill -9 "$child"
  sleep 10
  alive=$(kill -0 $APP_PID 2>/dev/null && echo 1 || echo 0)
  kill -TERM $APP_PID; wait $APP_PID; code=$?
  show_logs $N
  check "$which killed: app kept running" "$alive"
  if [ $which = pw-record ]; then
    check "$which killed: session ended with 'microphone capture failed … exit code 137'" "$(has_log $N 'voice session ended \(microphone capture failed.*exit code 137')"
  else
    check "$which killed: session ended with 'playback failed'" "$(has_log $N 'voice session ended \(playback failed')"
  fi
  check "$which killed: error.log has the Session entry, nothing unhandled" "$(grep -q '^\[.*\] Session: ' "$D/error.log" 2>/dev/null && ! grep -q 'Unhandled' "$D/error.log" 2>/dev/null && echo 1 || echo 0)"
  check "$which killed: clean exit afterwards" "$([ $code = 0 ] && echo 1 || echo 0)"
  check "$which killed: no stray child left" "$(pgrep -x pw-record >/dev/null || pgrep -x pw-play >/dev/null; [ $? = 0 ] && echo 0 || echo 1)"
done
# the microphone goes quiet (the recorder is stopped, not killed): the 5 s no-data probe ends the session
N=s22_quiet; D="$(fresh_data $N with-models)"; live_settings "$D" false 500 1000
start_app $N --auto-start --run-seconds 150
wait_for $N 'voice started' 70 || echo "(voice did not start within 70 s)"
sleep 6
child=$(pgrep -P $APP_PID -x pw-record | head -1); kill -STOP "$child"; echo "stopped pw-record $child"
sleep 14
kill -CONT "$child" 2>/dev/null
alive=$(kill -0 $APP_PID 2>/dev/null && echo 1 || echo 0)
kill -TERM $APP_PID; wait $APP_PID; code=$?
show_logs $N
check "recorder frozen: app kept running" "$alive"
check "recorder frozen: session ended with 'delivered no audio for 5 s'" "$(has_log $N 'voice session ended \(microphone capture failed.*delivered no audio')"
check "recorder frozen: clean exit afterwards" "$([ $code = 0 ] && echo 1 || echo 0)"
finish s22_child_dies
