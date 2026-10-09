#!/usr/bin/env bash
# Launched "by Steam": the overlay in LD_PRELOAD and the scout runtime in
# LD_LIBRARY_PATH; the app re-runs itself without them and mirrors the exit code.
source "$(dirname "$0")/_common.sh"
N=s16_steam; D="$(fresh_data $N)"
LD_PRELOAD=/nonexistent/gameoverlayrenderer.so LD_LIBRARY_PATH=/nonexistent/steam-runtime/lib run_app $N --run-seconds 10; code=$?
show_logs $N
check "exit 0 under a Steam-like environment" "$([ $code = 0 ] && echo 1 || echo 0)"
check "page connected (the re-exec'd child ran the window)" "$(has_log $N 'page connected')"
check "no error.log entries" "$(errlog_empty $N)"
check "child processes were spawned without the preload (no ld.so complaint about the helpers)" "$(grep -c 'cannot be preloaded' "$RUNS/$N/stderr.txt" | awk '{print ($1 <= 1) ? 1 : 0}')"
finish $N
