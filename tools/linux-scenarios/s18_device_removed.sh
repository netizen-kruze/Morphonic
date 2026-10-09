#!/usr/bin/env bash
# The microphone, then the output, vanish mid-session (their PipeWire modules
# are unloaded): the session must end with the right reason, the app must
# keep running, and the devices are re-created for the tests that follow.
source "$(dirname "$0")/_common.sh"
recreate() {
  pactl list short sinks | grep -q TestSpeakers || pactl load-module module-null-sink sink_name=TestSpeakers sink_properties=device.description=Test-Speakers >/dev/null
  pactl list short sources | grep -q 'TestMic' || pactl load-module module-remap-source master=TestSpeakers.monitor source_name=TestMic source_properties=device.description=Test-Microphone >/dev/null
  pactl set-default-sink TestSpeakers >/dev/null 2>&1; pactl set-default-source TestMic >/dev/null 2>&1
}
for which in mic output; do
  N=s18_$which; D="$(fresh_data $N with-models)"; live_settings "$D" false 500 1000
  # pick the devices explicitly so the app opens exactly these nodes
  python3 - "$D/settings.json" "$which" <<'PY'
import json,sys
p=sys.argv[1]; s=json.load(open(p))
s["InputDeviceIndex"]=1; s["InputDeviceName"]="Test-Microphone"; s["OutputDeviceIndex"]=1; s["OutputDeviceName"]="Test-Speakers"
json.dump(s, open(p,"w"))
PY
  start_app $N --auto-start --run-seconds 150
  wait_for $N 'voice started' 70 || echo "(voice did not start within 70 s)"
  sleep 8
  if [ $which = mic ]; then
    id=$(pactl list short modules | awk '/module-remap-source/ && /source_name=TestMic/ {print $1}')
  else
    id=$(pactl list short modules | awk '/module-null-sink/ && /sink_name=TestSpeakers/ {print $1}')
  fi
  echo "unloading $which module $id"; pactl unload-module "$id"
  sleep 12
  echo "--- sinks/sources now"; pactl list short sinks; pactl list short sources
  alive=$(kill -0 $APP_PID 2>/dev/null && echo 1 || echo 0)
  kill -TERM $APP_PID; wait $APP_PID; code=$?
  show_logs $N
  check "$which removed: app kept running" "$alive"
  if [ $which = mic ]; then
    check "mic removed: session ended with 'microphone capture failed'" "$(has_log $N 'voice session ended \(microphone capture failed')"
  else
    check "output removed: session ended with 'playback failed'" "$(has_log $N 'voice session ended \(playback failed')"
  fi
  check "$which removed: the failure is in error.log (Session entry), nothing unhandled" "$(grep -q '^\[.*\] Session: ' "$D/error.log" 2>/dev/null && ! grep -q 'Unhandled' "$D/error.log" 2>/dev/null && echo 1 || echo 0)"
  check "$which removed: clean exit afterwards" "$([ $code = 0 ] && echo 1 || echo 0)"
  recreate
done
finish s18_device_removed
