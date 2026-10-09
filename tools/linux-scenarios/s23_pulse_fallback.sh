#!/usr/bin/env bash
# A PulseAudio-only desktop: pw-record / pw-play / pw-dump are not there, so
# the app must list devices with pactl and run on parec / pacat.
source "$(dirname "$0")/_common.sh"
N=s23_pulse; D="$(fresh_data $N with-models)"; live_settings "$D" false 500 1000
SHIM="$RUNS/$N/shim"; mkdir -p "$SHIM"
for t in pw-record pw-play pw-dump; do printf '#!/bin/sh\nexit 127\n' > "$SHIM/$t"; chmod +x "$SHIM/$t"; done
PATH="$SHIM:$PATH" run_app $N --auto-start --run-seconds 60; code=$?
show_logs $N
check "exit 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "voice started on parec / pacat" "$(has_log $N 'voice started:.*in: parec.*out: pacat')"
check "devices listed through pactl (one input, one output)" "$(has_log $N 'microphone: .*\(1 input device\(s\) listed\)')"
check "session summary with passes" "$(has_log $N 'voice session ended \(exiting\) .*[1-9][0-9]* passes')"
check "no error.log entries" "$(errlog_empty $N)"
PATH="$SHIM:$PATH" "$BIN" --report --data-dir "$D" >"$RUNS/$N/report.txt" 2>&1
zip="$(sed -n 's/^report written: //p' "$RUNS/$N/report.txt")"
check "--report lists the pactl devices" "$(python3 -c "import zipfile; s=zipfile.ZipFile('$zip').read('system.txt').decode(); print(1 if 'TestSpeakers' in s and 'TestMic' in s and 'pw-record' in s else 0)")"
finish $N
