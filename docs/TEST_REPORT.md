# Test report — 1.0.0, Windows 11, 2026-10-08

Machine: AMD Ryzen 7 9800X3D, 61 GB RAM, NVIDIA GeForce RTX 5080 (16 GB),
Windows 11 Pro 25H2 (26200.9457), .NET 9.0.18, WebView2 present.
Artifacts tested: `releases/Morphonic-1.0.0-win-x64.zip` (the published
`publish\Morphonic.exe`), the offline variants, and
`releases/Morphonic-1.0.0-linux-x64` in a Fedora 44 WSL distribution (see
the Fedora section; bare-metal Linux is under "Not covered").

## Summary

| Layer | How | Result |
|---|---|---|
| Unit tests (`dotnet test`) | DSP vs Python reference, ONNX stages on the assembled models, checkpoint reader, model assembly reproduces the pinned hashes byte for byte, zip and streamed import, settings, sentinel, device parsing | 48 / 48 pass |
| Release smoke (`tools/smoke.ps1 -Models`) | published exe, throwaway data folder, voice on the default devices | 10 / 10 pass, 0 underruns |
| First-run download (`--fetch-models`, and the UI cards) | real downloads from Hugging Face, in-app assembly, pinned hashes | all three files verified; 812 MB in about 30 s |
| UI walkthrough (`tools/ui_walkthrough.py --phase all`) | the real window driven over WebView2's DevTools port: 90 checks across the checklist's sections A–F | 90 / 90 pass after the fixes below (the last run before the final fix was 89 / 90) |
| Offline conversion (`--convert`) | a recording through the live pipeline | output pitch follows input (log-f0 correlation 0.94; +12 st gives ratio 2.006) |
| Speed check (`--bench`) | the bundled 14 s clip | CPU 120 ms per 250 ms block (load 0.48); DirectML 22 ms (load 0.09) |

## Re-verified after the rename to Morphonic

