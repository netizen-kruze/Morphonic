#!/usr/bin/env bash
source "$(dirname "$0")/_common.sh"
N=s10_report; D="$(fresh_data $N with-models)"
run_app $N --run-seconds 8 >/dev/null
out="$("$BIN" --report --data-dir "$D" 2>&1)"; code=$?; echo "$out"
zip="$(echo "$out" | sed -n 's/^report written: //p')"
check "--report exits 0 and prints the path" "$([ $code = 0 ] && [ -f "$zip" ] && echo 1 || echo 0)"
python3 - "$zip" <<'PY'
import sys, zipfile
z = zipfile.ZipFile(sys.argv[1]); names = z.namelist(); print("entries:", names)
s = z.read("system.txt").decode(); print(s)
ok = all(n in names for n in ["last_boot.log", "settings.json", "system.txt", "models_manifest.json"])
print("CHECKS", int(ok), int("Test-Speakers" in s), int("Test-Microphone" in s), int("pw-record" in s and "pactl" in s), int("ui libraries: all present" in s))
PY
r=($(python3 -c "
import sys,zipfile; z=zipfile.ZipFile('$zip'); n=z.namelist(); s=z.read('system.txt').decode()
print(int(all(x in n for x in ['last_boot.log','settings.json','system.txt','models_manifest.json'])), int('Test-Speakers' in s), int('Test-Microphone' in s), int('pw-record' in s and 'pactl' in s), int('ui libraries: all present' in s))"))
check "zip holds last_boot.log, settings.json, system.txt, models_manifest.json" "${r[0]}"
check "system.txt lists the PipeWire output (Test-Speakers)" "${r[1]}"
check "system.txt lists the PipeWire input (Test-Microphone)" "${r[2]}"
check "system.txt locates pw-record and pactl" "${r[3]}"
check "system.txt says the UI libraries are present" "${r[4]}"
check "the folder listing does not show the report itself half-written" "$(python3 -c "import zipfile,sys; s=zipfile.ZipFile('$zip').read('system.txt').decode(); print(0 if 'Morphonic-report-' in s.split('data folder:')[-1] else 1)")"
check "instance.lock is not left behind after a clean exit" "$([ -e "$D/instance.lock" ] && echo 0 || echo 1)"
finish $N
