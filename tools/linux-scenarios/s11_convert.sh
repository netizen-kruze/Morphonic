#!/usr/bin/env bash
# --convert through the live pipeline: output rate/length, non-silence, and the
# pitch relation between --pitch 0 and --pitch 12 (f0 by autocorrelation).
source "$(dirname "$0")/_common.sh"
N=s11_convert; D="$(fresh_data $N with-models)"; live_settings "$D" false 500 1000
cp "$REPO/src/Morphonic/Bench/fixture.wav" "$RUNS/$N/in.wav"
"$BIN" --convert "$RUNS/$N/in.wav" "$RUNS/$N/out0.wav" --pitch 0 --data-dir "$D" 2>&1 | tee "$RUNS/$N/c0.txt"; c0=${PIPESTATUS[0]}
"$BIN" --convert "$RUNS/$N/in.wav" "$RUNS/$N/out12.wav" --pitch 12 --data-dir "$D" 2>&1 | tee "$RUNS/$N/c12.txt"; c12=${PIPESTATUS[0]}
check "--convert --pitch 0 exits 0" "$([ $c0 = 0 ] && echo 1 || echo 0)"
check "--convert --pitch 12 exits 0" "$([ $c12 = 0 ] && echo 1 || echo 0)"
python3 - "$RUNS/$N" <<'PY'
import sys, wave, numpy as np
d = sys.argv[1]
def read(p):
    w = wave.open(p); sr = w.getframerate(); x = np.frombuffer(w.readframes(w.getnframes()), dtype='<i2').astype(np.float64) / 32768; return sr, x
def f0_track(sr, x, lo=60, hi=500):
    hop = int(sr * 0.02); win = int(sr * 0.04); out = []
    for s in range(0, len(x) - win, hop):
        f = x[s:s+win]; f = f - f.mean()
        if np.sqrt((f**2).mean()) < 0.02: continue
        ac = np.correlate(f, f, 'full')[win-1:]; ac /= ac[0] + 1e-12
        lag0, lag1 = int(sr / hi), int(sr / lo)
        seg = ac[lag0:lag1]; k = int(np.argmax(seg)) + lag0
        if ac[k] > 0.5: out.append(sr / k)
    return np.array(out)
sri, xi = read(d + "/in.wav"); sr0, x0 = read(d + "/out0.wav"); sr12, x12 = read(d + "/out12.wav")
rms0 = np.sqrt((x0**2).mean()); rms12 = np.sqrt((x12**2).mean())
fi, f0, f12 = f0_track(sri, xi), f0_track(sr0, x0), f0_track(sr12, x12)
mi, m0, m12 = np.median(fi), np.median(f0), np.median(f12)
print(f"input: {sri} Hz {len(xi)/sri:.2f} s, median f0 {mi:.1f} Hz over {len(fi)} voiced frames")
print(f"out0:  {sr0} Hz {len(x0)/sr0:.2f} s, rms {rms0:.4f}, median f0 {m0:.1f} Hz ({len(f0)} frames)")
print(f"out12: {sr12} Hz {len(x12)/sr12:.2f} s, rms {rms12:.4f}, median f0 {m12:.1f} Hz ({len(f12)} frames)")
r = m12 / m0 if m0 else 0; r0 = m0 / mi if mi else 0
print(f"ratio out12/out0 = {r:.3f} (expect ~2), out0/input = {r0:.3f} (expect ~1)")
print("CHECKS", int(sr0 == 40000 and sr12 == 40000), int(abs(len(x0)/sr0 - len(xi)/sri) < 1.0), int(rms0 > 0.01 and rms12 > 0.01), int(1.7 < r < 2.3), int(0.75 < r0 < 1.33))
PY
r=($(python3 - "$RUNS/$N" <<'PY'
import sys, wave, numpy as np
d = sys.argv[1]
def read(p):
    w = wave.open(p); sr = w.getframerate(); x = np.frombuffer(w.readframes(w.getnframes()), dtype='<i2').astype(np.float64) / 32768; return sr, x
def f0_track(sr, x, lo=60, hi=500):
    hop = int(sr * 0.02); win = int(sr * 0.04); out = []
    for s in range(0, len(x) - win, hop):
        f = x[s:s+win]; f = f - f.mean()
        if np.sqrt((f**2).mean()) < 0.02: continue
        ac = np.correlate(f, f, 'full')[win-1:]; ac /= ac[0] + 1e-12
        lag0, lag1 = int(sr / hi), int(sr / lo)
        seg = ac[lag0:lag1]; k = int(np.argmax(seg)) + lag0
        if ac[k] > 0.5: out.append(sr / k)
    return np.array(out)
sri, xi = read(d + "/in.wav"); sr0, x0 = read(d + "/out0.wav"); sr12, x12 = read(d + "/out12.wav")
rms0 = np.sqrt((x0**2).mean()); rms12 = np.sqrt((x12**2).mean())
fi, f0, f12 = f0_track(sri, xi), f0_track(sr0, x0), f0_track(sr12, x12)
mi, m0, m12 = np.median(fi), np.median(f0), np.median(f12)
r = m12 / m0 if m0 else 0; r0 = m0 / mi if mi else 0
print(int(sr0 == 40000 and sr12 == 40000), int(abs(len(x0)/sr0 - len(xi)/sri) < 1.0), int(rms0 > 0.01 and rms12 > 0.01), int(1.7 < r < 2.3), int(0.75 < r0 < 1.33))
PY
))
check "outputs are at the voice's 40 kHz" "${r[0]}"
check "output length matches the input within 1 s" "${r[1]}"
check "outputs are not silent" "${r[2]}"
check "+12 semitones doubles the output pitch (ratio 1.7..2.3)" "${r[3]}"
check "pitch 0 keeps the input pitch (ratio 0.75..1.33)" "${r[4]}"
finish $N
