param([string]$OutputPath)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src\LanPod.App\LanPod.App.csproj') -Raw)
$version = [string]$project.Project.PropertyGroup.Version
$publishDirectory = Join-Path $projectRoot 'dist\LanPod'
if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'LanPod.exe'))) {
    throw 'Run scripts\publish.ps1 before packaging.'
}

$entries = @(Get-ChildItem -LiteralPath $publishDirectory -Force)
if ($entries.Count -ne 1 -or $entries[0].Name -ne 'LanPod.exe' -or $entries[0].PSIsContainer -or
    ($entries[0].Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Hidden))) {
    throw 'Publish output must contain only LanPod.exe. Do not package a running copy or user Data.'
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
