#!/usr/bin/env bash
# A GPU pack of the right file sizes but garbage content: the runtime must
# fail to load and the app fall back to the CPU with a sentence, not a crash.
source "$(dirname "$0")/_common.sh"
N=s25_gpu; D="$(fresh_data $N with-models)"; live_settings "$D" false 500 1000
G="$D/runtimes/gpu/linux-x64"; mkdir -p "$G"
while read -r name size; do truncate -s "$size" "$G/$name"; done <<'LIST'
libonnxruntime.so 25493408
libonnxruntime_providers_cuda.so 315724552
libonnxruntime_providers_shared.so 14632
ONNXRUNTIME-LICENSE.txt 1094
libcudart.so.12 741088
NVIDIA-CUDA-LICENSE.txt 59262
libnvJitLink.so.12 95935344
libcublasLt.so.12 749210000
libcublas.so.12 105140976
libcufft.so.11 291507928
libcurand.so.10 136749240
libcudnn.so.9 133336
libcudnn_graph.so.9 115562616
libcudnn_ops.so.9 106964664
libcudnn_cnn.so.9 4203880
libcudnn_engines_precompiled.so.9 566255968
libcudnn_engines_runtime_compiled.so.9 48269728
libcudnn_engines_tensor_ir.so.9 2099128
libcudnn_heuristic.so.9 96473880
libcudnn_adv.so.9 274846904
NVIDIA-CUDNN-LICENSE.txt 18174
LIST
python3 - "$D/settings.json" <<'PY'
import json,sys; p=sys.argv[1]; s=json.load(open(p)); s["Acceleration"]="gpu"; json.dump(s,open(p,"w"))
PY
run_app $N --auto-start --run-seconds 60; code=$?
show_logs $N
check "exit 0 (no crash on a garbage runtime)" "$([ $code = 0 ] && echo 1 || echo 0)"
check "boot log says the pack is installed" "$(has_log $N 'gpu: .*pack installed')"
check "acceleration fell back to the CPU with the reason" "$(has_log $N 'acceleration: +CPU \(GPU pack present but its runtime did not load')"
check "the voice still ran on the CPU" "$(has_log $N 'voice started: .*· CPU ·')"
check "the load failure is noted in error.log, nothing unhandled" "$(grep -q 'GpuPack.PreloadRuntime' "$D/error.log" 2>/dev/null && ! grep -q 'Unhandled' "$D/error.log" && echo 1 || echo 0)"
# --bench and --install-gpu report the same verdict
out="$("$BIN" --install-gpu --data-dir "$D" 2>&1)"; echo "$out" | tail -3
check "--install-gpu reports the pack as installed and the runtime failure" "$(echo "$out" | grep -q 'already installed' && echo "$out" | grep -q 'did not load\|failed to load' && echo 1 || echo 0)"
finish $N
