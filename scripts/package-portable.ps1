param([string]$OutputPath)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src\LanPod.App\LanPod.App.csproj') -Raw)
$version = [string]$project.Project.PropertyGroup.Version
$publishDirectory = Join-Path $projectRoot 'dist\LanPod'
if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'LanPod.exe'))) {
    throw 'Run scripts\publish.ps1 before packaging.'
}

$entries = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -Force)
$unsafeEntries = @($entries | Where-Object {
    ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
    ($_.Attributes -band [IO.FileAttributes]::Hidden) -or
    $_.Name -eq 'Data' -or $_.Name -like '.LanPod-data-*' -or
    $_.Name -in @('state.json','window-placement.json','router-credentials.json','credential-key.bin') -or
    $_.Extension -in @('.pdb','.log','.db','.sqlite','.dmp','.bak','.env','.tmp')
})
if ($unsafeEntries.Count -gt 0) {
    throw 'Publish output contains user data, links or debug artifacts. Create a clean publish before packaging.'
}

$requiredFiles = @('coreclr.dll','hostfxr.dll','Microsoft.WindowsAppRuntime.dll','LanPod.TrayRuntime.dll','Assets\SystemIcon.png')
foreach ($relativePath in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $relativePath))) {
        throw "Portable runtime file missing: $relativePath"
    }
}

if (-not $OutputPath) {
    $OutputPath = Join-Path $projectRoot "dist\LanPod-v$version-portable-win-x64.zip"
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) { throw "Archive already exists: $OutputPath" }
[IO.Compression.ZipFile]::CreateFromDirectory(
    $publishDirectory, $OutputPath, [IO.Compression.CompressionLevel]::Optimal, $true)
$archive = [IO.Compression.ZipFile]::OpenRead($OutputPath)
try {
    if ($archive.Entries.Count -ne @($entries | Where-Object { -not $_.PSIsContainer }).Count) {
        throw 'Archive entry count differs from the clean publish.'
    }
} finally { $archive.Dispose() }
Write-Output "Portable package created: $OutputPath"
