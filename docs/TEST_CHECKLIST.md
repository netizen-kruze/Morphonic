# Morphonic — live test checklist

The dev build is `src\Morphonic\bin\Debug\net9.0\Morphonic.exe` (Windows) or
`src/Morphonic/bin/Debug/net9.0/Morphonic` (Linux); the shippable artifacts are
`releases\Morphonic-<version>-win-x64.zip` (a single `Morphonic.exe`) and
`releases/Morphonic-<version>-linux-x64` (one file). The version is also shown
in-app under Settings → About. Record results at the bottom with date and
commit.

Automated coverage (run first): `dotnet test` (DSP against the Python
reference, settings, sentinel, pack planning, device parsing; the ONNX
stages too when `MORPHONIC_TEST_MODELS` points at the models), `tools/smoke.*`
(boot, and the voice on the default devices with `-Models`), `--bench` and
`--convert` (measurable output without a microphone). Everything below is
what only a person at the machine can judge.

## A. First run & models

Simulate a fresh machine: quit Morphonic, rename the data folder away
(`%APPDATA%\Morphonic` / `~/.local/share/Morphonic`; restore after this section).

- [ ] Launch: first-run screen shows (two cards), Start is disabled, the
      header chip reads idle.
- [ ] **Set up voice conversion**: downloads the encoder (its bar then
      shows "Assembling" for a few seconds) and the pitch model with one
      progress bar; when done the Voice screen replaces first-run. Voices
      is empty with the import hint.
- [ ] **Add the sample voice**: the same plus the sample voice; it becomes
      the active voice automatically and Start enables once a microphone
      is listed.
- [ ] Models screen: hardware banner shows the tier, the acceleration in
      use and (CPU machines with a GPU the pack cannot use) why; download
      shows live progress + Cancel; Cancel stops it; Delete removes and the
      row returns to Download; a required component deleted puts the Voice
      screen back to first-run.
- [ ] **Verify installed files**: toast reports all files verified. Corrupt
      test (optional): append a byte to a model file → Verify names it.
- [ ] GPU pack: download completes, toast offers restart; after restart the
      header shows `DirectML` (Windows) / `CUDA` (Linux) in the session
      label and `last_boot.log`'s `acceleration:` line says the pack loaded.
- [ ] Disconnect the network in the middle of a download → it fails within
      about a minute with "no data received"; retrying continues from where
      it stopped (the progress label starts above 0 %).

## B. Voice core

- [ ] Start voice with the sample voice → the You meter follows your speech,
      the Voice meter follows the output, the header chip reads "keeping up"
      (green) on capable hardware.
- [ ] Listen through the Monitor output: the converted voice is intelligible,
      follows your pitch and rhythm, no clicks at block boundaries during
      sustained vowels.
- [ ] Pitch slider: +12 doubles the perceived pitch live (no restart), −12
      halves it.
- [ ] Block size change while running → the voice restarts on the new
      setting (brief gap), the pipeline delay in the stage text changes.
- [ ] Noise gate at −40 dB: room noise between words no longer produces
      sound; speech still passes.
- [ ] Loudness follows you = Fully: whispering produces a quiet voice,
      shouting a loud one. Off: level stays roughly constant.
- [ ] Unplug / disable the microphone mid-session → the voice stops with
      the "microphone capture failed" toast (not silent dead air).
- [ ] Force the CPU (Settings → Acceleration → CPU, restart) and pick 160 ms
      blocks on a slow machine → the chip turns red and after ~10 s a toast
      offers the fix (larger block / less context / GPU); pressing it
      applies and restarts.
- [ ] Speed check (voice stopped): a row for the chosen voice; the verdict
      matches how the live session felt; `bench.log` holds the same row plus
      the machine line.

## C. Voices

- [ ] Import an `.onnx` voice: appears in the library with its sample rate;
      Use makes it active; Start converts with it.
- [ ] Drop a `.pth` onto the Voices screen (or a `.zip` from a voice
      library): the drop zone highlights while dragging, a "receiving…"
      line counts up, the voice appears, converts by itself within a few
      seconds (no Python involved: `last_boot.log` says "converted … in-app")
      and the `.pth` row is replaced by the `.onnx`. A dropped `.txt` is
      refused with a toast.
