#!/usr/bin/env bash
# A sink left behind by a crashed run is adopted and still removed at exit.
source "$(dirname "$0")/_common.sh"
N=s05_stale; D="$(fresh_data $N)"; echo '{ "VirtualMic": true }' > "$D/settings.json"
pactl load-module module-null-sink sink_name=morphonic_voice sink_properties=device.description=Morphonic-Voice >/dev/null
pactl load-module module-remap-source master=morphonic_voice.monitor source_name=morphonic_mic source_properties=device.description=Morphonic-Voice-Mic >/dev/null
echo "--- modules before"; pactl list short modules | grep morphonic
run_app $N --run-seconds 15; code=$?
show_logs $N
check "exit 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "stale sink adopted with its module ids" "$(has_log $N 'virtual mic: present \(adopted modules [0-9]+, [0-9]+\)')"
check "sink removed at exit" "$(pactl list short sinks | grep -q morphonic_voice && echo 0 || echo 1)"
check "source removed at exit" "$(pactl list short sources | grep -q morphonic_mic && echo 0 || echo 1)"
check "no morphonic modules left" "$(pactl list short modules | grep -q morphonic && echo 0 || echo 1)"
pactl list short modules | grep morphonic | awk '{print $1}' | xargs -r -n1 pactl unload-module 2>/dev/null
finish $N
