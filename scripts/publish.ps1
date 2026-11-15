param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$projectPath = Join-Path $projectRoot 'src\LanPod.App\LanPod.App.csproj'
$projectXml = [xml](Get-Content -Raw -Encoding UTF8 -LiteralPath $projectPath)
$applicationName = [string]$projectXml.Project.PropertyGroup.AssemblyName
$distDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $projectRoot 'dist'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $distDirectory $applicationName }
$outputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

$expectedPrefix = $distDirectory.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
if (-not $outputDirectory.StartsWith(
        $expectedPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish output must be inside dist: $outputDirectory"
}

if (Test-Path -LiteralPath $outputDirectory) {
    if (Test-Path -LiteralPath (Join-Path $outputDirectory 'Data')) {
        throw "Publish output contains portable user data. Preserve Data before replacing the program: $outputDirectory"
    }
    if (Get-ChildItem -LiteralPath $outputDirectory -Force | Where-Object { $_.Name -ne 'LanPod.exe' -or $_.PSIsContainer }) {
        throw "Publish output must contain only LanPod.exe. Preserve or clean previous output first: $outputDirectory"
    }
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$buildDirectory = Join-Path $projectRoot ".tools\portable-builds\$([guid]::NewGuid().ToString('N'))"
$runtimeDirectory = Join-Path $buildDirectory 'runtime'

$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

if (Test-Path -LiteralPath $localDotnet) {
    $env:DOTNET_ROOT = Split-Path -Parent $localDotnet
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
}

dotnet restore $projectPath `
    --runtime win-x64 `
    --ignore-failed-sources `
    --disable-parallel `
    -p:WindowsAppSDKSelfContained=true `
    -p:NuGetAudit=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE"
}

dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --output $runtimeDirectory `
    -p:PublishSingleFile=false `
    -p:WindowsAppSDKSelfContained=true `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    -p:UseSharedCompilation=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

& (Join-Path $PSScriptRoot 'build-portable-exe.ps1') -RuntimeDirectory $runtimeDirectory `
    -OutputDirectory $outputDirectory -BuildDirectory (Join-Path $buildDirectory 'bundle')
Write-Output "Published single executable to: $outputDirectory"