- [ ] Import voice… (file dialog) does the same for a chosen file.
- [ ] Find voices: Search lists Hugging Face results, Show files lists a
      repository's `.pth`/`.onnx`/`.zip`, Download shows a bar and the file
      lands in the library; the three library links open the browser.
- [ ] A v1 or no-f0 `.pth`: refused with a message saying so, in-app.
- [ ] A `.pth` with an unusual configuration: Convert falls back to the
      Python tool; without PyTorch the toast names Settings → Conversion →
      Install fallback converter, and that button installs the packages
      with pip (needs Python 3 on the machine).
- [ ] A w-okada / RVC WebUI `.onnx` export: loads; "rate detected on first
      use" shows until Start, then the session label shows the rate.
- [ ] Delete the active voice while running → refused; stop, delete → the
      Voice screen says to choose a voice.
- [ ] Open folder opens the voices folder in the file manager.

## D. Routing

- [ ] **Windows + VB-CABLE**: Output = CABLE Input; in Discord/VRChat pick
      CABLE Output as the mic → others hear the converted voice. Without a
      cable, the first-time note about VB-CABLE appears once.
- [ ] **Linux**: with Virtual microphone on, `pactl list short sinks` shows
      `morphonic_voice` and `pactl list short sources` shows `morphonic_mic` while
      Morphonic runs; both vanish on exit. Discord/OBS/VRChat (Proton) list
      "Morphonic-Voice-Mic" and hear the converted voice. Output left on System
      default routes to the virtual sink automatically.
- [ ] Monitor = headphones: you hear the converted voice; Monitor = Off
      stops it; the main output is unaffected.
- [ ] Device unplugged while idle → Rescan (changing a device) lists the new
      set; a saved device that is gone resolves to System default.

## E. Window, tray, instances, crash recovery

- [ ] Windows: Close (✕) → window hides, tray icon remains, the voice keeps
      running. Left-click tray → window returns, state intact. Tray →
      Exit quits fully (microphone released).
- [ ] Linux: Close quits; the virtual microphone is removed; `kill` or
      Ctrl+C from a terminal does the same cleanly (session summary in
      `last_boot.log`).
- [ ] Launching a second copy → no second window (Windows: silent; Linux: a
      notification).
- [ ] Crash recovery: with Morphonic closed, create an empty `boot.inprogress`
      in the data folder, then launch → a red toast says the last start
      didn't finish, `--auto-start` is ignored, `error.log` has a
      "PreviousStart" entry. The launch after that is normal again.
- [ ] Settings → Add to app grid (Linux): the entry appears with the icon;
      launching from the grid works; `--uninstall` removes it.
- [ ] Settings → About → Open folder (next to the data folder path): the file
      manager opens the data folder, with `error.log` and `last_boot.log` in it.

## F. Portable builds (ideally on a second machine)

- [ ] Windows zip: a single `Morphonic.exe`, runs with no install; About shows
      the matching version; Read me / License / Third-party notices expand
      and their text is selectable.
- [ ] Fresh Windows machine without WebView2 → the dialog points to
      Microsoft's installer.
- [ ] Linux file: `chmod +x`, runs; a machine without `webkit2gtk4.1` gets
      the dnf hint instead of a crash; `tools/smoke.sh <binary>` passes.
- [ ] Update simulation: replace the binary only → settings, voices,
      components and the GPU pack survive.
- [ ] Settings → About → Check for updates: with the network on, the row
      says either "You have the latest version" or names the newer version
      with the matching file for this platform, and "Open download page"
      opens the GitHub release in the browser; with the network off it says
      "Could not check" and nothing else changes. No request is made before
      the button is pressed (`last_boot.log` has no "update check" line).
- [ ] Offline build (`-offline`): with the network off, first run says
      "included in this build", Set up finishes in seconds without a
      download, Verify passes, and the Models buttons read Unpack.

---
Record results here with date + commit.
