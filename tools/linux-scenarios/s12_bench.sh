#!/usr/bin/env bash
source "$(dirname "$0")/_common.sh"
N=s12_bench; D="$(fresh_data $N with-models)"; live_settings "$D" false 500 1000
out="$("$BIN" --bench --data-dir "$D" 2>&1)"; code=$?; echo "$out"
check "--bench exits 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "a row with load/avg pass/verdict is printed" "$(echo "$out" | grep -qE 'avg pass +[0-9]+ ms.*(fast|usable|too slow)' && echo 1 || echo 0)"
check "the summary line names the best candidate" "$(echo "$out" | grep -q '^best: ' && echo 1 || echo 0)"
check "bench.log written" "$(has_file "$D/bench.log")"
check "bench.log carries the machine line" "$(grep -q '^machine: ' "$D/bench.log" && echo 1 || echo 0)"
# --bench without a voice chosen says so instead of crashing
N2=s12_novoice; D2="$(fresh_data $N2 with-models)"; echo '{ "Acceleration": "cpu" }' > "$D2/settings.json"
t0=$(date +%s); out2="$(timeout 120 "$BIN" --bench --data-dir "$D2" 2>&1)"; code2=$?; echo "$out2"; echo "no-voice run took $(( $(date +%s) - t0 )) s"
check "--bench without a chosen voice exits 1 at once with a plain message" "$([ $code2 = 1 ] && [ $(( $(date +%s) - t0 )) -lt 60 ] && echo "$out2" | grep -q 'needs the components and a chosen voice' && echo 1 || echo 0)"
finish $N
