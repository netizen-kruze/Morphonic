#!/usr/bin/env bash
# Virtual microphone on: the sink and source exist while the voice runs, the
# output is routed into the sink, sidetone Auto kicks in, and both vanish at exit.
source "$(dirname "$0")/_common.sh"
N=s04_vmic; D="$(fresh_data $N with-models)"; live_settings "$D" true 500 1000
pactl list short modules | grep -E "morphonic" && echo "WARNING: morphonic modules present before the test"
start_app $N --auto-start --run-seconds 70
wait_for $N 'voice started' 60 || echo "(voice did not start within 60 s)"
sleep 2
sinks="$(pactl list short sinks)"; sources="$(pactl list short sources)"; mods="$(pactl list short modules)"
echo "--- sinks while running"; echo "$sinks"; echo "--- sources while running"; echo "$sources"
check "morphonic_voice sink exists while running" "$(echo "$sinks" | grep -q morphonic_voice && echo 1 || echo 0)"
check "morphonic_mic source exists while running" "$(echo "$sources" | grep -q morphonic_mic && echo 1 || echo 0)"
check "pw-dump lists Morphonic-Voice-Mic as an Audio/Source" "$(pw-dump 2>/dev/null | grep -q '"node.name": "morphonic_mic"' && echo 1 || echo 0)"
wait $APP_PID; code=$?
show_logs $N
check "exit 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "virtual mic created at boot" "$(has_log $N 'virtual mic: created \(modules')"
check "output routed into the virtual sink" "$(has_log $N 'out: pw-play[^;]*→ Morphonic-Voice')"
check "sidetone auto on the default output" "$(has_log $N 'sidetone: pw-play[^;]*\(default output\) \(auto\)')"
check "session summary carries sidetone catch-ups" "$(has_log $N 'sidetone catch-ups [0-9]+')"
check "sink removed at exit" "$(pactl list short sinks | grep -q morphonic_voice && echo 0 || echo 1)"
check "source removed at exit" "$(pactl list short sources | grep -q morphonic_mic && echo 0 || echo 1)"
check "no morphonic modules left" "$(pactl list short modules | grep -q morphonic && echo 0 || echo 1)"
check "no error.log entries" "$(errlog_empty $N)"
finish $N
