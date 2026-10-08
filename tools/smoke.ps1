#Requires -Version 5.1
<#
Release smoke test (Windows) - run on ANY machine, including one without a
microphone or any model installed.

Boots the published Morphonic.exe against a throwaway data folder, then checks
that the process survived, the page connected, the settings were read, the
hardware and component lines were written, and nothing crashed. With
-Models <folder holding contentvec-768-layer12.onnx, rmvpe.onnx and
sample-voice-40k.onnx> it also starts the voice on the default devices
(--auto-start) and checks the session summary. Nothing touches the real
%APPDATA%\Morphonic.

  .\tools\smoke.ps1                                    # after .\build.ps1 (uses publish\Morphonic.exe)
  .\tools\smoke.ps1 -Exe C:\Tools\Morphonic\Morphonic.exe
  .\tools\smoke.ps1 -Models $env:APPDATA\Morphonic\models   # full run with the voice
#>
[CmdletBinding()]
param(
  [string]$Exe = '',
  [string]$Models = '',
  [int]$WaitSeconds = 12
)
$ErrorActionPreference = 'Stop'
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\publish\Morphonic.exe' }
$Exe = (Resolve-Path $Exe).Path
if (Get-Process Morphonic -ErrorAction SilentlyContinue) {
  throw 'Morphonic is already running - close it first (single instance).'
}

$root = Join-Path $env:TEMP ('morphonic-smoke-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $root 'data'
New-Item -ItemType Directory -Force $data | Out-Null
$full = $false
if ($Models) {
  New-Item -ItemType Directory -Force (Join-Path $data 'models'), (Join-Path $data 'voices') | Out-Null
  foreach ($f in 'contentvec-768-layer12.onnx', 'rmvpe.onnx') { Copy-Item (Join-Path $Models $f) (Join-Path $data "models\$f") }
  $voice = Join-Path $Models 'sample-voice-40k.onnx'
  if (-not (Test-Path $voice)) { $voice = Join-Path $Models '..\voices\sample-voice-40k.onnx' }
  Copy-Item $voice (Join-Path $data 'voices\sample-voice-40k.onnx')
  '{ "VoiceId": "sample-voice-40k.onnx", "Acceleration": "cpu", "PitchSemitones": 3 }' | Set-Content (Join-Path $data 'settings.json') -Encoding UTF8
  $full = $true
} else {
  '{ "PitchSemitones": 3 }' | Set-Content (Join-Path $data 'settings.json') -Encoding UTF8
}

$t0 = Get-Date
$args = @('--data-dir', "`"$data`"", '--run-seconds', "$WaitSeconds")
if ($full) { $args += '--auto-start' }
$p = Start-Process -FilePath $Exe -PassThru -WorkingDirectory (Split-Path $Exe) -ArgumentList $args
$exited = $p.WaitForExit(($WaitSeconds + 20) * 1000)
$p.Refresh()
if (-not $exited) { Stop-Process -Id $p.Id -Force }

$bootLog = Join-Path $data 'last_boot.log'
$log = if (Test-Path $bootLog) { Get-Content $bootLog -Raw -Encoding UTF8 } else { '' }
$errLog = Join-Path $data 'error.log'
$errors = if (Test-Path $errLog) { Get-Content $errLog -Raw -Encoding UTF8 } else { '' }
$crashes = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $t0; ProviderName = 'Application Error' } -ErrorAction SilentlyContinue |
  Where-Object { $_.Message -match 'Morphonic' }).Count

$checks = [ordered]@{
  'exited cleanly on --run-seconds'  = $exited
  'boot log written'                 = ($log.Length -gt 0)
  'settings file read'               = ($log -match 'settings from:\s+settings file')
  'page connected'                   = ($log -match 'page connected')
  'hardware verdict logged'          = ($log -match 'gpu: ')
  'components line logged'           = ($log -match 'components: ')
  'no Windows crash events'          = ($crashes -eq 0)
  'no unhandled exceptions logged'   = ($errors -notmatch 'Unhandled|SessionWork|UiDispatcher|OnUiMessage|StartSession')
}
if ($full) {
  $checks['voice started on the default devices'] = ($log -match 'voice started:')
  $checks['session summary written']              = ($log -match 'voice session ended .* passes')
}
$failed = 0
foreach ($k in $checks.Keys) {
  $ok = [bool]$checks[$k]
  if (-not $ok) { $failed++ }
  Write-Host ('  [{0}] {1}' -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $k)
}
Write-Host ''
Write-Host '--- last_boot.log ---'
Write-Host $log
if ($errors) { Write-Host '--- error.log ---'; Write-Host $errors }
try { Remove-Item -Recurse -Force $root } catch { }

if ($failed -gt 0) { Write-Host "SMOKE TEST FAILED ($failed check(s))" -ForegroundColor Red; exit 1 }
Write-Host 'SMOKE TEST PASSED' -ForegroundColor Green
exit 0
