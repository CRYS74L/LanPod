param(
    [int]$TimeoutSeconds = 30,
    [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ExecutablePath) { $ExecutablePath = Join-Path $projectRoot 'dist\LanPod\LanPod.exe' }
$ExecutablePath = (Resolve-Path -LiteralPath $ExecutablePath).Path
if (Get-Process -Name LanPod -ErrorAction SilentlyContinue) { throw 'Exit LanPod before verifying a primary launch.' }
$testDirectory = Join-Path $projectRoot ".tools\portable-verification\$([guid]::NewGuid().ToString('N'))"
$applicationDirectory = Join-Path $testDirectory '初始位置 带空格'
New-Item -ItemType Directory -Path (Join-Path $applicationDirectory 'Data') -Force | Out-Null
Copy-Item -LiteralPath $ExecutablePath -Destination (Join-Path $applicationDirectory 'LanPod.exe')
# A clean test copy must not import this computer's personal settings.
[IO.File]::WriteAllText((Join-Path $applicationDirectory 'Data\.initialized'), '1')
$launcher = $null
$application = $null

function Start-Portable([bool]$background) {
    $start = @{ FilePath = (Join-Path $applicationDirectory 'LanPod.exe'); PassThru = $true; WindowStyle = 'Hidden' }
    if ($background) { $start.ArgumentList = '--background' }
    $script:launcher = Start-Process @start
    $script:application = $null
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if ($launcher.HasExited) { throw "Portable launcher exited early: $($launcher.ExitCode)" }
        $child = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($launcher.Id) AND Name='LanPod.exe'"
        if ($child) { $script:application = Get-Process -Id $child.ProcessId -ErrorAction Stop }
        if ($application -and ($background -or $application.MainWindowHandle -ne 0)) { break }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    if (-not $application -or (-not $background -and $application.MainWindowHandle -eq 0)) {
        throw 'The bundled application did not complete startup.'
    }
    if (-not $application.Path.StartsWith((Join-Path $applicationDirectory 'Data\Runtime\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The application ran outside its local runtime directory.'
    }
}

function Stop-Portable {
    if ($application -and -not $application.HasExited) { Stop-Process -Id $application.Id }
    if ($launcher -and -not $launcher.WaitForExit(5000)) { Stop-Process -Id $launcher.Id }
}

try {
    Start-Portable $false
    $window = $application.MainWindowHandle
    $runtime = Split-Path -Parent $application.Path
    $marker = (Get-Item -LiteralPath (Join-Path $runtime '.complete')).LastWriteTimeUtc
    $modules = @($application.Modules | Where-Object { $_.ModuleName -in @('coreclr.dll','Microsoft.WindowsAppRuntime.dll') })
    if ($modules.Count -ne 2 -or ($modules | Where-Object { -not $_.FileName.StartsWith($runtime, [StringComparison]::OrdinalIgnoreCase) })) {
        throw 'The bundled application did not load its local .NET and Windows App SDK runtimes.'
    }
    $secondary = Start-Process -FilePath (Join-Path $applicationDirectory 'LanPod.exe') -PassThru -WindowStyle Hidden
    if (-not $secondary.WaitForExit($TimeoutSeconds * 1000) -or $secondary.ExitCode -ne 0) { throw 'Single-instance handoff failed.' }
    $application.Refresh()
    if ($application.HasExited -or $application.MainWindowHandle -ne $window) { throw 'Single-instance handoff lost the primary window.' }
    Stop-Portable

    $movedDirectory = Join-Path $testDirectory '移动后 新位置'
    [IO.Directory]::Move($applicationDirectory, $movedDirectory)
    $applicationDirectory = $movedDirectory
    Start-Portable $true
    Start-Sleep -Milliseconds 1500
    $application.Refresh()
    if ($application.HasExited -or $application.MainWindowHandle -ne 0) { throw 'Background startup displayed a main window or exited.' }
    $runtime = Split-Path -Parent $application.Path
    if ((Get-Item -LiteralPath (Join-Path $runtime '.complete')).LastWriteTimeUtc -ne $marker) { throw 'Moving the application unnecessarily re-extracted the runtime.' }
    $entries = @(Get-ChildItem -LiteralPath $applicationDirectory -Force)
    if ($entries.Count -ne 2 -or (@($entries.Name | Sort-Object) -join ',') -ne 'Data,LanPod.exe') { throw 'Portable root does not contain exactly EXE and Data.' }
    if (Get-ChildItem -LiteralPath (Join-Path $applicationDirectory 'Data') -Filter Data -Directory -Recurse) { throw 'Application data was nested inside the runtime.' }
    [pscustomobject]@{ Version=(Get-Item -LiteralPath $ExecutablePath).VersionInfo.ProductVersion; FirstLaunch=$true; LocalRuntimes=$true; SingleInstance=$true; Relocation=$true; BackgroundStartup=$true; CacheReused=$true; RootEntries=2 }
}
finally {
    Stop-Portable
    if (Test-Path -LiteralPath $testDirectory) { [IO.Directory]::Delete($testDirectory, $true) }
}
