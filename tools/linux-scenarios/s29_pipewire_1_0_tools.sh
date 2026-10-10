#!/usr/bin/env bash
# PipeWire before 1.2 (Ubuntu 24.04, Debian 12), imitated on a newer one:
# a pw-record / pw-play that refuse --raw, list no such option in --help and
# take a pipe as raw. The app must read that from --help and run the session
# on the tools without --raw, without ever trying the flag; on this machine's
# real tools it must do the opposite and never start one without it (a 1.2+
# pw-play without --raw waits for a sound file instead of failing).
source "$(dirname "$0")/_common.sh"
if ! pw-record --help 2>/dev/null | grep -q -- '--raw'; then
  echo "  [SKIP] this machine's PipeWire tools are themselves older than 1.2: every other scenario already ran on them"
  finish s29_pipewire_1_0_tools; exit 0
fi
report_says() { # report_says ZIP 'text' -> 1/0
  python3 -c "import sys, zipfile; print(1 if sys.argv[2] in zipfile.ZipFile(sys.argv[1]).read('system.txt').decode() else 0)" "$1" "$2" 2>/dev/null || echo 0
}

N=s29_old; D="$(fresh_data $N with-models)"; live_settings "$D" true 500 1000
SHIM="$RUNS/$N/shim"; mkdir -p "$SHIM"
for t in pw-record pw-play; do
  real="$(command -v $t)"
  cat > "$SHIM/$t" <<EOF
#!/bin/sh
# $t as PipeWire 1.0 behaves, on top of the real one
for a in "\$@"; do
  case "\$a" in
    --raw) echo "$t: unrecognized option '--raw'" >&2; echo "$t" >> "$RUNS/$N/refused.log"; exit 1;;
    --help) "$real" --help | grep -v -- '--raw'; exit 0;;
  esac
done
echo "$t \$*" >> "$RUNS/$N/ran.log"
exec "$real" --raw "\$@"
EOF
  chmod +x "$SHIM/$t"
done
PATH="$SHIM:$PATH" run_app $N --auto-start --run-seconds 45; code=$?
show_logs $N
echo "--- the tools as they were run"; cat "$RUNS/$N/ran.log" 2>/dev/null
check "older tools: exit 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "older tools: voice on pw-record and pw-play without --raw" "$(has_log $N 'voice started:.*in: pw-record \(PipeWire 1.0, no --raw\).*out: pw-play \(PipeWire 1.0, no --raw\)')"
check "older tools: the sidetone too" "$(has_log $N 'sidetone: pw-play \(PipeWire 1.0, no --raw\)')"
check "older tools: --raw was never tried" "$([ ! -e "$RUNS/$N/refused.log" ] && echo 1 || echo 0)"
check "older tools: the session ran to the end with passes" "$(has_log $N 'voice session ended \(exiting\) .*[1-9][0-9]* passes')"
check "older tools: no error.log entries" "$(errlog_empty $N)"
PATH="$SHIM:$PATH" "$BIN" --report --data-dir "$D" >"$RUNS/$N/report.txt" 2>&1
zip="$(sed -n 's/^report written: //p' "$RUNS/$N/report.txt")"
check "older tools: --report says they do not take --raw" "$(report_says "$zip" 'do not take --raw')"

# this machine's own tools (1.2 or newer)
N=s29_new; D="$(fresh_data $N with-models)"; live_settings "$D" false 500 1000
run_app $N --auto-start --run-seconds 25; code=$?
show_logs $N
check "newer tools: exit 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "newer tools: voice on pw-record and pw-play with --raw (no 1.0 note)" "$(has_log $N 'voice started:.*in: pw-record \(default input\); out: pw-play \(default output\)')"
check "newer tools: no error.log entries" "$(errlog_empty $N)"
"$BIN" --report --data-dir "$D" >"$RUNS/$N/report.txt" 2>&1
zip="$(sed -n 's/^report written: //p' "$RUNS/$N/report.txt")"
check "newer tools: --report says they take --raw and are tried first" "$([ "$(report_says "$zip" 'take --raw (PipeWire 1.2 or newer)')" = 1 ] && [ "$(report_says "$zip" 'tried first: microphone pw-record; output pw-play')" = 1 ] && echo 1 || echo 0)"
finish s29_pipewire_1_0_tools
