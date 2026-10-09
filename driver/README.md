# Morphonic's Windows virtual microphone (driver/MorphonicCable)

Other programs can only use the converted voice as a **microphone** if a
microphone device exists. Windows has no built-in way to add one from an
application: an audio device is a kernel-mode driver. So Morphonic ships its
own, a small virtual audio cable:

- **Morphonic Voice** — an output. Morphonic plays the converted voice into it.
- **Morphonic Microphone** — an input other programs (Discord, VRChat, OBS,
  games) pick as their microphone. Whatever plays into Morphonic Voice comes
  out here.

The driver is a PortCls / WaveRT miniport (KMDF) derived from
[AudioMirror](https://github.com/JannesP/AudioMirror) (MIT, Jannes Peters;
see `LICENSE-AudioMirror.md`), which in turn descends from Microsoft's
`sysvad` sample. The render stream writes into a ring buffer the paired
capture stream reads. Hardware id `Root\MorphonicCable`, service
`MorphonicCable`.

The package (`MorphonicCable.inf`, `.sys`, `.cat`) is embedded in
`Morphonic.exe` when it exists in `driver/package/` at publish time.
**Settings → Virtual microphone → Install** unpacks it to the data folder and
runs a second copy of the exe elevated (one administrator prompt) that
creates the device node and installs the driver through SetupAPI —
exactly what `devcon install` does. **Remove** deletes the device and the
package from the driver store. Nothing else on the machine is touched.

## Building the driver

Requirements on the build machine (Windows 11):

- Visual Studio 2022 Build Tools (or the IDE) with *Desktop development with
  C++*.
- Windows SDK and **Windows Driver Kit 10.0.26100** (`winget install
  Microsoft.WindowsWDK.10.0.26100`), plus the Visual Studio component
  *Windows Driver Kit Build Tools* (`Component.Microsoft.Windows.DriverKit.BuildTools`
  through the Visual Studio Installer), which supplies the
  `WindowsKernelModeDriver10.0` toolset.

Or, without touching Visual Studio: the **Enterprise WDK** (EWDK), a
self-contained ISO from Microsoft's WDK download page (about 20 GB) with
its own build tools, SDK and WDK. Mount it (double-click, or
`Mount-DiskImage`) and pass the mounted drive:

```powershell
.\build.ps1 -DriverOnly -Ewdk E:\     # E: = the mounted EWDK
```

Then, from the repository root:

```powershell
.\build.ps1 -Driver          # builds driver\MorphonicCable (Release, x64) into driver\package\
.\build.ps1                  # publishes the app; the package in driver\package\ is embedded
```

`-Driver` runs MSBuild on `driver\MorphonicCable\MorphonicCable.vcxproj`
(Release | x64, `SignMode=Off`) and copies the INF, SYS and CAT it
produces. The CAT from a local build is **unsigned**: Windows will not
load it, so the app offers no Install button for it and says why in
Settings (it checks the catalog for the Hardware Dev Center signer). The
1.0.1 package was built this way with the EWDK 10.0.28000 and Visual
Studio 2026 Build Tools; the two warnings the inherited sources trip
(`ExAllocatePoolWithTag` deprecated in favour of `ExAllocatePool2`, one
empty statement) are excluded from warnings-as-errors in the project.

## Signing: the step only the publisher can do

Windows (10 and 11, 64-bit, Secure Boot on) loads a kernel driver only when
Microsoft has signed it. There is no application-side workaround, and
Morphonic does not try one: it never changes a machine's boot or
signature settings.

The route is Microsoft's **Hardware Dev Center attestation signing**:

1. Buy an **EV code-signing certificate** from one of the authorities
   Microsoft lists (Certum, DigiCert, GlobalSign, IdenTrust, Sectigo,
   SSL.com). The authority verifies the legal entity; the certificate
   arrives on a hardware token or in a cloud HSM.
2. Create a **Partner Center** (Hardware Dev Center) account and register
   that certificate with it.
3. Sign the package locally: `signtool sign /fd sha256 /a ...` on
   `MorphonicCable.sys` and `MorphonicCable.cat` (or on the submission
   `.cab`, which `makecab` builds from the three files). Microsoft's
   requirements: SHA-2 certificates, `/fd sha256`.
4. Submit the `.cab` for **attestation signing** (no HLK run needed for a
   driver that targets Windows 10/11 client). Microsoft returns the package
   with its own signature on the CAT and SYS.
5. Put the signed `MorphonicCable.inf`, `.sys` and `.cat` into
   `driver\package\` and publish the app (`build.ps1`). Every release after
   that carries a driver Windows accepts, and *Install* in Settings works
   on any PC.

Reference: [Driver code signing requirements](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/code-signing-reqs)
and the Partner Center hardware-submission pages.

## What is verified without the signature

- The driver builds from source with the WDK (`build.ps1 -Driver`).
- The app embeds the package, detects whether the device exists, offers
  Install / Remove, runs the elevated half, routes the voice into
  "Morphonic Voice" automatically when *Virtual microphone* is on and
  Output is *System default*, and turns *Hear yourself* on for it.
- The elevated install path is the same SetupAPI sequence `devcon install`
  uses; with an unsigned package it stops at Windows' signature check and
  that message is shown in the app.

The audio path itself (voice into Morphonic Voice, out of Morphonic
Microphone into another program) can only be exercised with a loadable,
Microsoft-signed package.
