#!/usr/bin/env bash
# A voice file of the right size but garbage content, a crash record read
# through fake coredumpctl / journalctl, and a data folder that cannot be written.
source "$(dirname "$0")/_common.sh"
N=s27_damaged; D="$(fresh_data $N with-models)"; live_settings "$D" false 500 1000
rm "$D/voices/sample-voice-40k.onnx"; cp "$VOICES/sample-voice-40k.onnx" "$D/voices/sample-voice-40k.onnx"
dd if=/dev/urandom of="$D/voices/sample-voice-40k.onnx" bs=1M count=1 conv=notrunc 2>/dev/null
run_app $N --auto-start --run-seconds 40; code=$?
show_logs $N
check "damaged voice: exit 0 (no crash)" "$([ $code = 0 ] && echo 1 || echo 0)"
check "damaged voice: the start failure is in error.log as StartSession" "$(grep -q 'StartSession' "$D/error.log" 2>/dev/null && ! grep -q 'Unhandled' "$D/error.log" && echo 1 || echo 0)"
check "damaged voice: no 'voice started' line" "$( [ "$(has_log $N 'voice started')" = 0 ] && echo 1 || echo 0)"
# crash record: fake tools in PATH after an unfinished boot
N=s27_crash; D="$(fresh_data $N)"; SHIM="$RUNS/$N/shim"; mkdir -p "$SHIM"
printf '#!/bin/sh\necho "$@" >> "%s/argv.log"\necho "Thu 2026-10-09 04:40:00 UTC 5941 0 0 SIGSEGV present /x/Morphonic-1.0.1-linux-x64 1.2M"\n' "$RUNS/$N" > "$SHIM/coredumpctl"
printf '#!/bin/sh\necho "$@" >> "%s/argv.log"\necho "2026-10-09T04:40:00+0000 host kernel: Morphonic-1.0.1-lin[5941]: segfault at 0 ip 0 sp 0 error 4"\necho "2026-10-09T04:40:01+0000 host other[1]: unrelated line"\n' "$RUNS/$N" > "$SHIM/journalctl"
chmod +x "$SHIM"/*
echo "1.0.1|2026-10-09T04:39:00.0000000+00:00|5941" > "$D/boot.inprogress"
PATH="$SHIM:$PATH" run_app $N --run-seconds 10; code=$?
show_logs $N; echo "--- tool argv"; cat "$RUNS/$N/argv.log"
check "crash record: exit 0" "$([ $code = 0 ] && echo 1 || echo 0)"
COMM="$(basename "$BIN" | cut -c1-15)"
check "crash record: coredumpctl asked with the 15-byte comm and --since" "$(grep -q -- "--since=.* $COMM\$" "$RUNS/$N/argv.log" && echo 1 || echo 0)"
check "crash record: error.log holds the SIGSEGV line and the segfault line, not the unrelated one" "$(grep -q 'SIGSEGV' "$D/error.log" && grep -q 'segfault' "$D/error.log" && ! grep -q 'unrelated' "$D/error.log" && echo 1 || echo 0)"
# unwritable data folder (root writes through mode 555, so only as a normal user)
if [ "$(id -u)" = 0 ]; then
  echo "  [SKIP] read-only folder: running as root, which ignores the folder's mode"
else
  N=s27_ro; D="$(fresh_data $N)"; chmod 555 "$D"
  run_app $N --run-seconds 10; code=$?; chmod 755 "$D"
  echo "stderr: $(grep -v -E 'dbind|libEGL' "$RUNS/$N/stderr.txt")"
  check "read-only folder: exits 1 within the run" "$([ $code = 1 ] && echo 1 || echo 0)"
  check "read-only folder: says it could not unpack its interface files" "$(grep -q "unpack its interface files" "$RUNS/$N/stderr.txt" && echo 1 || echo 0)"
fi
finish s27_damaged_and_unwritable
