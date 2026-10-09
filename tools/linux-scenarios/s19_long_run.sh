#!/usr/bin/env bash
# A 150 s session with the virtual microphone and sidetone on: memory, passes,
# pace lines, nothing in error.log.
source "$(dirname "$0")/_common.sh"
N=s19_long; D="$(fresh_data $N with-models)"; live_settings "$D" true 500 1000
run_app $N --auto-start --run-seconds 150; code=$?
show_logs $N
check "exit 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "session ran to the end (summary with passes)" "$(has_log $N 'voice session ended \\(exiting\\) after 2\\.[0-9] min .*[1-9][0-9]* passes')"
check "virtual mic removal logged at exit" "$(has_log $N 'virtual mic: removed')"
check "no error.log entries" "$(errlog_empty $N)"
check "virtual mic created and removed" "$( [ "$(has_log $N 'virtual mic: created')" = 1 ] && ! pactl list short sinks | grep -q morphonic_voice && echo 1 || echo 0)"
rss=$(sed -n 's/.*rss \([0-9]*\) MB.*/\1/p' "$D/last_boot.log" | tail -1); echo "rss at end: ${rss:-?} MB"
check "resident memory under 2.5 GB at the end" "$([ -n "$rss" ] && [ "$rss" -lt 2500 ] && echo 1 || echo 0)"
finish $N
