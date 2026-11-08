param([switch]$Test, [string]$Rscript, [string]$StressData, [string]$StressOutput)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$work = Join-Path $projectRoot 'work'
New-Item -ItemType Directory -Force -Path $work,(Join-Path $work 'temp'),(Join-Path $work 'dotnet-home'),(Join-Path $work 'nuget') | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $work 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:NUGET_PACKAGES = Join-Path $work 'nuget'
$env:TEMP = Join-Path $work 'temp'
$env:TMP = $env:TEMP
Push-Location $projectRoot
try {
    & dotnet build 'app/WideLong/WideLong.csproj' --configuration Release --configfile 'NuGet.Config' --output 'output/WideLong-0.5.3' -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if ($Test) {
        if ($Rscript) { $env:WIDELONG_RSCRIPT = $Rscript }
        & dotnet run --project 'tests/WideLong.Tests.csproj' --configuration Release -- (Join-Path $work 'test-output')
        if ($LASTEXITCODE -ne 0) { throw 'Verification failed.' }
    }
    # Optional pressure test over a folder of real CSVs kept outside this tree (results go to -StressOutput).
    if ($StressData) {
        if ($Rscript) { $env:WIDELONG_RSCRIPT = $Rscript }
        if (-not $StressOutput) { $StressOutput = Join-Path $StressData 'stress-output' }
        & dotnet run --project 'tests/WideLong.Tests.csproj' --configuration Release -- (Join-Path $work 'test-output') --stress $StressData $StressOutput
        if ($LASTEXITCODE -ne 0) { throw 'Stress checks reported failures.' }
    }
} finally { Pop-Location }
