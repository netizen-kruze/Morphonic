#!/usr/bin/env bash
# A 75 s auto-started session on the PipeWire default devices (this box is a
# slow 4-thread Xeon: 500 ms blocks, 1 s context). Judged on robustness and
# honest logging, not on real-time pace.
source "$(dirname "$0")/_common.sh"
N=s03_live; D="$(fresh_data $N with-models)"; live_settings "$D" false 500 1000
run_app $N --auto-start --run-seconds 75; code=$?
show_logs $N
check "exit 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "page connected" "$(has_log $N 'page connected')"
check "voice started on pw-record / pw-play" "$(has_log $N 'voice started:.*in: pw-record.*out: pw-play')"
check "session summary written with passes" "$(has_log $N 'voice session ended .*[1-9][0-9]* passes')"
check "no error.log entries" "$(errlog_empty $N)"
check "no pace line after the session ended (ordering)" "$( awk '/voice session ended/{e=1} e && /conversion (falling|keeping)/{bad=1} END{print bad?0:1}' "$D/last_boot.log")"
echo "pace lines:"; grep -E "conversion|pace advice" "$D/last_boot.log"
echo "ui files: $(ls "$D/ui/wwwroot" 2>/dev/null | tr '\n' ' ')"; echo "tools: $(ls "$D/tools" 2>/dev/null | tr '\n' ' ')"
check "ui and tools unpacked into the data folder" "$([ -f "$D/ui/wwwroot/index.html" ] && [ -f "$D/tools/morphonic_export.py" ] && echo 1 || echo 0)"
check "boot log names the default devices in words" "$(has_log $N "microphone: +system default")"
finish $N
