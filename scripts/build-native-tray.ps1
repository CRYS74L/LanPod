param([string]$Compiler, [switch]$RunRegression)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(!$Compiler){$Compiler=$env:LAN_TRAY_CXX}
if(!$Compiler){
    $shared=Join-Path ([IO.Directory]::GetParent($root).FullName) '【Common】\LLVM-MinGW\llvm-mingw-20260922-ucrt-x86_64\bin\clang++.exe'
    if(Test-Path -LiteralPath $shared){$Compiler=$shared}
    else {$Compiler=(Get-Command clang++.exe -ErrorAction Stop).Source}
}
$source=Join-Path $root 'src\LanPod.App\Native\TrayRuntime.cpp'
$output=Join-Path $root '.tools\native-tray'
New-Item -ItemType Directory -Path $output -Force | Out-Null
& $Compiler '-std=c++20' '-O2' '-shared' '-static' '-DUNICODE' '-D_UNICODE' '-D_WIN32_WINNT=0x0A00' $source '-o' (Join-Path $output 'LanPod.TrayRuntime.dll') '-luser32' '-lshell32'
if($LASTEXITCODE -ne 0){throw "Native tray build failed ($LASTEXITCODE)"}

if ($RunRegression) {
    $testSource = Join-Path $root 'tests\native\TrayStartupRegression.cpp'
    $testDirectory = Join-Path $root '.tools\native-tray-tests'
    New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
    $testExecutable = Join-Path $testDirectory 'TrayStartupRegression.exe'
    & $Compiler '-std=c++20' '-O2' '-static' '-municode' '-DUNICODE' '-D_UNICODE' '-D_WIN32_WINNT=0x0A00' $testSource '-o' $testExecutable '-luser32' '-lshell32'
    if ($LASTEXITCODE -ne 0) { throw "Native tray regression build failed ($LASTEXITCODE)" }
    & $testExecutable (Join-Path $testDirectory 'tray-runtime.log')
    if ($LASTEXITCODE -ne 0) { throw "Native tray regression failed ($LASTEXITCODE)" }
    & $Compiler '-std=c++20' '-O2' '-shared' '-static' '-DTRAY_TEST_LIBRARY' '-DUNICODE' '-D_UNICODE' '-D_WIN32_WINNT=0x0A00' $testSource '-o' (Join-Path $testDirectory 'TrayStartupFixture.dll') '-luser32' '-lshell32'
    if ($LASTEXITCODE -ne 0) { throw "Native tray fixture build failed ($LASTEXITCODE)" }
    $desktopSource = Join-Path $root 'tests\native\TrayDesktopRegression.cpp'
    $desktopExecutable = Join-Path $testDirectory 'TrayDesktopRegression.exe'
    & $Compiler '-std=c++20' '-O2' '-static' '-municode' '-DUNICODE' '-D_UNICODE' '-D_WIN32_WINNT=0x0A00' $desktopSource '-o' $desktopExecutable '-luser32' '-lshell32'
    if ($LASTEXITCODE -ne 0) { throw "Desktop tray regression build failed ($LASTEXITCODE)" }
    & $desktopExecutable (Join-Path $output 'LanPod.TrayRuntime.dll') (Join-Path $testDirectory 'desktop-runtime.log')
    if ($LASTEXITCODE -ne 0) { throw "Desktop tray regression failed ($LASTEXITCODE)" }
}
