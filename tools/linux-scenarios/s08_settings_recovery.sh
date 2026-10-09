#!/usr/bin/env bash
source "$(dirname "$0")/_common.sh"
N=s08_settings; D="$(fresh_data $N)"
echo '{ "PitchSemitones": 7, "BlockMs": 350 }' > "$D/settings.json.bak"
echo '{ not json' > "$D/settings.json"
run_app $N --run-seconds 8; code=$?
show_logs $N
check "exit 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "settings restored from the backup" "$(has_log $N 'settings from: +backup')"
check "restored values in effect (+7 st, 350 ms)" "$(has_log $N 'pitch/block: +\+7 st, block 350 ms')"
check "damaged file kept as settings.json.corrupt" "$(has_file "$D/settings.json.corrupt")"
check "error.log notes the recovery" "$(grep -q 'AppSettings.Load' "$D/error.log" && echo 1 || echo 0)"
check "the good copy is written back over the damaged file" "$(python3 -c "import json,sys; json.load(open('$D/settings.json')); print(1)" 2>/dev/null || echo 0)"
check "the backup still holds the good copy" "$(python3 -c "import json,sys; json.load(open('$D/settings.json.bak')); print(1)" 2>/dev/null || echo 0)"
# and a plain first run leaves a settings file behind (the boot log needs one)
N2=s08_first; D2="$(fresh_data $N2)"; export XDG_CONFIG_HOME="$RUNS/$N2/config"; mkdir -p "$XDG_CONFIG_HOME"
run_app $N2 --run-seconds 8 >/dev/null
check "first run writes settings.json" "$(has_file "$D2/settings.json")"
check "first run: 'run before: no', then yes" "$( [ "$(has_log $N2 'run before: +no')" = 1 ] && (run_app $N2 --run-seconds 6 >/dev/null; has_log $N2 'run before: +yes') )"
finish $N
