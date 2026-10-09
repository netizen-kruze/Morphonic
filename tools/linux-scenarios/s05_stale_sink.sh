#!/usr/bin/env bash
# A virtual microphone left behind by a crashed run of this data folder
# (virtualmic.json names its modules and a dead pid) is adopted and removed
# at exit; one without that record (another program's, or another running
# Morphonic's) is used and left in place.
source "$(dirname "$0")/_common.sh"
make_modules() {
  SINK_ID=$(pactl load-module module-null-sink sink_name=morphonic_voice sink_properties=device.description=Morphonic-Voice)
  SOURCE_ID=$(pactl load-module module-remap-source master=morphonic_voice.monitor source_name=morphonic_mic source_properties=device.description=Morphonic-Voice-Mic)
  echo "--- modules made: $SINK_ID $SOURCE_ID"
}
unload_ours() { pactl list short modules | grep morphonic | awk '{print $1}' | tac | xargs -r -n1 pactl unload-module 2>/dev/null; }

# leftover of a dead run of this folder
N=s05_stale; D="$(fresh_data $N)"; echo '{ "VirtualMic": true }' > "$D/settings.json"
make_modules
sh -c 'exit 0' & DEAD=$!; wait $DEAD
echo "{\"Pid\":$DEAD,\"Modules\":[$SINK_ID,$SOURCE_ID]}" > "$D/virtualmic.json"
run_app $N --run-seconds 15; code=$?
show_logs $N
check "exit 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "leftover adopted with its module ids" "$(has_log $N "virtual mic: present \(adopted modules $SINK_ID, $SOURCE_ID of a run that did not exit cleanly\)")"
check "removal logged at exit" "$(has_log $N 'virtual mic: removed \(was: present \(adopted')"
check "sink removed at exit" "$(pactl list short sinks | grep -q morphonic_voice && echo 0 || echo 1)"
check "source removed at exit" "$(pactl list short sources | grep -q morphonic_mic && echo 0 || echo 1)"
check "no morphonic modules left" "$(pactl list short modules | grep -q morphonic && echo 0 || echo 1)"
check "virtualmic.json deleted" "$([ -e "$D/virtualmic.json" ] && echo 0 || echo 1)"
unload_ours

# the same modules without a record: used, left alone
N=s05_foreign; D="$(fresh_data $N)"; echo '{ "VirtualMic": true }' > "$D/settings.json"
make_modules
run_app $N --run-seconds 10; code=$?
show_logs $N
check "no record: exit 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "no record: present and left alone" "$(has_log $N 'virtual mic: present \(made by another program or Morphonic; left alone\)')"
check "no record: sink and source still there after exit" "$(pactl list short sinks | grep -q morphonic_voice && pactl list short sources | grep -q morphonic_mic && echo 1 || echo 0)"
unload_ours
finish s05_stale_sink
