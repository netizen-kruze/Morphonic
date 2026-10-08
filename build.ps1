#Requires -Version 5.1
# Build the Morphonic releases from Windows:
#   releases\Morphonic-<version>-win-x64.zip      a single Morphonic.exe, no installer
#   releases\Morphonic-<version>-linux-x64        one self-contained Linux binary (cross-published;
#                                             no Linux toolchain needed - the .NET SDK does it)
# Both are self-contained single-file publishes: the .NET runtime, the
# interface and the CPU inference runtime are inside; GPU acceleration is
# downloaded in-app.
#
#   .\build.ps1                 # both
#   .\build.ps1 -Target win     # Windows only
#   .\build.ps1 -Target linux   # Linux only
[CmdletBinding()]
param(
  [string]$Configuration = 'Release',
  [ValidateSet('all', 'win', 'linux')][string]$Target = 'all',
  # -Offline <folder>: also write the offline variants (about 850 MB more
  # each) with the three model files inside. The folder is a models folder
  # the app filled: `publish\Morphonic.exe --fetch-models --data-dir X`
  # then X\models (the sample voice is found in X\voices).
  [string]$Offline = '',
  # -Driver: build the Windows virtual-microphone driver (driver\MorphonicCable,
  # Release x64, needs the WDK and the "Windows Driver Kit Build Tools" VS
  # component) into driver\package\, which the app build then embeds. Only
  # a package signed through Microsoft's Hardware Dev Center loads on
  # users' machines (driver\README.md).
  [switch]$Driver,
  [switch]$DriverOnly
)
$ErrorActionPreference = 'Stop'
if ($Driver -or $DriverOnly) {
  $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
  $vs = & $vswhere -products * -latest -requires Component.Microsoft.Windows.DriverKit.BuildTools -property installationPath 2>$null
  if (-not $vs) { $vs = & $vswhere -products * -latest -requires Component.Microsoft.Windows.DriverKit -property installationPath 2>$null }
  if (-not $vs) { throw 'No Visual Studio with the Windows Driver Kit component (see driver\README.md).' }
  $msbuild = Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe'
  $dproj = Join-Path $PSScriptRoot 'driver\MorphonicCable\MorphonicCable.vcxproj'
  Write-Host "==> Building the virtual microphone driver (Release, x64) with $msbuild" -ForegroundColor Cyan
  & $msbuild $dproj /nologo /v:m /p:Configuration=Release /p:Platform=x64 /p:SignMode=Off
  if ($LASTEXITCODE -ne 0) { throw 'driver build failed' }
  $pkg = Join-Path $PSScriptRoot 'driver\package'
  New-Item -ItemType Directory -Force $pkg | Out-Null
  $built = Get-ChildItem (Join-Path $PSScriptRoot 'driver\MorphonicCable\x64\Release') -Recurse -Include 'MorphonicCable.inf', 'MorphonicCable.sys', 'MorphonicCable.cat' -ErrorAction SilentlyContinue
  foreach ($f in $built) { Copy-Item $f.FullName (Join-Path $pkg $f.Name) -Force }
  $have = Get-ChildItem $pkg | Select-Object -ExpandProperty Name
  Write-Host "==> driver\package: $($have -join ', ')"
  if ($have -notcontains 'MorphonicCable.sys') { throw 'the driver build produced no MorphonicCable.sys' }
  if ($DriverOnly) { exit 0 }
}
$proj = Join-Path $PSScriptRoot 'src\Morphonic\Morphonic.csproj'
[xml]$x = Get-Content $proj
$ver = ($x.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $ver) { throw 'No <Version> in the csproj.' }
$releases = Join-Path $PSScriptRoot 'releases'
New-Item -ItemType Directory -Force $releases | Out-Null

function Publish([string]$rid, [string]$out) {
  if (Test-Path $out) { Remove-Item -Recurse -Force $out }
  Write-Host "==> Publishing Morphonic $ver ($Configuration, $rid, self-contained, single file)" -ForegroundColor Cyan
  dotnet publish $proj -c $Configuration -r $rid --self-contained -o $out -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded
  if ($LASTEXITCODE -ne 0) { throw "publish failed ($rid)" }
}

