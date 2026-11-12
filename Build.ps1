param([switch]$Test, [string]$Rscript, [string]$StressData, [string]$StressOutput, [switch]$Publish)
# Builds WideLong into output/WideLong (run with "Start WideLong.cmd").
#   -Test       also runs the synthetic verification suite (pass -Rscript <Rscript.exe> to replay generated R code natively)
#   -Publish    also creates output/WideLong-<version>-win-x64.zip: one self-contained WideLong.exe that needs no .NET install.
#               This is the only option that downloads anything (the .NET runtime packs, from nuget.org).
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
$version = ([xml](Get-Content (Join-Path $projectRoot 'Directory.Build.props'))).Project.PropertyGroup.Version
Push-Location $projectRoot
try {
    & dotnet build 'app/WideLong/WideLong.csproj' --configuration Release --configfile 'NuGet.Config' --output 'output/WideLong' -p:NuGetAudit=false
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
    if ($Publish) {
        $publishDir = Join-Path $work 'publish'
        if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
        & dotnet publish 'app/WideLong/WideLong.csproj' --configuration Release --runtime win-x64 --self-contained true --source 'https://api.nuget.org/v3/index.json' --output $publishDir -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        $zip = Join-Path $projectRoot "output/WideLong-$version-win-x64.zip"
        if (Test-Path $zip) { Remove-Item -Force $zip }
        Compress-Archive -Path (Join-Path $publishDir 'WideLong.exe'), (Join-Path $projectRoot 'LICENSE'), (Join-Path $projectRoot 'PRIVACY.md') -DestinationPath $zip
        Write-Host "Release package: $zip"
    }
} finally { Pop-Location }
