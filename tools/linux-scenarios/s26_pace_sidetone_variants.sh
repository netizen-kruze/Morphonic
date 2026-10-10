#!/usr/bin/env bash
# Settings-driven branches: the pace advice for small blocks, sidetone to a
# named device and off, and a saved device that is gone.
source "$(dirname "$0")/_common.sh"
# pace advice: 160 ms blocks with the longest context on the CPU. What a
# machine is told depends on the machine, so the checks follow the session's
# own log: after ten seconds of falling behind it must be told what helps
# here — GPU acceleration where a usable card has no pack installed, else
# "A larger block (250 ms)" — and one that keeps up (or dips only briefly)
# must be told nothing. The 4-thread box these were written on falls behind;
# a fast desktop keeps up unless something else is using it.
N=s26_pace; D="$(fresh_data $N with-models)"; live_settings "$D" false 160 2500
run_app $N --auto-start --run-seconds 60; code=$?
show_logs $N
check "pace: exit 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "pace: session summary with passes" "$(has_log $N 'voice session ended \(exiting\) .*[1-9][0-9]* passes')"
if [ "$(has_log $N 'pace advice')" = 1 ]; then
  if grep -q 'tier GPU' "$D/last_boot.log" && grep -q 'pack not installed' "$D/last_boot.log"; then
    want='install GPU acceleration'; what="GPU acceleration (a usable card, no pack installed)"
  else
    want='A larger block \(250 ms\)'; what="the next block size (250 ms)"
  fi
  echo "  (this machine fell behind at 160 ms blocks: $(grep -o 'peak load [0-9.]*×' "$D/last_boot.log" | tail -n 1))"
  check "pace: the advice came after falling behind was logged" "$(grep -B99 'pace advice' "$D/last_boot.log" | grep -q 'conversion falling behind' && echo 1 || echo 0)"
  check "pace: the advice offers $what" "$(has_log $N "pace advice: .*$want")"
else
  echo "  (this machine kept up at 160 ms blocks, or dipped only briefly: $(grep -o 'peak load [0-9.]*×' "$D/last_boot.log" | tail -n 1); the advice itself was not exercised)"
  check "pace: no advice was given" "$([ "$(has_log $N 'pace advice')" = 0 ] && echo 1 || echo 0)"
  check "pace: nothing in error.log" "$(errlog_empty $N)"
fi
# sidetone to a named device, with the virtual mic on
N=s26_side; D="$(fresh_data $N with-models)"; live_settings "$D" true 500 1000
python3 - "$D/settings.json" <<'PY'
import json,sys; p=sys.argv[1]; s=json.load(open(p)); s["MonitorDeviceIndex"]=1; s["MonitorDeviceName"]="Test-Speakers"; json.dump(s,open(p,"w"))
PY
run_app $N --auto-start --run-seconds 45; code=$?
show_logs $N
check "sidetone device: exit 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "sidetone device: named device, not auto" "$([ "$(has_log $N 'sidetone: pw-play.*Test-Speakers')" = 1 ] && ! grep -q 'sidetone: .*(auto)' "$D/last_boot.log" && echo 1 || echo 0)"
# sidetone off while the output is the virtual mic
N=s26_off; D="$(fresh_data $N with-models)"; live_settings "$D" true 500 1000
python3 - "$D/settings.json" <<'PY'
import json,sys; p=sys.argv[1]; s=json.load(open(p)); s["SidetoneAuto"]=False; json.dump(s,open(p,"w"))
PY
run_app $N --auto-start --run-seconds 45; code=$?
show_logs $N
check "sidetone off: exit 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "sidetone off: logged off with the virtual sink as output" "$(has_log $N 'out: pw-play.*Morphonic-Voice; sidetone: off')"
# a saved device that is gone resolves to the default
N=s26_gone; D="$(fresh_data $N with-models)"; live_settings "$D" false 500 1000
python3 - "$D/settings.json" <<'PY'
import json,sys; p=sys.argv[1]; s=json.load(open(p)); s["InputDeviceIndex"]=1; s["InputDeviceName"]="Unplugged USB Mic"; s["OutputDeviceIndex"]=1; s["OutputDeviceName"]="Test-Speakers"; json.dump(s,open(p,"w"))
PY
run_app $N --auto-start --run-seconds 45; code=$?
show_logs $N
check "gone device: exit 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "gone device: input fell back to the default, output resolved by name" "$(has_log $N 'voice started:.*in: pw-record[^;]*\(default input\).*out: pw-play.*Test-Speakers')"
# locale: InvariantGlobalization keeps numbers with a dot under de_DE
N=s26_locale; D="$(fresh_data $N with-models)"; live_settings "$D" false 500 1000
LC_ALL=de_DE.UTF-8 LANG=de_DE.UTF-8 "$BIN" --bench --data-dir "$D" >"$RUNS/$N/bench.txt" 2>&1
check "locale: bench.log keeps a decimal point under de_DE" "$(grep -qE 'load [0-9]+\.[0-9]{2}×' "$D/bench.log" && echo 1 || echo 0)"
finish s26_variants