if ($Target -ne 'linux') {
  $out = Join-Path $PSScriptRoot 'publish'
  Publish 'win-x64' $out
  $zip = Join-Path $releases "Morphonic-$ver-win-x64.zip"
  if (Test-Path $zip) { Remove-Item -Force $zip }
  Compress-Archive -Path (Join-Path $out 'Morphonic.exe') -DestinationPath $zip
  $mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
  Write-Host "==> Done: $zip ($mb MB)" -ForegroundColor Green
  Write-Host "    SHA-256: $((Get-FileHash $zip -Algorithm SHA256).Hash)"
  if ($Offline) {
    $offDir = Join-Path $PSScriptRoot 'publish-offline'
    New-Item -ItemType Directory -Force $offDir | Out-Null
    $offExe = Join-Path $offDir 'Morphonic.exe'
    # a GUI-subsystem exe is not waited for by '&': Start-Process -Wait is
    $pack = Start-Process -FilePath (Join-Path $out 'Morphonic.exe') -ArgumentList @('--pack-offline', "`"$Offline`"", "`"$offExe`"") -Wait -NoNewWindow -PassThru
    if ($pack.ExitCode -ne 0) { throw 'offline pack failed (win-x64)' }
    $offZip = Join-Path $releases "Morphonic-$ver-win-x64-offline.zip"
    if (Test-Path $offZip) { Remove-Item -Force $offZip }
    Compress-Archive -Path $offExe -DestinationPath $offZip -CompressionLevel Fastest
    $mb = [math]::Round((Get-Item $offZip).Length / 1MB, 1)
    Write-Host "==> Done: $offZip ($mb MB, models inside)" -ForegroundColor Green
    Write-Host "    SHA-256: $((Get-FileHash $offZip -Algorithm SHA256).Hash)"
  }
}
if ($Target -ne 'win') {
  $out = Join-Path $PSScriptRoot 'publish-linux'
  Publish 'linux-x64' $out
  $file = Join-Path $releases "Morphonic-$ver-linux-x64"
  Copy-Item (Join-Path $out 'Morphonic') $file -Force
  $mb = [math]::Round((Get-Item $file).Length / 1MB, 1)
  Write-Host "==> Done: $file ($mb MB) - Linux never keeps an executable bit on a download: chmod +x after downloading" -ForegroundColor Green
  Write-Host "    SHA-256: $((Get-FileHash $file -Algorithm SHA256).Hash)"
  if ($Offline) {
    $winExe = Join-Path $PSScriptRoot 'publish\Morphonic.exe'
    if (-not (Test-Path $winExe)) { throw 'the offline Linux file is packed by the Windows build: run -Target all' }
    $offFile = Join-Path $releases "Morphonic-$ver-linux-x64-offline"
    $pack = Start-Process -FilePath $winExe -ArgumentList @('--pack-offline', "`"$Offline`"", "`"$offFile`"", '--base', "`"$file`"") -Wait -NoNewWindow -PassThru
    if ($pack.ExitCode -ne 0) { throw 'offline pack failed (linux-x64)' }
    $mb = [math]::Round((Get-Item $offFile).Length / 1MB, 1)
    Write-Host "==> Done: $offFile ($mb MB, models inside)" -ForegroundColor Green
    Write-Host "    SHA-256: $((Get-FileHash $offFile -Algorithm SHA256).Hash)"
  }
}
# releases\SHA256SUMS.txt: one line per release file, in sha256sum's format,
# published next to the files so a download can be checked.
$sums = Get-ChildItem $releases -File | Where-Object { $_.Name -like "Morphonic-$ver-*" } |
  ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)" }
[IO.File]::WriteAllText((Join-Path $releases 'SHA256SUMS.txt'), (($sums -join "`n") + "`n"))
Write-Host "==> releases\SHA256SUMS.txt covers $($sums.Count) file(s)"
