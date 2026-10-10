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

# PipeWire's tools are installed, but PulseAudio is the sound server and no
# PipeWire daemon can be reached (WSLg with pipewire-utils, a desktop still
# on PulseAudio). The real tools run here and fail to connect; the lists come
# from pactl, and the voice, the virtual microphone's sink and the sidetone
# must all run on parec / pacat to the end. A pw-play (1.2 or newer) started
# without --raw would sit waiting for a sound file on its input, be taken for
# started, and end the session as soon as audio reached it.
N=s23_nodaemon; D="$(fresh_data $N with-models)"; live_settings "$D" true 500 1000
PIPEWIRE_REMOTE="$RUNS/$N/no-such-socket" run_app $N --auto-start --run-seconds 45; code=$?
show_logs $N
check "no PipeWire daemon: exit 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "no PipeWire daemon: voice on parec, into the virtual sink through pacat" "$(has_log $N 'voice started:.*in: parec.*out: pacat[^;]*morphonic_voice')"
check "no PipeWire daemon: sidetone through pacat as well" "$(has_log $N 'voice started:.*sidetone: pacat')"
check "no PipeWire daemon: the session ran to the end with passes" "$(has_log $N 'voice session ended \(exiting\) .*[1-9][0-9]* passes')"
check "no PipeWire daemon: no error.log entries" "$(errlog_empty $N)"
check "no PipeWire daemon: the virtual microphone is gone at exit" "$(pactl list short sinks | grep -q morphonic_voice && echo 0 || echo 1)"
PIPEWIRE_REMOTE="$RUNS/$N/no-such-socket" "$BIN" --report --data-dir "$D" >"$RUNS/$N/report.txt" 2>&1
zip="$(sed -n 's/^report written: //p' "$RUNS/$N/report.txt")"
check "no PipeWire daemon: --report says PulseAudio's tools are tried first" "$(python3 -c "import zipfile; s=zipfile.ZipFile('$zip').read('system.txt').decode(); print(1 if 'tried first: microphone parec' in s and 'output pacat' in s else 0)" 2>/dev/null || echo 0)"
finish s23_pulse_fallback
