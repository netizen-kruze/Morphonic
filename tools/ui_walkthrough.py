#!/usr/bin/env python3
"""Drives the real Morphonic window through docs/TEST_CHECKLIST.md (Windows).

Starts Morphonic.exe with --debug-port, talks to the page over the DevTools
protocol (tools/cdp.py), clicks the actual buttons and checks the actual
outcomes: the DOM, the toasts, the data folder, last_boot.log. Nothing
touches the real %APPDATA%\\Morphonic; everything happens in --data.

    python tools/ui_walkthrough.py --exe publish\\Morphonic.exe --data C:\\tmp\\morphonic-ui --phase all
    python tools/ui_walkthrough.py ... --phase first      # A: first run, the two cards, downloads
    python tools/ui_walkthrough.py ... --phase models     # A: verify, delete, cancel, corrupt, GPU pack + restart
    python tools/ui_walkthrough.py ... --phase voice      # B: start/stop, live pitch, restart on block change, bench
    python tools/ui_walkthrough.py ... --phase voices     # C: .pth conversion (good, v1, no-f0), use, delete rules
    python tools/ui_walkthrough.py ... --phase lifecycle  # E: second instance, close-to-tray, crash sentinel, bad settings
    python tools/ui_walkthrough.py ... --phase settings   # F/B: defaults, acceleration, docs, update simulation

Needs: pip install websocket-client. Phases after "first" expect the
components in --data (run "first" once, or point --data at a folder where
`Morphonic --fetch-models --data-dir` ran). Screenshots and a results file go
to --out.
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from cdp import Page  # noqa: E402

PORT = 9222
RESULTS = []


def result(ok, name, detail=""):
    RESULTS.append((ok, name, detail))
    print("  [%s] %s%s" % ("PASS" if ok else "FAIL", name, (" - " + detail) if detail else ""), flush=True)
    return ok


def info(msg):
    print("  ..   " + msg, flush=True)


class App:
    def __init__(self, exe, data, out, env_extra=None):
        self.exe, self.data, self.out = exe, data, out
        self.env_extra = env_extra or {}
        self.proc = None
        self.page = None

    # WebView2 keeps its browser process a moment after a hard kill; a
    # new window cannot share its profile until it is gone. Only
    # Morphonic's own browser processes are touched: WebView2 tags them
    # with "--webview-exe-name=Morphonic.exe" (the browser process) and the
    # profile folder under the test's data folder (its children). Every
    # other Photino app on the machine (VRCNext, Chatterbox…) also says
    # "Photino" on its command line, so that word must never be the filter.
    @staticmethod
    def kill_all(data=None):
        marks = ["--webview-exe-name=Morphonic.exe"]
        if data:
            marks.append(os.path.join(os.path.abspath(data), "webview"))
        cond = " -or ".join("$_.CommandLine.Contains('%s')" % m.replace("'", "''") for m in marks)
        subprocess.run(["powershell", "-NoProfile", "-Command",
                        "Stop-Process -Name Morphonic -Force -ErrorAction SilentlyContinue; "
                        "Get-CimInstance Win32_Process -Filter \"name='msedgewebview2.exe'\" | "
                        "Where-Object { $_.CommandLine -ne $null -and (" + cond + ") } | "
                        "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"],
                       capture_output=True)
        time.sleep(1.5)

    def launch(self, extra_args=(), connect=True, env_extra=None, exe=None):
        env = dict(os.environ)
        env.update(self.env_extra)
        env.update(env_extra or {})
        args = [exe or self.exe, "--data-dir", self.data, "--debug-port", str(PORT), *extra_args]
        self.proc = subprocess.Popen(args, env=env, cwd=os.path.dirname(exe or self.exe),
                                     creationflags=subprocess.CREATE_NEW_PROCESS_GROUP | subprocess.DETACHED_PROCESS)
        if connect:
            self.connect()
        return self.proc

    def connect(self, timeout=40):
        self.page = Page(PORT, timeout=timeout)
        self.page.wait("typeof send === 'function' && !!document.getElementById('toasts')", timeout=30, what="the page's script")
        # keep every toast the page shows, even after it fades
        self.page.eval("""(function(){ if (window.__toasts) return true;
            window.__toasts = Array.from(document.querySelectorAll('#toasts .toast')).map(function(t){ return t.textContent.trim(); });
            new MutationObserver(function(ms){ ms.forEach(function(m){ m.addedNodes.forEach(function(n){
              if (n.textContent) window.__toasts.push(n.textContent.trim()); }); }); })
            .observe(document.getElementById('toasts'), {childList: true}); return true; })()""")
        return self.page

    def toasts(self):
        return self.page.eval("window.__toasts") or []

    def wait_toast(self, needle, timeout=30):
        deadline = time.time() + timeout
        while time.time() < deadline:
            for t in self.toasts():
                if needle.lower() in t.lower():
                    return t
            time.sleep(0.3)
        return None

    def view(self, name):
        self.page.click('.rail button[data-view="%s"]' % name)
        time.sleep(0.3)

    def send(self, obj):
        self.page.eval("send(%s)" % json.dumps(obj))

    def shot(self, name):
        path = os.path.join(self.out, name + ".png")
        self.page.screenshot(path)
        return path

    def boot_log(self):
        try:
            with open(os.path.join(self.data, "last_boot.log"), encoding="utf-8") as f:
                return f.read()
        except OSError:
            return ""

    def error_log(self):
        try:
            with open(os.path.join(self.data, "error.log"), encoding="utf-8") as f:
                return f.read()
        except OSError:
            return ""

    def stop(self):
        if self.page:
            self.page.close()
            self.page = None
        self.kill_all(self.data)
        self.proc = None

    def alive(self):
        r = subprocess.run(["tasklist", "/FI", "IMAGENAME eq Morphonic.exe"], capture_output=True, text=True)
        return r.stdout.count("Morphonic.exe")


def models_dir(data):
    return os.path.join(data, "models")


def voices_dir(data):
    return os.path.join(data, "voices")


# ── A. first run ─────────────────────────────────────────────────────

def phase_first(app):
    print("== A. first run")
    if os.path.isdir(app.data):
        shutil.rmtree(app.data)
    os.makedirs(app.data)
    app.launch()
    p = app.page
    result(p.visible("#view-firstrun") and not p.visible("#view-voice"), "first-run screen shows instead of the Voice view")
    result(p.eval("document.getElementById('btnStartStop').disabled"), "Start is disabled")
    result((p.text("#statusText") or "").lower().startswith("idle"), "header chip reads idle", p.text("#statusText"))
    result("MB" in (p.text("#chSetupSize") or "") or "included" in (p.text("#chSetupSize") or ""), "set-up card shows the download size (or 'included')", p.text("#chSetupSize"))
    app.shot("A1_firstrun")

    included = "included" in (p.text("#chSetupSize") or "")
    t0 = time.time()
    p.click("#chSetup")
    seen_assembling = False
    seen_pct = False
    while time.time() - t0 < 600:
        label = p.text("#frLabel") or ""
        if "Assembling" in label:
            seen_assembling = True
        if "%" in label:
            seen_pct = True
        if not p.visible("#view-firstrun"):
            break
        time.sleep(0.5)
    result(not p.visible("#view-firstrun") and p.visible("#view-voice"), "after set-up the Voice view replaces first-run (%.0f s)" % (time.time() - t0))
    result(seen_pct, "progress label showed percentages")
    result(seen_assembling or included, "progress label showed the Assembling phase for the encoder (not expected for an offline build)")
    for f in ("contentvec-768-layer12.onnx", "rmvpe.onnx"):
        result(os.path.exists(os.path.join(models_dir(app.data), f)), "installed " + f)
    result(not any(n.endswith((".partial", ".source", ".assembling")) for n in os.listdir(models_dir(app.data))), "no leftover partial/source files")
    app.shot("A2_voice_after_setup")

    app.view("voices")
    result(p.visible("#view-voices"), "Voices view opens")
    rows = p.text("#voiceRows") or ""
    result("import" in rows.lower() or rows.strip() == "" or "no voice" in rows.lower(), "voices library is empty with the import hint", rows[:80])
    app.shot("A3_voices_empty")

    app.view("models")
    result(p.eval("!!document.querySelector('[data-model=\"contentvec\"] .badge.installed')"), "Models: encoder row shows Installed")
    result(p.eval("!!document.querySelector('[data-model=\"rmvpe\"] .badge.installed')"), "Models: pitch model row shows Installed")
    result(p.eval("!!document.querySelector('[data-dl=\"sample-voice\"]')"), "Models: sample voice offers Download/Unpack")
    hw = p.text("#hwText") or ""
    result("GPU" in hw or "CPU" in hw, "hardware banner names the tier", hw)
    app.shot("A4_models")

    t0 = time.time()
    p.click('[data-dl="sample-voice"]')
    p.wait("!!document.querySelector('[data-model=\"sample-voice\"] .badge.installed')", timeout=300, what="sample voice installed")
    info("sample voice downloaded + assembled in %.0f s" % (time.time() - t0))
    t = app.wait_toast("assembled and verified", 10)
    result(bool(t), "toast confirms the sample voice was downloaded, assembled and verified", t or "")
    result(os.path.exists(os.path.join(voices_dir(app.data), "sample-voice-40k.onnx")), "sample-voice-40k.onnx is in the voices folder")
    app.view("voice")
    p.wait("!document.getElementById('btnStartStop').disabled", timeout=10, what="Start enabled")
    result(True, "Start enables once the sample voice is active")
    result("sample voice" in (p.text("#statusText") or "").lower(), "the header chip names the sample voice", p.text("#statusText") or "")
    app.shot("A5_voice_ready")
    app.stop()


# ── A (continued). models screen ──────────────────────────────────────

def phase_models(app):
    print("== A. models screen")
    app.launch()
    p = app.page
    app.view("models")
    p.click("#btnVerify")
    t = app.wait_toast("verified", 60)
    result(bool(t), "Verify installed files reports success", t or "")

    # delete the sample voice, see the row return to Download, download again
    p.click('[data-del="sample-voice"]')
    p.wait("!!document.querySelector('[data-dl=\"sample-voice\"]')", timeout=10, what="sample voice row back to Download")
    result(not os.path.exists(os.path.join(voices_dir(app.data), "sample-voice-40k.onnx")), "Delete removes the sample voice file")
    t0 = time.time()
    p.click('[data-dl="sample-voice"]')
    p.wait("!!document.querySelector('[data-model=\"sample-voice\"] .badge.installed')", timeout=300, what="sample voice installed again")
    result(True, "download again completes (%.0f s)" % (time.time() - t0))

    # delete a required component: the Voice view must go back to first-run;
    # its re-download is the one we cancel midway (it is the largest file)
    p.click('[data-del="rmvpe"]')
    p.wait("!!document.querySelector('[data-dl=\"rmvpe\"]')", timeout=10, what="pitch row back to Download")
    time.sleep(1)
    app.view("voice")
    result(p.visible("#view-firstrun"), "deleting a required component brings the first-run screen back",
           "firstRunNeeded=%s" % p.eval("firstRunNeeded"))
    app.view("models")
    p.click('[data-dl="rmvpe"]')
    p.wait("!!document.querySelector('[data-cancel]')", timeout=10, what="Cancel button while downloading")
    result(True, "a running download shows Cancel")
    time.sleep(1.0)
    if p.eval("!!document.querySelector('[data-cancel]')"):
        p.eval("window.__toasts.length = 0")
        p.click("[data-cancel]")
        cancelled = app.wait_toast("cancel", 10)
        result(bool(cancelled), "Cancel stops the download with a toast", cancelled or "")
        time.sleep(1)
        result(p.eval("!!document.querySelector('[data-dl=\"rmvpe\"]')"), "row is back to Download after Cancel")
        leftovers = [n for n in os.listdir(models_dir(app.data)) if ".partial" in n or ".source" in n]
        result(not leftovers, "no partial file left after Cancel", ", ".join(leftovers))
        t0 = time.time()
        p.click('[data-dl="rmvpe"]')
    else:
        info("the download finished before Cancel could be pressed (fast connection)")
        t0 = time.time()
    p.wait("!!document.querySelector('[data-model=\"rmvpe\"] .badge.installed')", timeout=600, what="pitch model installed again")
    info("pitch model downloaded again in %.0f s" % (time.time() - t0))
    app.view("voice")
    result(p.visible("#view-voice"), "Voice view is back once the component is reinstalled")
    app.view("models")

    # corrupt a model, Verify must name it; repair by truncating the extra byte
    rm = os.path.join(models_dir(app.data), "rmvpe.onnx")
    with open(rm, "ab") as f:
        f.write(b"\0")
    p.eval("window.__toasts.length = 0")
    p.click("#btnVerify")
    t = app.wait_toast("Pitch model", 90)
    result(bool(t), "Verify names a corrupted file", t or "")
    with open(rm, "r+b") as f:
        f.seek(0, 2)
        f.truncate(f.tell() - 1)
    p.eval("window.__toasts.length = 0")
    p.click("#btnVerify")
    t = app.wait_toast("verified", 90)
    result(bool(t) and "Pitch" not in t, "Verify passes again after the repair", t or "")

    app.shot("A6_models_after_tests")

    # GPU pack: download, restart through the toast's action, DirectML active
    if p.eval("!!document.querySelector('[data-del=\"gpu-pack\"]')"):
        p.click('[data-del="gpu-pack"]')
        p.wait("!!document.querySelector('[data-dl=\"gpu-pack\"]')", timeout=10, what="pack row back to Download")
        result(True, "Delete removes an installed GPU pack")
    if p.eval("!!document.querySelector('[data-dl=\"gpu-pack\"]')"):
        app.view("models")
        t0 = time.time()
        p.eval("window.__toasts.length = 0")
        p.click('[data-dl="gpu-pack"]')
        t = app.wait_toast("restart Morphonic to activate", 600)
        p.wait("!!document.querySelector('#toasts button')", timeout=10, what="the toast's Restart button")
        result(bool(t), "GPU pack download completes with a restart toast (%.0f s)" % (time.time() - t0), t or "")
        app.shot("A7_gpu_pack_toast")
        old_pid = app.proc.pid
        p.click("#toasts button")
        app.page.close()
        app.page = None
        time.sleep(6)
        app.connect(timeout=60)
        p = app.page
        app.view("models")
        hw = p.text("#hwText") or ""
        result("DirectML" in hw, "after the restart the hardware banner shows DirectML", hw)
        log = app.boot_log()
        result("acceleration:   DirectML" in log or "acceleration: DirectML" in log, "last_boot.log acceleration line says DirectML", [l for l in log.splitlines() if "acceleration" in l][:1].__str__())
        result(app.alive() == 1, "exactly one Morphonic process after the restart")
        app.shot("A8_after_restart")
    else:
        info("GPU pack row not offered (already installed, or no usable GPU)")
    app.stop()


# ── B. voice core ────────────────────────────────────────────────────

def phase_voice(app):
    print("== B. voice core")
    app.launch()
    p = app.page
    app.view("voice")
    p.wait("!document.getElementById('btnStartStop').disabled", timeout=15, what="Start enabled")
    p.click("#btnStartStop")
    p.wait("document.getElementById('btnStartStopText').textContent.toLowerCase().indexOf('stop') >= 0", timeout=60, what="Stop button")
    time.sleep(6)
    status = p.text("#statusText") or ""
    pace = p.text("#paceText") or ""
    result("keeping up" in (status + pace).lower() or "live" in status.lower(), "header chip reports the session state", status + " | " + pace)
    log = app.boot_log()
    result("voice started:" in log, "last_boot.log has the voice-started line")
    result(p.visible("#stageLive") and not p.visible("#stageIdle"), "live stage replaces the idle stage")
    app.shot("B1_live")

    # pitch is live: no restart
    before = log.count("voice started:")
    p.set_value("#rngPitch", "12")
    time.sleep(3)
    result(app.boot_log().count("voice started:") == before, "moving the pitch slider does not restart the voice")

    # block size restarts
    app.view("settings")
    p.click('#segBlock button[data-v="160"]')
    time.sleep(8)
    log = app.boot_log()
    result(log.count("voice started:") == before + 1, "changing the block size restarts the voice once", "%d starts" % log.count("voice started:"))
    result("block 160 ms" in log, "the restarted session uses 160 ms blocks")
    p.click('#segGate button[data-v="-40"]')
    p.click('#segRms button[data-v="0"]')
    time.sleep(2)
    settings = json.load(open(os.path.join(app.data, "settings.json"), encoding="utf-8"))
    result(settings.get("NoiseGateDb") == -40 and settings.get("RmsMixRate") == 0, "gate and loudness choices are saved", json.dumps({k: settings.get(k) for k in ("NoiseGateDb", "RmsMixRate", "BlockMs")}))
    app.shot("B2_settings_live")
    p.click('#segBlock button[data-v="250"]')
    time.sleep(6)

    app.view("voice")
    p.click("#btnStartStop")
    p.wait("document.getElementById('btnStartStopText').textContent.toLowerCase().indexOf('start') >= 0", timeout=30, what="Start button back")
    time.sleep(1.5)
    log = app.boot_log()
    ended = [l for l in log.splitlines() if "voice session ended" in l]
    result(len(ended) >= 1, "Stop writes the session summary", ended[-1][:160] if ended else "")
    result("underrun" in (ended[-1] if ended else ""), "summary includes the underrun count")
    result("Unhandled" not in app.error_log(), "no unhandled exception in error.log")

    # speed check from the UI
    app.view("settings")
    p.click("#btnBench")
    p.wait("(document.getElementById('benchOut').textContent||'').indexOf('per') >= 0 || (document.getElementById('benchOut').textContent||'').indexOf('block') >= 0", timeout=300, what="bench result")
    bench = p.text("#benchOut") or ""
    result("ms" in bench, "speed check shows a result row", bench[:160])
    result(os.path.exists(os.path.join(app.data, "bench.log")), "bench.log written")
    app.shot("B3_bench")
    app.stop()


# ── C. voices ────────────────────────────────────────────────────────

def phase_voices(app, testvoices, python):
    print("== C. voices")
    for n in ("TestVoice40k.onnx", "Dropped.onnx", "f0G40k.pth"):   # leftovers of an aborted run
        try:
            os.remove(os.path.join(voices_dir(app.data), n))
        except OSError:
            pass
    for n in ("TestVoice40k.pth", "OldV1Voice.pth", "NoPitchVoice.pth"):
        shutil.copy(os.path.join(testvoices, n), os.path.join(voices_dir(app.data), n))
    app.launch(env_extra={"MORPHONIC_PYTHON": python} if python else {})
    p = app.page
    app.view("voices")
    rows = p.text("#voiceRows") or ""
    result(all(n in rows for n in ("TestVoice40k", "OldV1Voice", "NoPitchVoice")), "the three .pth files are listed", rows[:120])
    result("convert" in rows.lower(), "pth rows offer Convert")
    app.shot("C1_voices_pth")

    def convert(stem, expect, timeout):
        p.eval("window.__toasts.length = 0")
        p.click('[data-conv="%s.pth"]' % stem)
        deadline = time.time() + timeout
        while time.time() < deadline:
            for t in app.toasts():
                low = t.lower()
                if "conversion failed" in low or "is ready" in low:
                    return t if expect.lower() in low else ("UNEXPECTED: " + t)
            time.sleep(0.5)
        return None

    t = convert("OldV1Voice", "this is v1", 120)
    result(bool(t) and not t.startswith("UNEXPECTED"), "a v1 voice is refused with a message saying so", t or str(app.toasts()[-2:]))
    t = convert("NoPitchVoice", "without pitch", 120)
    result(bool(t) and not t.startswith("UNEXPECTED"), "a no-f0 voice is refused with a message saying so", t or str(app.toasts()[-2:]))
    t0 = time.time()
    t = convert("TestVoice40k", "is ready", 900)
    result(bool(t), "a v2 voice converts to .onnx (%.0f s)" % (time.time() - t0), t or str(app.toasts()[-2:]))
    result("converted TestVoice40k.pth in-app" in app.boot_log(), "the conversion ran inside the app (no Python)")
    result(os.path.exists(os.path.join(voices_dir(app.data), "TestVoice40k.onnx")), "TestVoice40k.onnx exists")
    app.send({"action": "getState"})
    time.sleep(1)
    result(not p.eval("!!document.querySelector('[data-conv=\"TestVoice40k.pth\"]')"), "the .pth row disappears once converted")
    app.shot("C2_voices_converted")

    p.eval("window.__toasts.length = 0")
    p.eval("importFiles([new File([new Uint8Array(3 * 1024 * 1024)], 'Dropped.onnx')]); true")
    # 1.0.2: an export that arrives (drop, dialog, Find voices) is made the
    # active voice at once and says so, instead of only "added".
    t = app.wait_toast("Dropped is in your library", 60)
    result(bool(t), "a dropped file streams into the library", t or str(app.toasts()))
    time.sleep(1)
    result(os.path.getsize(os.path.join(voices_dir(app.data), "Dropped.onnx")) == 3 * 1024 * 1024 if os.path.exists(os.path.join(voices_dir(app.data), "Dropped.onnx")) else False, "the dropped file arrived complete (3 MB)")
    chosen = json.load(open(os.path.join(app.data, "settings.json"), encoding="utf-8")).get("VoiceId", "")
    result(chosen == "Dropped.onnx", "the dropped voice becomes the active voice", "VoiceId=%s" % chosen)
    result(p.eval("!!document.querySelector('[data-delv=\"Dropped.onnx\"]')"), "the dropped voice is listed")
    p.click('[data-delv="Dropped.onnx"]')
    time.sleep(1)
    p.eval("window.__toasts.length = 0")
    p.eval("importFiles([new File([new Uint8Array(10)], 'notes.txt')]); true")
    t = app.wait_toast("Drop a .pth", 10)
    result(bool(t), "a dropped non-voice file is refused", t or "")

    # Find voices: search, list a repository's files, download one
    p.set_value("#findQuery", "VoiceConversionWebUI")
    p.click("#btnFind")
    p.wait("!!document.querySelector('[data-files=\"lj1995/VoiceConversionWebUI\"]')", timeout=60, what="search results")
    result(True, "Find voices lists Hugging Face results")
    p.click('[data-files="lj1995/VoiceConversionWebUI"]')
    p.wait("!!document.querySelector('[data-dlfile=\"pretrained_v2/f0G40k.pth\"]')", timeout=60, what="file list")
    result(True, "Show files lists the repository's .pth files")
    app.shot("C1b_find_voices")
    p.eval("window.__toasts.length = 0")
    p.click('[data-dlfile="pretrained_v2/f0G40k.pth"]')
    t = app.wait_toast("f0G40k.pth added", 300)
    result(bool(t), "a library file downloads into the voices folder", t or str(app.toasts()[-2:]))
    result(os.path.exists(os.path.join(voices_dir(app.data), "f0G40k.pth")), "f0G40k.pth is in the voices folder")
    # a raw training generator is not a voice: the automatic conversion falls back to the Python tool, which says so
    t = app.wait_toast("conversion failed", 300)
    result(bool(t) and "generator" in (t or ""), "a downloaded training generator is refused with a reason", t or str(app.toasts()[-2:]))
    p.wait("!!document.querySelector('[data-delv=\"f0G40k.pth\"]')", timeout=10, what="f0G40k row")
    p.click('[data-delv="f0G40k.pth"]')
    time.sleep(1)

    if p.eval("!!document.querySelector('[data-use=\"TestVoice40k.onnx\"]')"):
        p.click('[data-use="TestVoice40k.onnx"]')
    else:
        app.send({"action": "setVoice", "id": "TestVoice40k.onnx"})
    time.sleep(1)
    settings = json.load(open(os.path.join(app.data, "settings.json"), encoding="utf-8"))
    result(settings.get("VoiceId") == "TestVoice40k.onnx", "Use makes the converted voice active")
    app.view("voice")
    p.click("#btnStartStop")
    p.wait("document.getElementById('btnStartStopText').textContent.toLowerCase().indexOf('stop') >= 0", timeout=60, what="running with the converted voice")
    time.sleep(4)
    result("TestVoice40k" in app.boot_log().split("voice started:")[-1][:200], "the session runs with the converted voice")
    app.view("voices")
    p.eval("window.__toasts.length = 0")
    p.click('[data-delv="TestVoice40k.onnx"]')
    t = app.wait_toast("stop", 10)
    result(bool(t) and os.path.exists(os.path.join(voices_dir(app.data), "TestVoice40k.onnx")), "deleting the active voice while running is refused", t or "")
    app.view("voice")
    p.click("#btnStartStop")
    p.wait("document.getElementById('btnStartStopText').textContent.toLowerCase().indexOf('start') >= 0", timeout=30, what="stopped")
    app.view("voices")
    p.click('[data-delv="TestVoice40k.onnx"]')
    time.sleep(1.5)
    result(not os.path.exists(os.path.join(voices_dir(app.data), "TestVoice40k.onnx")), "after Stop the delete goes through")
    app.view("voice")
    p.eval("window.__toasts.length = 0")
    if not p.eval("document.getElementById('btnStartStop').disabled"):
        p.click("#btnStartStop")
    t = app.wait_toast("choose a voice", 5)
    result(bool(t) or p.eval("document.getElementById('btnStartStop').disabled"), "with no voice chosen, Start asks to choose one", t or "Start disabled")
    app.shot("C3_no_voice")
    app.stop()

    # without Python: the Convert toast must name the pip command
    app.launch(env_extra={"MORPHONIC_PYTHON": "", "PATH": r"C:\Windows\System32"})
    p = app.page
    app.view("voices")
    p.eval("window.__toasts.length = 0")
    p.click('[data-conv="OldV1Voice.pth"]')
    t = app.wait_toast("conversion failed", 120)
    result(bool(t) and "v1" in (t or ""), "without PyTorch a v1 voice is still refused in-app", t or str(app.toasts()[-2:]))
    app.stop()
    for n in ("OldV1Voice.pth", "NoPitchVoice.pth", "TestVoice40k.pth"):
        try:
            os.remove(os.path.join(voices_dir(app.data), n))
        except OSError:
            pass


# ── E. lifecycle ─────────────────────────────────────────────────────

def phase_lifecycle(app):
    print("== E. instances, tray, crash recovery")
    app.launch()
    p = app.page
    app.send({"action": "setVoice", "id": "sample-voice-40k.onnx"})
    time.sleep(1)
    second = subprocess.Popen([app.exe, "--data-dir", app.data], creationflags=subprocess.DETACHED_PROCESS)
    time.sleep(4)
    result(second.poll() is not None and app.alive() == 1, "a second instance exits on its own, one process remains", "exit %s, %d process(es)" % (second.poll(), app.alive()))

    # close hides to the tray; the process (and a running voice) survive
    app.view("voice")
    p.click("#btnStartStop")
    p.wait("document.getElementById('btnStartStopText').textContent.toLowerCase().indexOf('stop') >= 0", timeout=60, what="running")
    subprocess.run(["powershell", "-NoProfile", "-Command", "(Get-Process Morphonic).CloseMainWindow() | Out-Null"], capture_output=True)
    time.sleep(3)
    visible = subprocess.run(["powershell", "-NoProfile", "-Command",
                              "Add-Type -Name W -Namespace N -MemberDefinition '[DllImport(\"user32.dll\")] public static extern bool IsWindowVisible(IntPtr h);'; "
                              "[N.W]::IsWindowVisible((Get-Process Morphonic).MainWindowHandle)"], capture_output=True, text=True).stdout.strip()
    result(app.alive() == 1, "Close keeps the process alive (tray)")
    result(visible.lower() in ("false", ""), "Close hides the window", "IsWindowVisible=" + visible)
    still_running = p.eval("document.getElementById('btnStartStopText').textContent.toLowerCase().indexOf('stop') >= 0")
    result(still_running, "the voice keeps running while hidden")
    p.click("#btnStartStop")
    time.sleep(2)
    app.stop()

    # crash sentinel: a boot that never finished
    open(os.path.join(app.data, "boot.inprogress"), "w").close()
    app.launch(extra_args=["--auto-start"])
    p = app.page
    t = app.wait_toast("didn't start cleanly", 15)
    result(bool(t), "after an unfinished boot a toast says so", t or str(app.toasts()))
    time.sleep(4)
    result("PreviousStart" in app.error_log(), "error.log records the unfinished start")
    result("voice started:" not in app.boot_log(), "--auto-start is ignored on that safe boot")
    app.shot("E1_safe_boot")
    app.stop()
    app.launch()
    result("PreviousStart" not in app.boot_log() and not app.wait_toast("didn't start cleanly", 3), "the launch after that is normal again")
    app.stop()

    # a corrupt settings file is recovered
    sp = os.path.join(app.data, "settings.json")
    good = open(sp, encoding="utf-8").read()
    with open(sp, "w", encoding="utf-8") as f:
        f.write("{ this is not json")
    app.launch()
    log = app.boot_log()
    line = [l for l in log.splitlines() if l.startswith("settings from:")]
    result(bool(line) and "settings file" not in line[0].split(":", 1)[1].strip()[:13] or "backup" in (line[0] if line else "") or "default" in (line[0] if line else ""),
           "corrupt settings.json falls back to the backup or defaults", line[0] if line else "no line")
    result(app.page.visible("#view-voice") or app.page.visible("#view-firstrun"), "the app still opens its window")
    app.stop()
    with open(sp, "w", encoding="utf-8") as f:
        f.write(good)


# ── F / settings ─────────────────────────────────────────────────────

def phase_settings(app, published_exe):
    print("== F. settings, about, update simulation")
    app.launch()
    p = app.page
    app.view("settings")
    p.click("#btnDefaults")
    time.sleep(1.5)
    t = app.wait_toast("recommended", 5) or app.wait_toast("default", 3)
    settings = json.load(open(os.path.join(app.data, "settings.json"), encoding="utf-8"))
    result(settings.get("BlockMs") in (160, 250, 350, 500), "Recommended defaults wrote a block size", "BlockMs=%s ExtraMs=%s" % (settings.get("BlockMs"), settings.get("ExtraMs")))
    app.shot("F1_settings")
    p.click("#btnDocReadme")
    time.sleep(1)
    readme = p.text("#docReadme") or ""
    result(len(readme) > 1000 and "Morphonic" in readme, "About: Read me expands with its text")
    p.click("#btnDocLicense")
    time.sleep(0.5)
    result("MIT" in (p.text("#docLicense") or ""), "About: License shows the MIT text")
    p.click("#btnDocNotice")
    time.sleep(0.5)
    result("ContentVec" in (p.text("#docNotice") or ""), "About: Third-party notice names ContentVec")
    about = p.text("#view-settings") or ""
    result(re.search(r"1\.0\.\d+", about) is not None, "About shows the version")
    app.shot("F2_about")
    result(p.visible("#btnInstallConverter"), "Settings shows the fallback-converter install button")
    p.click('#segAccel button[data-v="cpu"]')
    t = app.wait_toast("restart", 5)
    result(bool(t), "changing acceleration offers a restart", t or "")
    p.click('#segAccel button[data-v="auto"]')
    app.stop()

    if published_exe and os.path.exists(published_exe):
        app.launch(exe=published_exe)
        p = app.page
        log = app.boot_log()
        result("components: ready" in log, "update simulation: the published exe on the same data folder finds the components")
        result("voice:" in log and "(none chosen)" not in log, "update simulation: the chosen voice survives")
        app.view("settings")
        result(re.search(r"1\.0\.\d+", p.text("#view-settings") or "") is not None, "published exe reports its version")
        app.shot("F3_published_exe")
        app.stop()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--exe", required=True)
    ap.add_argument("--data", required=True)
    ap.add_argument("--out", default=None)
    ap.add_argument("--phase", default="all")
    ap.add_argument("--testvoices", default="", help="folder with TestVoice40k.pth, OldV1Voice.pth, NoPitchVoice.pth (phase voices)")
    ap.add_argument("--python", default="", help="python with torch for the conversion (MORPHONIC_PYTHON)")
    ap.add_argument("--published", default="", help="the published Morphonic.exe for the update simulation")
    a = ap.parse_args()
    out = a.out or os.path.join(a.data, "..", "ui-results")
    os.makedirs(out, exist_ok=True)
    app = App(os.path.abspath(a.exe), os.path.abspath(a.data), os.path.abspath(out))
    App.kill_all()
    phases = ["first", "models", "voice", "voices", "lifecycle", "settings"] if a.phase == "all" else a.phase.split(",")
    try:
        for ph in phases:
            if ph == "first":
                phase_first(app)
            elif ph == "models":
                phase_models(app)
            elif ph == "voice":
                phase_voice(app)
            elif ph == "voices":
                phase_voices(app, a.testvoices, a.python)
            elif ph == "lifecycle":
                phase_lifecycle(app)
            elif ph == "settings":
                phase_settings(app, a.published)
    except Exception as ex:
        result(False, "phase aborted", repr(ex))
        try:
            if app.page:
                app.shot("ZZ_abort")
        except Exception:
            pass
    finally:
        app.stop()
    failed = [r for r in RESULTS if not r[0]]
    with open(os.path.join(out, "results.txt"), "a", encoding="utf-8") as f:
        for ok, name, detail in RESULTS:
            f.write("%s %s%s\n" % ("PASS" if ok else "FAIL", name, (" - " + detail) if detail else ""))
    print("%d checks, %d failed" % (len(RESULTS), len(failed)))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
