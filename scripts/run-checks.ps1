# Builds, runs the synthetic suite (with native R when installed) and, if a data folder is given, the stress checks.
param([string]$StressData)
$ErrorActionPreference = "Continue"
$root = Split-Path $PSScriptRoot -Parent
$log = Join-Path $PSScriptRoot "build.log"
$r = Get-ChildItem "C:\Program Files\R\*\bin\Rscript.exe","$env:LOCALAPPDATA\Programs\R\*\bin\Rscript.exe" -ErrorAction SilentlyContinue | Sort-Object FullName | Select-Object -Last 1
"Rscript: $($r.FullName)" | Out-File $log -Encoding utf8
$extra = @{}
if ($r) { $extra.Rscript = $r.FullName }
if ($StressData) { $extra.StressData = $StressData; $extra.StressOutput = Join-Path $StressData 'stress-output' }
try { & (Join-Path $root "Build.ps1") -Test @extra *>> $log; "EXIT OK" | Out-File $log -Append -Encoding utf8 } catch { "EXIT FAILED: $_" | Out-File $log -Append -Encoding utf8 }