The project was renamed late on the same day (code, data folder, registry
key, mutex, virtual-microphone names, metadata keys, embedded templates
and their pinned hashes). After the rename: unit tests 48 / 48, smoke test
10 / 10, and the first-run, voices and settings walkthrough phases 52 / 53
on the published `Morphonic.exe`. The one miss ("the chosen voice
survives" an update) is an artifact of running the settings phase right
after the voices phase, which ends with no voice chosen; the full run above
covers that check.

## Offline build

`build.ps1 -Offline` produced `Morphonic-1.0.0-win-x64-offline.zip` (672 MB
zipped, 893 MB exe) and `Morphonic-1.0.0-linux-x64-offline` (856 MB), each
packing the three verified model files behind the binary. On the Windows
offline exe: the first-run walkthrough phase passed 20 / 20 with set-up
finishing in one second from the included files ("included in this build",
no download, Unpack buttons), and the smoke test passed 10 / 10 with the
voice running on the real devices. Unit tests: 49 / 49 with the new
payload test.

## Fedora 44 (WSL 2 + WSLg, RTX 5080 through /dev/dxg)

The Linux binary (`Morphonic-1.0.0-linux-x64`) ran in a Fedora 44 WSL
distribution with WSLg, as the user "tester", after `dnf install gtk3
libnotify webkit2gtk4.1 pulseaudio-utils pipewire-utils`:

| Check | Result |
|---|---|
| `--help`, `--fetch-models` (files present: verify only), `--convert` | ok; verify 3 / 3 |
| Speed check, CPU | 140 ms per 250 ms block (load 0.56), usable |
| Speed check, CUDA (after `--install-gpu`, 1.9 GB) | 35 ms per 250 ms block (load 0.14), fast; "loaded the bundled CUDA 12 runtime" |
| `tools/smoke.sh` (WebKitGTK window, voice on the default devices) | 10 / 10 pass, 0 underruns |
| Live session, virtual microphone on | `morphonic_voice` sink and `morphonic_mic` source appear while running and are removed on exit; output routed to the sink |
| 45 s live session, CPU | 164 passes, 0 underruns, 0 ms skipped |
| 20 s live session, CUDA, output to the virtual microphone | 50 passes, avg 39 ms, 0 underruns, 0 ms skipped |
| SIGTERM from a terminal | clean exit with the session summary |
| Second instance | exits with "already running" (instance.lock) |
| `--install` / `--uninstall` | desktop entry, icon and app copy created and removed |
| Corrupt `settings.json` | restored from `settings.json.bak` |

WSL has a PipeWire daemon but no PipeWire audio nodes, so capture and
playback fell back to `parec`/`pacat` against WSLg's PulseAudio, which is
the path a PulseAudio-only desktop would take. At session start on WSL the
engine dropped about four seconds of backlog once (the RDP source hands
over a burst of buffered audio) and ran clean afterwards.

Two Linux bugs found and fixed here:

- **CUDA failed on the pitch model**: the pack omitted cuDNN's RNN
  library (`libcudnn_adv.so.9`), which ONNX Runtime needs for the GRU
  layers in RMVPE (`CUDNN_STATUS_NOT_SUPPORTED_SUBLIBRARY_UNAVAILABLE`).
  It is now part of the pack; the CUDA speed check above is from the fixed
  build.
- The rail's "✕ quits" note rendered a missing-glyph box with Fedora's
  default fonts; it now uses "×".

## Final rebuild (release audit)

A pre-release audit added an **Open folder** button beside the data folder
path in Settings → About, so bug reporters can reach `error.log` and
`last_boot.log` in one click (the handler opens only the app's own data
folder; the page cannot supply a path). All four release files were rebuilt
afterwards: unit tests 49 / 49, the Windows smoke test 10 / 10 on the
rebuilt exe, and a DevTools-driven check that the button is present in the
shipped page and opens an Explorer window on the data folder with nothing
in `error.log`. The audit also confirmed: no update check or network call
the user did not trigger (new versions ship as a fresh download), the exe's
version resource names only Morphonic, the old project name appears nowhere
in the tree or the binaries, and every network host in the code is one of
Hugging Face, NuGet, PyPI or the WebView2 download page.

## Update check

Settings → About gained **Check for updates**: one request to GitHub's
latest-release endpoint for the project's repository, made only on the
button press, answered in the row; **Open download page** opens the release
in the browser (github.com joined the link allow-list). The app never
downloads or replaces itself. Unit tests cover the reading of the release
document (newer / equal / older tags, with and without the `v` prefix, the
plain asset for the platform chosen over the offline one, malformed
documents refused, non-GitHub links dropped): 54 / 54. A DevTools-driven
run against a local fake feed (`--update-feed`, a test hook) passed
12 / 12: no request before the press, a newer release announced with the
Windows file and the notes, an equal release reported as latest, a dead
feed reported as "Could not check" with a note (not an exception) in
`error.log`, and the button usable again afterwards. One bug fixed on the
way: the first build sent the answer with capitalised field names the page
did not read. `build.ps1` / `build.sh` now also write
`releases/SHA256SUMS.txt` for the release page.

Against the live feed: the project was published at
https://github.com/netizen-kruze/Morphonic and release **v1.0.0** created
from the `main` commit the four files were built from, with the four files
and `SHA256SUMS.txt` attached (each upload's size checked against the local
file). While the release was still a draft the shipped exe reported "Could
not check: no release has been published yet" with a note in `error.log`;
once published, the same exe reported "You have the latest version (1.0.0)"
and `last_boot.log` got "update check: 1.0.0 is the latest".

## README screenshot

`docs/screenshot.png` was retaken from the published 1.0.0 exe through the
DevTools hook: the Voice screen with the sample voice running on the default
devices (CPU, 250 ms blocks), the "Converting" status with its lag and
pass-time readout, the "keeping up" pace chip, and the toasts cleared so
nothing covers the layout. The earlier image showed the idle screen behind
three first-run toasts and the pre-fix tray glyph. The same image was added
to the v1.0.0 release notes under the title line, served from the
repository at the commit that added it so the notes keep showing this exact
picture.

## 1.0.1 — the voices workflow, audited and reworked

The hand-out of 1.0.0 surfaced the one thing the app is for: getting a
voice and speaking with it was not obvious. The installed app's
`last_boot.log` showed the sequence: a Hugging Face download of
`binant/SpongeBob_SquarePants__RVC_v2_/model.pth` arrived as a voice called
"model" (every library names its checkpoint `model.pth`), the library list
sat above the search results out of view, the toast said "press Convert"
although conversion is automatic, and the same file was downloaded twice
because nothing said it had arrived. Changes, all driven through the real
window afterwards:

- **Two halves on the Voices screen.** *My voices* (the library: Use /
  Delete / Open folder, with the active voice marked) opens first;
  *Get voices* holds every way in: Find voices on Hugging Face, the sample
  voice (moved here from the Models screen), Import voice… and the drop
  zone, and the library links. A *Get voices…* button in the library
  header and the tab control switch between them.
- **Names.** A generic checkpoint name (`model.pth`, `G_2333.pth`,
  `pytorch_model.pth`…) is replaced by the name of where it came from: the
  repository, the zip, or the folder it was picked from, plus a sub-folder
  when a repository holds several ("Pack Alice", "Pack Bob"). A
  descriptive file name is kept. The file list under Show files says which
  name each file will get.
- **Arrival.** A downloaded or dropped voice is converted on its own and,
  when ready, becomes the active voice (if a voice is running the toast
  asks for Use instead); the page switches to My voices and flashes the
  row into view. The Hub button turns into *Use*, and pressing it for a
  voice already in the library chooses it without downloading again.

Verified on the 1.0.1 exe: unit tests 56 / 56 (naming rules, zip naming);
the real download flow against Hugging Face 11 / 11 (the file list shows
"model.pth → SpongeBob SquarePants RVC v2.pth", the voice appears under
that name, converts, becomes active, the row is flashed, no file called
`model.*` in the library, Use again makes no second download, and *Start
voice* runs it at 32 kHz with nothing in `error.log`); the two-tab layout
9 / 9 (opens on My voices, Get voices holds search / sample / import /
drop zone, the Models screen no longer lists voices).

**Sidetone.** The Settings "Monitor" output became **Hear yourself** on
the Voice screen, beside Microphone and Output: *Auto* (the default) plays
the converted voice on the system default output whenever the main output
is a virtual cable (VB-CABLE on Windows, the virtual microphone on Linux),
where the speaker would otherwise hear nothing; *Off*; or a named device.
`last_boot.log`'s "voice started" line now ends with `sidetone: …`.
Driven check 9 / 9: the control with Auto / Off / devices, Auto silent on a
real output, a chosen device opening a second WASAPI output during a live
session, the choice saved and Off saved as off rather than auto.

**Raw training generators.** A `G_*.pth` / `f0G*.pth` from training (the
generator under `model` with the optimizer state) is now refused in-app
with a message naming RVC WebUI's / Applio's export step, instead of being
handed to the Python tool, which without PyTorch produced a traceback.
The no-PyTorch message for a genuinely non-standard checkpoint now points
at Settings → Conversion → Install fallback converter.

The full walkthrough on the 1.0.1 exe: first run / models / voice 47 / 47,
voices 23 / 23 (without any Python on the path), lifecycle 10 / 10,
settings 10 / 10 — 90 / 90.

**A Windows virtual microphone of Morphonic's own.** Other programs can
use the converted voice only through a microphone device, and on Windows
a device is a kernel driver. Rather than require a second download
(VB-CABLE), the app now carries its own cable driver,
`driver/MorphonicCable` (forked from the MIT AudioMirror driver, itself
from Microsoft's sysvad sample): an output "Morphonic Voice" whose audio
appears on an input "Morphonic Microphone". Settings → Virtual microphone
gained Install / Remove on Windows: the app unpacks the embedded package
and runs a second copy of itself elevated (one administrator prompt) that
creates the root-enumerated device and installs the driver through
SetupAPI, the same sequence as `devcon install`; Remove deletes the device
and the package from the driver store. With the virtual microphone on and
Output on System default the voice is routed into Morphonic Voice, and
*Hear yourself* (Auto) treats it as a virtual output.

What is verified here: the driver **builds** from source — with the
Enterprise WDK 10.0.28000 (Visual Studio 2026 Build Tools 18.3, mounted
from Microsoft's ISO, `build.ps1 -DriverOnly -Ewdk E:\`), Release x64,
`MorphonicCable.sys` 42 KB, INF stamped 1.0.1.0 with `PnpLockdown=1`, the
only build problems being two warnings the inherited sources trip
(`ExAllocatePoolWithTag` deprecated, an empty statement), now excluded
from warnings-as-errors; the package is embedded in the published exe
(`driver/MorphonicCable.{inf,sys,cat}` resources); unit tests 57 / 57; and
a driven run of Settings on this build 6 / 6: the row is shown on Windows
with the Linux toggle hidden, and because the catalog is unsigned it
offers no Install button and explains the signing situation instead,
naming VB-CABLE as the interim route. The two elevated entry points were
also run without elevation from the published exe: `--remove-virtual-mic`
answered "the Morphonic virtual microphone was not installed" (exit 0),
and `--install-virtual-mic <package>` stopped at the first SetupAPI call
with "Access is denied" (exit 1), leaving no device behind — the
administrator boundary holds. What is **not** verified: loading the driver.
Windows loads a kernel driver only when Microsoft has signed it through
the Hardware Dev Center (EV certificate + attestation signing,
`driver/README.md`), a step only the publisher can take; this test
machine keeps Secure Boot and signature enforcement as they are, so an
unsigned local build cannot be exercised end to end. The Linux virtual
microphone was verified in the Fedora run. Until a signed package ships, a
third-party cable (VB-CABLE) remains the Windows route, and
`mic_check.py` (choose CABLE Input through the real control, start the
voice, record CABLE Output as Discord would, measure the level) is ready
for a machine that has one.

**Decision: no signing, no third-party driver inside the app.** The
publisher will not take the Hardware Dev Center route, and the one
Microsoft-signed cable whose licence allows embedding (VB-CABLE) would
need its author's written agreement, which is not in hand, so it was not
added. The app keeps the complete install path for a signed package of
its own (and a slot for VB-CABLE should that agreement come), and ships
without either. For users the Settings row now says exactly that and
offers *Get VB-CABLE…* (opens vb-audio.com); a VB-CABLE the user installs
is detected by its hardware id, the automatic-routing switch appears, and
with Output on System default the voice plays into "CABLE Input".

## 1.0.1 on Linux (Fedora 44, WSL 2 + WSLg)

Both 1.0.1 Linux files were run in the Fedora 44 distribution after the
1.0.1 changes: `Morphonic-1.0.1-linux-x64` prints its help, benches
(177 ms per 250 ms block on the CPU share WSL gives it), starts a 25 s
auto-started session (virtual microphone created, sidetone Auto on the
default output, 82 passes), passes `tools/smoke.sh` 10 / 10, and shows the
first-run screen on an empty data folder; `Morphonic-1.0.1-linux-x64-offline`
keeps working with its 850 MB trailer (`--help`), unpacks the three
included model files on `--fetch-models` with no network (all three
verified against the pinned hashes) and starts with them. Nothing in
`error.log` in any run.

## Release 1.0.1 published

v1.0.1 was published from the draft on 2026-10-09 (UTC) with the four
files and `SHA256SUMS.txt`, tag on the `main` commit the files were built
from; GitHub's latest-release feed answers with it.

## "VRCNext goes black when Morphonic opens" — the test driver, not the app

Reported on the development PC, where VRCNext (another Photino app) runs
all day. Reproduced the other way round first: with VRCNext up, Morphonic
was started the plain way from `C:\tools` and VRCNext's window was
screenshotted and its six WebView2 processes listed before and after —
same brightness, same process ids, nothing in its log. The cause was
`tools/ui_walkthrough.py`: its clean-up step killed every WebView2
process whose command line contained "Photino", meant to catch
Morphonic's own lingering browser, and VRCNext's browser, GPU, renderer
and utility processes all carry that word too — so every driven test run
today pulled the browser out from under VRCNext, which is exactly a black
window. The driver now matches only `--webview-exe-name=Morphonic.exe`
and the profile folder under the test's own data folder. Morphonic itself
kills no processes and shares nothing with other apps (own WebView2
profile folder, own single-instance mutex, no environment or registry
overrides).

## Support report

For a tester on another machine: **Settings → About → Save a report…**
writes `Morphonic-report-<date>.zip` into the data folder (last_boot.log,
error.log, bench.log, settings.json, the model manifest, a `system.txt`
with the machine, the desktop / display environment variables, the audio
devices the app sees, the virtual-microphone status, the Linux UI
libraries and audio tools found on the path, and the data-folder listing)
and opens the folder; `Morphonic --report` does the same from a terminal
for the case where the window never opens. Verified: the button in the
real window (toast names the zip, one new zip in the folder), `--report`
on Windows (exit 0, three entries) and on Fedora 44 in WSL (seven lines of
environment, both PipeWire and PulseAudio tools located, `xdg-open` missing
noted — a real finding for a minimal install).

## What the walkthrough exercised

Every check clicks the real buttons and reads the real page, the data
folder and `last_boot.log`:

- **A. First run and models** — first-run cards, the set-up download with
  its Assembling phase, the sample voice, Verify, Delete and re-download,
  Cancel midway (no partial left), a corrupted file named by Verify and
  passing again after repair, a deleted required component bringing
  first-run back, the GPU pack download, the restart from its toast, and
  DirectML active afterwards.
- **B. Voice core** — Start, the "keeping up" chip, pitch changes without a
  restart, block-size change restarting exactly once, gate and loudness
  saved, Stop writing the session summary with its underrun count, the
  speed check from Settings and `bench.log`.
- **C. Voices** — three `.pth` files (a v2 voice, a v1 voice, a no-pitch
  voice): the v2 one converts in-app in 1 s with no Python, the other two
  are refused with the right message; drag-and-drop streaming of a file
  into the library and refusal of a non-voice file; Find voices (Hugging
  Face search, file listing, a verified download); a raw training
  generator refused with a reason; Use, Start with the converted voice,
  delete refused while running and allowed after Stop; no-voice state.
- **E. Lifecycle** — a second instance exits, Close hides to the tray with
  the voice still running, the unfinished-boot sentinel (toast, error.log,
  `--auto-start` ignored, normal on the next launch), corrupt
  `settings.json` recovered from the backup.
- **F. Settings and update** — Recommended defaults, About documents,
  version, the fallback-converter button, acceleration change offering a
  restart, and the published exe on an existing data folder keeping
  components and the chosen voice.

Screenshots of each screen from the run are in the walkthrough's output
folder (`--out`).

## Bugs found by this testing, all fixed

1. **Restart lost the command line.** The restart offered after installing
   the GPU pack started the new process without the original arguments, so
   a `--data-dir` (and the test hooks) were dropped. The new process now
   inherits them.
2. **Blank window when another Photino app runs.** Photino's default
   WebView2 profile folder is shared by every Photino app on the machine,
   and WebView2 refuses to start in a folder already open with different
   browser arguments. The profile now lives in the app's own data folder.
3. **Start stayed disabled after choosing a voice.** The page read the chosen
   voice from the devices payload, which was not re-sent on Use, after a
   conversion, or after deleting the active voice. All three now re-send it.
4. **First-run did not return after deleting a required component**
   (same cause: the readiness flag travels in that payload). Fixed the same
   way.
5. **Conversion toast printed a full temp path and a hash**; long toasts
   could not wrap. Both fixed.
6. **A flaky unit test** (two test classes pointed the data folder at their
   own temporary directories while xunit ran them in parallel). The suite
   now runs its classes serially; it passed three consecutive runs.

## Not covered here

- **Linux on real hardware:** Fedora 44 was tested in WSL 2 with WSLg
  (see the Fedora section), where audio goes through WSLg's PulseAudio
  bridge. PipeWire devices on a bare-metal Fedora desktop, a real
  microphone, and GNOME/KDE window management have not been exercised.
- **Listening judgments** (intelligibility, clicks at block boundaries,
  gate and loudness by ear, VB-CABLE into a real game or call): the
  walkthrough verifies that sessions run clean (0 underruns, 0 skipped)
  but cannot listen. Section B/D of the checklist, by a person.
- **Network failure mid-download** and **unplugging the microphone** were
  not simulated.
- **A machine without WebView2** (the installer prompt) was not available.
