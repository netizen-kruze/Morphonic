#!/usr/bin/env bash
# Two running instances with two data folders, both with the virtual mic:
# the second must use the first's sink without adopting it, and its exit
# must leave the first's sink in place. Then a dead Wayland socket.
source "$(dirname "$0")/_common.sh"
NA=s28_a; DA="$(fresh_data $NA with-models)"; live_settings "$DA" true 500 1000
start_app $NA --auto-start --run-seconds 90; A=$APP_PID
wait_for $NA 'voice started' 70 || echo "(A did not start within 70 s)"
NB=s28_b; DB="$(fresh_data $NB)"; echo '{ "VirtualMic": true }' > "$DB/settings.json"
run_app $NB --run-seconds 10; cb=$?
sleep 2
sinks_after_b="$(pactl list short sinks)"; echo "--- sinks after B exited"; echo "$sinks_after_b"
a_alive=$(kill -0 $A 2>/dev/null && echo 1 || echo 0)
wait $A; ca=$?
echo "=== A"; show_logs $NA; echo "=== B"; show_logs $NB
check "B exits 0" "$([ $cb = 0 ] && echo 1 || echo 0)"
check "B saw the sink as present and left it alone (not adopted)" "$(grep -q 'virtual mic: present (made by another' "$DB/last_boot.log" && echo 1 || echo 0)"
check "A's sink survived B's exit" "$(echo "$sinks_after_b" | grep -q morphonic_voice && echo 1 || echo 0)"
check "A kept running and exited 0 with its summary" "$([ $a_alive = 1 ] && [ $ca = 0 ] && grep -q 'voice session ended (exiting)' "$DA/last_boot.log" && echo 1 || echo 0)"
check "A removed its sink at exit" "$(pactl list short sinks | grep -q morphonic_voice && echo 0 || echo 1)"
check "no virtualmic.json left in A's folder" "$([ -e "$DA/virtualmic.json" ] && echo 0 || echo 1)"
# a stale WAYLAND_DISPLAY without a socket and no DISPLAY: refused in words
N=s28_wl; D="$(fresh_data $N)"
env -u DISPLAY WAYLAND_DISPLAY=wayland-9 "$BIN" --data-dir "$D" --run-seconds 5 >"$RUNS/$N/stdout.txt" 2>"$RUNS/$N/stderr.txt"; code=$?
echo "dead wayland: exit=$code stderr=$(cat "$RUNS/$N/stderr.txt")"
check "dead Wayland socket: exit 1 with the no-display message" "$([ $code = 1 ] && grep -q 'no display' "$RUNS/$N/stderr.txt" && echo 1 || echo 0)"
check "dead Wayland socket: no sentinel left" "$([ -e "$D/boot.inprogress" ] && echo 0 || echo 1)"
# SIGHUP ends a session cleanly too
N=s28_hup; D="$(fresh_data $N with-models)"; live_settings "$D" true 500 1000
start_app $N --auto-start --run-seconds 300
wait_for $N 'voice started' 70 || echo "(voice did not start within 70 s)"
sleep 4; kill -HUP $APP_PID
for _ in $(seq 1 20); do kill -0 $APP_PID 2>/dev/null || break; sleep 1; done
alive=$(kill -0 $APP_PID 2>/dev/null && echo 1 || echo 0); [ $alive = 1 ] && kill -9 $APP_PID
wait $APP_PID; code=$?
show_logs $N
check "SIGHUP: exits 0 within 20 s" "$([ $alive = 0 ] && [ $code = 0 ] && echo 1 || echo 0)"
check "SIGHUP: exit requested by SIGHUP, summary written, sink removed" "$(has_log $N 'exit requested by SIGHUP' && has_log $N 'voice session ended \(exiting\)' && ! pactl list short sinks | grep -q morphonic_voice && echo 1 || echo 0)"
# Steam re-exec: a SIGTERM to the parent reaches the app
N=s28_steam; D="$(fresh_data $N with-models)"; live_settings "$D" false 500 1000
set -m; LD_PRELOAD=/nonexistent/gameoverlayrenderer.so "$BIN" --data-dir "$D" --auto-start --run-seconds 300 >"$RUNS/$N/stdout.txt" 2>"$RUNS/$N/stderr.txt" & P=$!; set +m
wait_for $N 'voice started' 80 || echo "(voice did not start within 80 s)"
sleep 3; kill -TERM $P
for _ in $(seq 1 20); do kill -0 $P 2>/dev/null || break; sleep 1; done
alive=$(kill -0 $P 2>/dev/null && echo 1 || echo 0); [ $alive = 1 ] && kill -9 $P
wait $P; code=$?
sleep 2; orphan=$(pgrep -f "Morphonic-1.0.1-linux-x64 --data-dir $D" | wc -l)
show_logs $N
check "Steam re-exec: SIGTERM to the parent ends the app (exit requested by SIGTERM)" "$(has_log $N 'exit requested by SIGTERM')"
check "Steam re-exec: parent exits 0 and no orphan child remains" "$([ $alive = 0 ] && [ $code = 0 ] && [ "$orphan" = 0 ] && echo 1 || echo 0)"
finish s28_two_instances
