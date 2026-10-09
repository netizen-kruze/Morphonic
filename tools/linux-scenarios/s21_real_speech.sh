#!/usr/bin/env bash
# Real speech through the live path: the bench clip is played into a sink
# whose monitor is the app's microphone; the app's output goes into the
# virtual microphone sink, whose monitor is recorded. The recording must be
# speech-like (non-silent, pitched) and track the clip's pitch.
source "$(dirname "$0")/_common.sh"
N=s21_speech; D="$(fresh_data $N with-models)"
echo '{ "VoiceId": "sample-voice-40k.onnx", "Acceleration": "cpu", "PitchSemitones": 0, "VirtualMic": true, "BlockMs": 500, "ExtraMs": 1000, "InputDeviceIndex": 1, "InputDeviceName": "Speech-In-Mic", "SidetoneAuto": false }' > "$D/settings.json"
pactl load-module module-null-sink sink_name=SpeechIn sink_properties=device.description=Speech-In >/dev/null
pactl load-module module-remap-source master=SpeechIn.monitor source_name=SpeechInMic source_properties=device.description=Speech-In-Mic >/dev/null
# the clip, 16 kHz mono, played in a loop for the whole session
cp "$REPO/src/Morphonic/Bench/fixture.wav" "$RUNS/$N/clip.wav"
start_app $N --auto-start --run-seconds 110
wait_for $N 'voice started' 70 || echo "(voice did not start within 70 s)"
sleep 2
( for _ in 1 2 3 4 5 6; do pw-play --target=SpeechIn "$RUNS/$N/clip.wav" 2>/dev/null; done ) &
PLAYER=$!
sleep 6   # let the pipeline fill, then record 40 s of the voice from the virtual microphone's sink monitor
timeout 40 pw-record --target=morphonic_voice.monitor --rate=40000 --channels=1 --format=s16 "$RUNS/$N/voice.wav" 2>"$RUNS/$N/rec.err" || true
kill $PLAYER 2>/dev/null; pkill -f "pw-play --target=SpeechIn" 2>/dev/null
wait $APP_PID; code=$?
show_logs $N
pactl list short modules | awk '/sink_name=SpeechIn|source_name=SpeechInMic/ {print $1}' | tac | xargs -r -n1 pactl unload-module
check "exit 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "voice started on the speech source and the virtual sink" "$(has_log $N 'voice started:.*in: pw-record.*Speech-In-Mic.*out: pw-play.*Morphonic-Voice')"
check "no error.log entries" "$(errlog_empty $N)"
python3 - "$RUNS/$N" <<'PY'
import sys, wave, numpy as np
d=sys.argv[1]
def read(p):
    w=wave.open(p); sr=w.getframerate(); x=np.frombuffer(w.readframes(w.getnframes()),dtype='<i2').astype(np.float64)/32768; return sr,x
def f0_track(sr,x,lo=60,hi=500):
    hop=int(sr*0.02); win=int(sr*0.04); out=[]
    for s in range(0,len(x)-win,hop):
        f=x[s:s+win]; f=f-f.mean()
        if np.sqrt((f**2).mean())<0.02: continue
        ac=np.correlate(f,f,'full')[win-1:]; ac/=ac[0]+1e-12
        lag0,lag1=int(sr/hi),int(sr/lo); seg=ac[lag0:lag1]; k=int(np.argmax(seg))+lag0
        if ac[k]>0.5: out.append(sr/k)
    return np.array(out)
sri,xi=read(d+"/clip.wav"); sro,xo=read(d+"/voice.wav")
rms=np.sqrt((xo**2).mean()); active=(np.abs(xo)>0.01).mean()
fi,fo=f0_track(sri,xi),f0_track(sro,xo)
mi=np.median(fi) if len(fi) else 0; mo=np.median(fo) if len(fo) else 0
print(f"recorded {len(xo)/sro:.1f} s at {sro} Hz, rms {rms:.4f}, {active*100:.0f}% of samples above -40 dBFS, {len(fo)} voiced frames, median f0 {mo:.0f} Hz (clip {mi:.0f} Hz)")
print("CHECKS", int(sro==40000 and len(xo)/sro>30), int(rms>0.01), int(len(fo)>100), int(mi>0 and 0.7<mo/mi<1.45))
PY
r=($(python3 - "$RUNS/$N" <<'PY'
import sys, wave, numpy as np
d=sys.argv[1]
def read(p):
    w=wave.open(p); sr=w.getframerate(); x=np.frombuffer(w.readframes(w.getnframes()),dtype='<i2').astype(np.float64)/32768; return sr,x
def f0_track(sr,x,lo=60,hi=500):
    hop=int(sr*0.02); win=int(sr*0.04); out=[]
    for s in range(0,len(x)-win,hop):
        f=x[s:s+win]; f=f-f.mean()
        if np.sqrt((f**2).mean())<0.02: continue
        ac=np.correlate(f,f,'full')[win-1:]; ac/=ac[0]+1e-12
        lag0,lag1=int(sr/hi),int(sr/lo); seg=ac[lag0:lag1]; k=int(np.argmax(seg))+lag0
        if ac[k]>0.5: out.append(sr/k)
    return np.array(out)
sri,xi=read(d+"/clip.wav"); sro,xo=read(d+"/voice.wav")
rms=np.sqrt((xo**2).mean()); fi,fo=f0_track(sri,xi),f0_track(sro,xo)
mi=np.median(fi) if len(fi) else 0; mo=np.median(fo) if len(fo) else 0
print(int(sro==40000 and len(xo)/sro>30), int(rms>0.01), int(len(fo)>100), int(mi>0 and 0.7<mo/mi<1.45))
PY
))
check "the virtual microphone carried 30+ s at 40 kHz" "${r[0]}"
check "the converted voice is not silent" "${r[1]}"
check "the converted voice is pitched speech (100+ voiced frames)" "${r[2]}"
check "the converted voice follows the clip's pitch (ratio 0.7..1.45 at 0 semitones)" "${r[3]}"
finish $N
