param(
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$BuildDirectory,
    [string]$Compiler
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$RuntimeDirectory = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $Compiler) { $Compiler = $env:LAN_TRAY_CXX }
if (-not $Compiler) {
    $shared = Join-Path ([IO.Directory]::GetParent($projectRoot).FullName) '【Common】\LLVM-MinGW\llvm-mingw-20260922-ucrt-x86_64\bin\clang++.exe'
    $Compiler = if (Test-Path -LiteralPath $shared) { $shared } else { (Get-Command clang++.exe -ErrorAction Stop).Source }
}
$resourceCompiler = Join-Path (Split-Path -Parent $Compiler) 'llvm-rc.exe'
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $projectRoot ".tools\portable-builds\$([guid]::NewGuid().ToString('N'))\bundle" }
$buildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$cabinetPath = Join-Path $buildDirectory 'payload.cab'
if (Test-Path -LiteralPath $cabinetPath) { throw 'Remove the previous portable-exe build directory before rebuilding.' }

$entries = @(Get-ChildItem -LiteralPath $RuntimeDirectory -Recurse -Force)
if ($entries | Where-Object {
    ($_.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Hidden)) -or
    $_.Name -in @('Data','state.json','window-placement.json','router-credentials.json','credential-key.bin','LOCAL_PRIVACY.md') -or
    $_.Name -like '.LanPod-data-*' -or $_.Extension -in @('.pdb','.log','.db','.sqlite','.dmp','.bak','.env','.tmp')
}) { throw 'Runtime contains user data, links or debug artifacts.' }
$files = @($entries | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName)
foreach ($required in @('LanPod.exe','coreclr.dll','hostfxr.dll','Microsoft.WindowsAppRuntime.dll','LanPod.TrayRuntime.dll','Assets\SystemIcon.png')) {
    if (-not (Test-Path -LiteralPath (Join-Path $RuntimeDirectory $required) -PathType Leaf)) { throw "Portable runtime file missing: $required" }
}
$directives = [Collections.Generic.List[string]]::new()
foreach ($line in @('.OPTION EXPLICIT','.Set Cabinet=on','.Set Compress=on','.Set CompressionType=MSZIP',
    '.Set CabinetNameTemplate=payload.cab','.Set MaxDiskSize=0','.Set MaxCabinetSize=0')) { $directives.Add($line) }
$relativeBuild = [IO.Path]::GetRelativePath($RuntimeDirectory, $buildDirectory)
$directives.Add('.Set DiskDirectoryTemplate=' + $relativeBuild)
$directives.Add('.Set RptFileName=' + (Join-Path $relativeBuild 'payload.rpt'))
$directives.Add('.Set InfFileName=' + (Join-Path $relativeBuild 'payload.inf'))
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($RuntimeDirectory, $file.FullName)
    if ($relative -match '[^\x20-\x7e]') { throw "Cabinet entry must use an ASCII relative name: $relative" }
    $directives.Add('"' + $relative + '" "' + $relative + '"')
}
$ddfPath = Join-Path $buildDirectory 'payload.ddf'
[IO.File]::WriteAllLines($ddfPath, $directives, [Text.Encoding]::ASCII)
Push-Location -LiteralPath $RuntimeDirectory
try {
    $cabinetLog = Join-Path $buildDirectory 'makecab.log'
    & makecab.exe /V0 /F ([IO.Path]::GetRelativePath($RuntimeDirectory, $ddfPath)) > $cabinetLog 2>&1
    if ($LASTEXITCODE -ne 0) { throw "makecab failed. See $cabinetLog" }
} finally { Pop-Location }
if (-not (Test-Path -LiteralPath $cabinetPath)) { throw 'Cabinet output is missing.' }

$identity = (Get-FileHash -LiteralPath $cabinetPath -Algorithm SHA256).Hash.Substring(0,16)
[IO.File]::WriteAllText((Join-Path $buildDirectory 'identity.txt'), $identity, [Text.Encoding]::ASCII)
$manifest = $files | ForEach-Object { "$($_.Length)`t$([IO.Path]::GetRelativePath($RuntimeDirectory, $_.FullName))" }
[IO.File]::WriteAllText((Join-Path $buildDirectory 'files.txt'), ($manifest -join "`n"), [Text.Encoding]::ASCII)
$project = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src\LanPod.App\LanPod.App.csproj') -Raw)
$version = [string]$project.Project.PropertyGroup.Version
$fileVersion = ([version]$version).ToString(3).Replace('.', ',') + ',0'
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\LanPod.App\Assets\AppIcon.ico') -Destination (Join-Path $buildDirectory 'AppIcon.ico')
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\LanPod.App\app.manifest') -Destination (Join-Path $buildDirectory 'app.manifest')
$resourceText = @(
    '1 ICON "AppIcon.ico"'
    '1 24 "app.manifest"'
    '101 RCDATA "payload.cab"'
    '102 RCDATA "identity.txt"'
    '103 RCDATA "files.txt"'
    '1 VERSIONINFO'
    "FILEVERSION $fileVersion"
    "PRODUCTVERSION $fileVersion"
    'FILEOS 0x40004L'
    'FILETYPE 1L'
    'BEGIN'
    '  BLOCK "StringFileInfo" BEGIN'
    '    BLOCK "040904B0" BEGIN'
    '      VALUE "CompanyName", "LanPod\0"'
    '      VALUE "FileDescription", "LanPod\0"'
    "      VALUE `"FileVersion`", `"$version\0`""
    '      VALUE "InternalName", "LanPod\0"'
    '      VALUE "OriginalFilename", "LanPod.exe\0"'
    '      VALUE "ProductName", "LanPod\0"'
    "      VALUE `"ProductVersion`", `"$version\0`""
    '      VALUE "LegalCopyright", "GPL-3.0-only\0"'
    '    END'
    '  END'
    '  BLOCK "VarFileInfo" BEGIN'
    '    VALUE "Translation", 0x0409, 1200'
    '  END'
    'END'
)
[IO.File]::WriteAllLines((Join-Path $buildDirectory 'launcher.rc'), $resourceText, [Text.Encoding]::ASCII)
Push-Location -LiteralPath $buildDirectory
try {
    & $resourceCompiler /FO launcher.res launcher.rc
    if ($LASTEXITCODE -ne 0) { throw 'Portable resource compilation failed.' }
    & $Compiler '-std=c++20' '-O2' '-static' '-municode' '-mwindows' '-DUNICODE' '-D_UNICODE' `
        (Join-Path $projectRoot 'src\LanPod.App\Native\PortableLauncher.cpp') 'launcher.res' `
        '-o' (Join-Path $OutputDirectory 'LanPod.exe') '-lsetupapi' '-luser32'
    if ($LASTEXITCODE -ne 0) { throw 'Portable launcher build failed.' }
} finally { Pop-Location }
Write-Output "Single executable created: $(Join-Path $OutputDirectory 'LanPod.exe')"
