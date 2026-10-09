#!/usr/bin/env bash
# The offline build: packed from this binary, unpacks its models with no network.
source "$(dirname "$0")/_common.sh"
N=s13_offline; rm -rf "$RUNS/$N"; mkdir -p "$RUNS/$N/data" "$RUNS/$N/data2"
OFF="$RUNS/$N/Morphonic-offline"
out="$("$BIN" --pack-offline "$MODELS" "$OFF" 2>&1)"; code=$?; echo "$out"
check "--pack-offline exits 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "pack reports three verified files" "$(echo "$out" | grep -c 'verified' | grep -qx 3 && echo 1 || echo 0)"
check "the offline file carries 3 models" "$(echo "$out" | grep -q 'carries 3 model' && echo 1 || echo 0)"
chmod +x "$OFF"
sz1=$(stat -c %s "$OFF"); echo "offline size $sz1 (plain $(stat -c %s "$BIN"))"
check "--help works on the offline binary" "$("$OFF" --help 2>&1 | grep -q 'real-time RVC voice changer' && echo 1 || echo 0)"
out="$("$OFF" --fetch-models --data-dir "$RUNS/$N/data" 2>&1)"; code=$?; echo "$out" | tr '\r' '\n' | grep -vE '^\s+(unpacking|downloading|assembling)' 
check "--fetch-models on the offline build exits 0 without network" "$([ $code = 0 ] && echo 1 || echo 0)"
check "says the files are included, not downloaded" "$(echo "$out" | grep -q 'included in this build, no download' && echo 1 || echo 0)"
check "progress says unpacking, never downloading" "$(echo "$out" | grep -q 'unpacking' && ! echo "$out" | grep -q 'downloading' && echo 1 || echo 0)"
check "all three verified against the pinned hashes" "$(echo "$out" | grep -q 'verify: 3 file(s) match' && echo 1 || echo 0)"
check "models and the sample voice in place" "$([ -f "$RUNS/$N/data/models/rmvpe.onnx" ] && [ -f "$RUNS/$N/data/models/contentvec-768-layer12.onnx" ] && [ -f "$RUNS/$N/data/voices/sample-voice-40k.onnx" ] && echo 1 || echo 0)"
cmp -s "$RUNS/$N/data/models/rmvpe.onnx" "$MODELS/rmvpe.onnx" && echo "rmvpe.onnx byte-identical to the source" 
check "unpacked rmvpe.onnx is byte-identical" "$(cmp -s "$RUNS/$N/data/models/rmvpe.onnx" "$MODELS/rmvpe.onnx" && echo 1 || echo 0)"
# re-packing from an offline base drops the old payload instead of stacking it
out="$("$OFF" --pack-offline "$MODELS" "$RUNS/$N/again" 2>&1)"; code=$?
sz2=$(stat -c %s "$RUNS/$N/again"); echo "repacked size $sz2"
check "repacking from the offline binary keeps the same size (payload replaced, not stacked)" "$([ $code = 0 ] && [ "$sz1" = "$sz2" ] && echo 1 || echo 0)"
# the offline binary boots (smoke) with its unpacked models
cd "$REPO"; out="$(timeout 180 bash tools/smoke.sh "$OFF" "$RUNS/$N/data/models" 2>&1)"; echo "$out" | grep -E "SMOKE TEST|\[FAIL\]"
check "smoke.sh passes on the offline binary" "$(echo "$out" | grep -q 'SMOKE TEST PASSED' && echo 1 || echo 0)"
rm -f "$RUNS/$N/again"
finish $N
