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

# Linux audit run — Ubuntu 24.04, PipeWire 1.0.5, headless, 2026-10-09

Machine: 4-thread Intel Xeon @ 2.8 GHz (no GPU), 10 GB RAM, Ubuntu 24.04.5
in a cloud container, .NET 10 SDK building the net9.0 project (the 9.0
runtime and targeting packs come from NuGet; the published single-file
binary carries .NET 9.0.20). Desktop: Xvfb, a D-Bus session, PipeWire
1.0.5 + WirePlumber 0.4.17 + pipewire-pulse with a null sink
("Test-Speakers") and a remapped source ("Test-Microphone"), so the app
has real PipeWire devices to open. Hugging Face is unreachable from the
box; the three model files were taken out of the published
`Morphonic-1.0.1-linux-x64-offline` (checksum verified against the
release's `SHA256SUMS.txt`) with `--fetch-models`, which exercised the
offline unpack path on Linux. Binary under test: `./build.sh Release`
from the audit branch (commits `a6e7e41`, `eac4048`, `b633a4f`). These
changes are version 1.0.2.

What was done, in order: a static audit of the whole tree; the audit's
fixes; unit tests with the models; a four-lens adversarial review of the
diff (every finding checked by two independent refuters); a 20-scenario
runtime matrix, run twice (the second time against the fixed binary with
a corrected harness); a completeness critic.

## Unit tests on Linux

| Tree | Result |
|---|---|
| `main` (5cae98e), unmodified | 54 / 57 — `Fixtures/tiny_voice.pth` is gitignored (`*.pth`) and never reached the repository (2 failures); the name sanitizer used the platform's invalid-character set, which on Linux is only NUL and `/` (1 failure) |
| 1.0.2 (the audit branch) | 64 / 64 with `MORPHONIC_TEST_MODELS` (ONNX stages against the reference included) |

The fixture is regenerated by `tools/make_test_fixture.py` (torch.save's
layout written with numpy alone) together with its weight-norm reference,
and `.gitignore` now excepts it. `build.sh` and `tools/smoke.sh` were
stored without the executable bit; they have it now.

## Found on Linux and fixed

1. **PipeWire 1.0 rejects `--raw`.** `pw-record` / `pw-play` on PipeWire
   1.0.x (Ubuntu 24.04 LTS) exit at once with "unrecognized option
   '--raw'" (the flag exists from 1.2), so every session silently fell
   back to `parec` / `pacat` — the boot log of the Fedora/WSL run in this
   report shows the same fallback. A pipe is raw on 1.0 anyway, so each
   tool is tried with `--raw` and then without. Verified: `in: pw-record
   (default input); out: pw-play (default output)` on 1.0.5.
2. **A vanished device went unnoticed under WirePlumber.** When the chosen
   microphone or output disappeared mid-session, WirePlumber re-linked the
   stream to the default device and the child process kept running, so a
   voice routed into the virtual microphone played out of the speakers
   with nothing in the logs. Streams aimed at a chosen device now carry
   `node.dont-reconnect`; the tool exits ("target not found") and the
   session ends with "microphone capture failed" / "playback failed".
3. **`--bench` without a chosen voice hung** for its 15-minute timeout:
   the refusal was a toast only, and the terminal path waits on the bench
   channel. Fixed; exits 1 at once with the message.
4. **`--convert --data-dir X`** took the switch as a file name; `--convert`
   was missing from `--help`.
5. **Settings recovery destroyed its own backup**: the restored copy was
   never written back, so the next save moved the damaged file into
   `.bak`. The good copy now replaces the damaged file at once.
6. **A leftover virtual-microphone sink** (from a crashed run) was never
   removed; the first fix adopted any sink carrying the name, which the
   review showed would tear down a running instance's sink from a second
   data folder. Now `virtualmic.json` in the data folder records the pid
   and module ids; a leftover is adopted only when that record names it
   and its process is dead.
7. Smaller: the support report listed its own half-written zip; pace
   advice promised GPU acceleration on a machine without a usable card;
   the Photino comment claimed the WebKitGTK profile lives in the data
   folder (it goes under `~/.local/share/<binary name>` and
   `~/.cache/<binary name>`; `--uninstall --purge` removes them now);
   `instance.lock` stayed behind after a clean exit; the boot log printed
   `''` for the default device and counted the app's own files; a missing
   display left nothing for `--report`; the Python fallback ran with
   `-I`, which hides the user site-packages that "Install fallback
   converter" writes to.

## Runtime matrix

Twenty-eight scripted scenarios, each in its own data folder with the
models hard-linked in. History: run 1 (binary from `a6e7e41`) passed 12 of
the first 20 outright; of the 8 failures, 3 were harness mistakes (SIGINT
ignored by a child started from a non-interactive shell, a per-user marker
the script expected per folder, a manifest the fixture did not link), the
rest were items 2–5 above and a wrong expectation about the WebKitGTK
profile path. Run 2 (binary from `eac4048`) passed the 6 it reached before
the session's agent allowance ran out.

**Full run on 1.0.2** (`releases/Morphonic-1.0.2-linux-x64`, one scenario
after another, the test devices restored before each): all 28 pass, with
s27's read-only-folder part skipped as root. Four failed on the first
pass, each a harness mistake, fixed and run again: s05 still expected any
leftover sink to be adopted (the ownership record came later; it now
plants the record of a dead run, and also checks that a sink without one
is left alone), s06 and s19 matched the summary line with doubled
backslashes, and s21 recorded `morphonic_voice.monitor` with pw-record,
which knows only node names and quietly recorded the default source
instead (it now records the virtual microphone, what other programs
hear). The counts are each scenario's PASS lines, smoke.sh's own included
where it runs.

| # | Scenario | Checks | 1.0.2 |
|---|---|---|---|
| s01/s02 | `tools/smoke.sh` without and with the models | PASS/FAIL lines, pw-record / pw-play in the boot log | 10 / 10, 15 / 15 |
| s03 | 75 s auto-started session on the PipeWire defaults | summary with passes, no error.log, UI and tools unpacked | 8 / 8 |
| s04 | virtual microphone on | `morphonic_voice` / `morphonic_mic` exist while running, listed by pw-dump, output routed into the sink, sidetone Auto on the default output, both gone at exit | 12 / 12 |
| s05 | leftover virtual microphone, with and without the record of a dead run of this folder | adopted and removed at exit, record deleted; without the record used and left in place | 10 / 10 |
| s06 | SIGTERM and SIGINT mid-session; SIGTERM while the voice is still loading | exit 0 within 20 s, "exit requested by", summary written; during the load "start abandoned: the app is closing", no voice started | 16 / 16 |
| s07 | second instance on the same data folder | exits 0 at once with "already running", lock released afterwards | 6 / 6 |
| s08 | damaged settings.json with a good .bak | restored, `.corrupt` kept, good copy written back, first-run marker | 9 / 9 |
| s09 | `--install`, re-install in place, boot from the copy, `--uninstall`, `--purge` | files, desktop entry, icons, data folder kept then removed | 13 / 13 |
| s10 | `--report` | zip contents, PipeWire devices in system.txt, no half-written zip in the listing | 8 / 8 |
| s11 | `--convert` with the bench clip at 0 and +12 semitones | 40 kHz, length, non-silent, f0 ratio ≈ 2 (autocorrelation) | 7 / 7 |
| s12 | `--bench`, and without a chosen voice | row with verdict, bench.log, refusal exits 1 at once | 6 / 6 |
| s13 | `--pack-offline`, then the offline binary's `--fetch-models` with no network | 3 files verified, "included in this build", byte-identical model, repack replaces the payload, smoke.sh passes on it | 12 / 12 |
| s14 | `--fetch-models` with Hugging Face unreachable | exit 1 with a sentence, nothing half-made | 4 / 4 |
| s15 | boot sentinel left behind | safe boot, `--auto-start` ignored once, normal afterwards | 7 / 7 |
| s16 | Steam-like LD_PRELOAD / LD_LIBRARY_PATH | re-exec'd child runs the window, exit 0 | 4 / 4 |
| s17 | no display; `--help`; bad `--convert` / `--pack-offline` arguments | exit 1 with the message and an error.log note; usage, exit 2 | 8 / 8 |
| s18 | chosen microphone, then chosen output, removed mid-session | session ends with the right reason, app keeps running, clean exit | 8 / 8 |
| s19 | 150 s session with the virtual microphone and sidetone | full summary (288 passes, 0 underruns, one sidetone catch-up), memory flat (~1.45 GB RSS), removal logged | 6 / 6 |
| s20 | data folder path with spaces and non-ASCII | session and `--report` work | 5 / 5 |
| s21 | real speech (the bench clip, looped) through the live path into the virtual microphone | recorded at 40 kHz, non-silent, pitched, f0 follows the clip at 0 semitones (median 96 Hz against the clip's 92 Hz, 968 voiced frames) | 7 / 7 |
| s22 | capture tool killed, playback tool killed, recorder frozen (SIGSTOP), each mid-session | app keeps running; "microphone capture failed … exit code 137", "playback failed", "delivered no audio for 5 s"; no stray child | 13 / 13 |
| s23 | PipeWire tools shimmed to fail | voice on parec / pacat, devices through pactl, `--report` lists them | 6 / 6 |
| s24 | `--after` restart handshake | the new instance waits for the old one, then runs | 4 / 4 |
| s25 | a garbage GPU pack in the data folder | no crash, falls back to the CPU with the reason, `--install-gpu` reports the failure | 6 / 6 |
| s26 | pace advice for small blocks, sidetone to a named device and off, a saved device that is gone, `--bench` under `de_DE` | advice names the next block size, sidetone lines, default input and output resolved by name, decimal point kept | 11 / 11 |
| s27 | damaged voice file, crash record via fake coredumpctl / journalctl, read-only data folder | start fails into error.log, no crash; coredumpctl asked for the 15-byte process name, only Morphonic's lines kept; read-only part skipped as root | 6 / 6 |
| s28 | two instances with two data folders and the virtual microphone, dead Wayland socket, SIGHUP, SIGTERM to a Steam re-exec parent | the second uses the sink without adopting it, the first removes it; refusal in words; clean exits, no orphan | 12 / 12 |

Not covered on this box: anything that needs a click in the page (WebKitGTK
exposes no remote-debugging port here, unlike WebView2 on Windows), the
GPU pack (no GPU, NuGet-hosted archives unreachable), real microphones,
bare-metal Fedora with GNOME/KDE window management, and listening.

## Harness, and what is left

The scenario scripts and the headless-desktop setup are in
`tools/linux-scenarios/`: `start-audio.sh` brings up Xvfb, D-Bus and the
PipeWire graph and writes `env.sh` to `$MORPHONIC_SCENARIOS` (default
`/tmp/morphonic-scenarios`, where the runs go too); `scenario.sh` holds the
helpers; each `sNN_*.sh` is one scenario, run as
`MODELS_DATA=<a data folder filled by --fetch-models> bash tools/linux-scenarios/s04_virtual_mic.sh`
against `releases/Morphonic-<version>-linux-x64` (or `BIN=`). The
scenarios share the PipeWire graph and the virtual microphone's names, so
they run one at a time; s05, s18 and s21 add or remove devices and put
them back. Unit tests on the same binary: 64 / 64 with the models.

The start abandoned when the app closes during a voice load ran through
SIGTERM (s06), which calls the same `BeginShutdown` as the window's close
handler; that one-line handler itself needs a window manager to trigger.
Left: the close handler, s27's read-only data folder as a normal user,
and everything under "Not covered" above.

# 1.0.2 checked on both targets and released — Windows 11 and Fedora 44, 2026-10-10

The 1.0.2 work above reached `main` as pull requests #1 and #2. Before the
release it was checked on the two platforms this project is for, neither
of which the audit box could run: Windows 11 (the machine of the 1.0.0
report) and Fedora 44 (WSL 2, kernel 6.18, 16 threads, RTX 5080 visible).
Release files: `.\build.ps1 -Offline` from a clean tree at commit
`5e3c220`, the commit the tag `v1.0.2` points at.

## Found in this pass, fixed before the release

1. **Playback broke where PulseAudio is the sound server and PipeWire's
   newer tools are installed** — the plain WSLg session of the Fedora test
   system, where a socket-activated PipeWire daemon runs beside WSLg's
   PulseAudio with no audio devices. 1.0.2 retried `pw-play` without
   `--raw` after any early failure of the first attempt. On PipeWire 1.2+
   that form takes its input for a sound file, waits for a header, is taken
   for started, and ends the session as soon as audio reaches it ("sndfile:
   failed to open audio file"); the virtual microphone and the sidetone
   went the same way. 1.0.1 fell through to `pacat` and worked. Now the
   tools' own `--help` says which form they take, once, and only that form
   is ever run; and where PulseAudio lists the devices and PipeWire lists
   none, `parec` / `pacat` are tried first. `--report` states both facts.
   The fault never shipped: 1.0.2 had not been published.
2. **A new unit test failed on Windows.** It compared POSIX paths in full,
   which `Path.GetFullPath` roots on the current drive there. The app code
   was right; the test uses each platform's own paths now.
3. **`--uninstall --purge` deleted by the binary's file name**:
   `~/.local/share/<name>` and `~/.cache/<name>` for whatever the running
   binary was called, so a binary renamed to another program's folder name
   would have taken that folder. Only `Morphonic` and names that still
   begin with it are purged (`LinuxInstaller.PurgeNames`, unit test).
4. **Windows: Exit from the tray during a voice load** did not abandon the
   start as the Linux close does; it calls `BeginShutdown` now.
5. Test drivers. The walkthrough waited for the old "added" message after
   a drop; a dropped export is made the active voice in 1.0.2 (91 checks
   now, one more for the choice). In the Linux scenarios: s20 lacked the
   copy fallback; s26's pace checks assumed a slow machine without a
   graphics card (a fast one keeps up at 160 ms blocks, and one with a
   usable card is rightly told to install GPU acceleration); s23 gained the
   case of item 1 (the real tools with `PIPEWIRE_REMOTE` pointing nowhere);
   s29 is new and imitates PipeWire 1.0 on a newer one. The first full run
   also filled Fedora's RAM-backed `/tmp` (16 GB): with the models on
   another file system every run folder gets a full copy, and from s22 on
   nothing could start. `scenario.sh` says to keep both on one file system.

## Windows 11

| Layer | What | Result |
|---|---|---|
| Unit tests | `dotnet test` with `MORPHONIC_TEST_MODELS` and `MORPHONIC_TEST_SOURCES` (ONNX stages against the reference, assembly reproduces the pinned hashes) | 66 / 66 |
| UI walkthrough | `tools/ui_walkthrough.py --phase all` on the release exe | 91 / 91 |
| Offline exe | first-run phase: "included in this build — no download", set-up in 1 s | 20 / 20 |
| Update check | fake feed (1.0.3 announced with the Windows file, equal release, dead feed, nothing requested before the press) and the live feed | 15 / 15 |
| New in 1.0.2 | Cancel on a running Hub download (button read Cancel 11 ms after the click on a 362 MB file; nothing left behind); Start refused during the speed check and the speed check refused during a voice; an output picked while running restarts once, picked while stopped starts nothing, a pick followed at once by Start starts once; a dropped zip's voice is adopted under the zip's name; an exit landing inside the voice load logs "start abandoned: the app is closing", exit 0 | 33 / 33 |
| Find voices | a real Hugging Face download, named after its repository, converted in-app, adopted, started | 11 / 11 |
| Voices tabs, sidetone, report button | as in the 1.0.1 section | 9 / 9, 9 / 9, pass |
| Release archives | the exe inside each zip is byte-identical to the tested exe; `SHA256SUMS.txt` matches the four files | pass |

The last three rows before the archives ran on a build of the same
Windows-side source made before the final two commits (which changed
tests, tools and Linux-only code); the first four and the archives are the
release files themselves. VRCNext was running throughout and its browser
processes were left alone (same pids before and after the driver's
clean-up).

## Fedora 44

PipeWire 1.6.9, WirePlumber 0.5.18, pipewire-pulse 1.6.9, WebKitGTK 2.54.1,
GTK 3.24.52. This is the `--raw` path of `pw-record` / `pw-play`, which
Ubuntu 24.04's PipeWire 1.0 cannot reach. Hugging Face is reachable here,
so `--fetch-models` did the real thing on Linux for the first time in this
report: three downloads, in-app assembly, all three files matching their
pinned hashes.

**Unit tests** from a fresh clone of the public repository at `5e3c220`
(.NET SDK 9.0.121): 66 / 66 with the models and the assembly sources.

**Scenarios** (`tools/linux-scenarios`, the harness's own PipeWire graph
under Xvfb, one after another, on the release binary): all 29 pass, 259
checks. s26 passed on a second go after its expectation was corrected
(item 5); nothing else needed one.

| # | Checks | Worth noting on this machine |
|---|---|---|
| s01, s02 | 10, 15 | `in: pw-record (default input); out: pw-play (default output)`, with `--raw` |
| s03 – s10 | 8, 12, 10, 16, 6, 9, 13, 8 | s09's purge runs under the release file's own name |
| s11 | 7 | +12 semitones: ratio 1.93; pitch 0 against the clip: 1.03 |
| s12 | 6 | CPU: 134 ms per 500 ms block (load 0.27) |
| s13 | 12 | offline pack, unpack with no network, repack, smoke |
| s14 | 4 | run with a proxy that refuses, since Hugging Face answers here |
| s15 – s17 | 7, 4, 8 | |
| s18 | 8 | "target not found": capture failed, then playback failed, app kept running |
| s19 | 6 | 150 s: 294 passes, 0 ms skipped, 10 underruns with the desktop in use, one sidetone catch-up, 1407 MB RSS |
| s20 | 5 | |
| s21 | 7 | 39.7 s recorded from the virtual microphone, 949 voiced frames, median 97 Hz against the clip's 92 Hz |
| s22 | 13 | |
| s23 | 13 | new half: real tools, no PipeWire daemon in reach, virtual microphone and sidetone on — `in: parec; out: pacat → morphonic_voice; sidetone: pacat`, to the end |
| s24 | 4 | |
| s25 | 6 | a usable NVIDIA card and a garbage pack: "bundled CUDA 12 runtime failed to load", the voice on the CPU |
| s26 | 11 | idle: keeps up at 160 ms blocks (peak load 0.83), no advice; under load from the desktop: falls behind and is told to install GPU acceleration |
| s27 | 8 | the read-only folder ran as a normal user: exit 1, "couldn't unpack its interface files" |
| s28 | 12 | |
| s29 | 11 | new: tools that refuse `--raw` and do not list it — the session runs on `pw-record` / `pw-play (PipeWire 1.0, no --raw)` and `--raw` is never tried |

**The plain WSLg session** (PulseAudio server, the case of item 1), release
files: window on X11 and on WSLg's Wayland compositor, `in: parec; out:
pacat → morphonic_voice; sidetone: pacat`, sessions to the end, empty
error.log, no sink, lock or record left; `--report` names the tools tried
first. The released offline file unpacked its three models with the
network refused, verified them and ran a session. One observation: most
short sessions here skip about 3 s of backlog right at the start. The
published 1.0.1 binary does the same under the same conditions, so it is
WSLg's microphone bridge delivering a burst, not this release.

## Release 1.0.2 published

2026-10-10 06:45 UTC, https://github.com/netizen-kruze/Morphonic/releases/tag/v1.0.2,
tag on `5e3c220`. GitHub's SHA-256 digests of the five files equal the
local ones; the two plain files downloaded anonymously match
`SHA256SUMS.txt`. The published 1.0.1 and 1.0.0 exes are each told
"Version 1.0.2 is available … Download Morphonic-1.0.2-win-x64.zip (38
MB)"; the published 1.0.2 exe says "You have the latest version (1.0.2)."

## Still not covered

- A real PipeWire 1.0 machine with the release binary. The command line it
  runs there is the one that passed on Ubuntu 24.04 during the audit; what
  changed since is how it is chosen (from `--help`), and that ran against
  an imitation of the older tools (s29), not the tools themselves.
- Fedora on real hardware: a real microphone, GNOME or KDE, PipeWire
  devices that are not null sinks.
- Clicks the drivers cannot make: the Linux window's close button, Exit in
  the Windows tray (the same `BeginShutdown` ran through SIGTERM and
  `--run-seconds`), the `.zip` filter of the import dialog.
- The Python fallback converter end to end with PyTorch installed. That
  `-I` hides the user site-packages and `-E` does not was confirmed with
  the interpreter's own flags; no conversion was run through it.
- Listening.
