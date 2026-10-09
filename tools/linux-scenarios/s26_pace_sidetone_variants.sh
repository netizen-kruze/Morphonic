#!/usr/bin/env bash
# Settings-driven branches: the pace advice for small blocks, sidetone to a
# named device and off, and a saved device that is gone.
source "$(dirname "$0")/_common.sh"
# pace advice: 160 ms blocks on this slow box -> "A larger block (250 ms)"
N=s26_pace; D="$(fresh_data $N with-models)"; live_settings "$D" false 160 2500
run_app $N --auto-start --run-seconds 60; code=$?
show_logs $N
check "pace: exit 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "pace: falling behind logged" "$(has_log $N 'conversion falling behind')"
check "pace: advice offers the next block size (250 ms)" "$(has_log $N 'pace advice: .*A larger block \(250 ms\)')"
check "pace: backlog skipping logged in the summary" "$(has_log $N 'voice session ended .* [1-9][0-9]* ms skipped')"
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
