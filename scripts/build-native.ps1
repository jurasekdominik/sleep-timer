$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    cmake -S native -B native/obj -A x64
    if ($LASTEXITCODE -ne 0) { throw 'Native configure failed; install Visual Studio C++ build tools and the Windows SDK' }
    cmake --build native/obj --config Release --parallel
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }
    $process = Start-Process ./native/bin/SleepTimer.Taskbar.exe -ArgumentList '--self-test' -PassThru -NoNewWindow
    if (-not $process.WaitForExit(15000)) { $process.Kill(); throw 'Native renderer self-test timed out' }
    if ($process.ExitCode -ne 0) { throw "Native renderer self-test failed: $($process.ExitCode)" }
} finally { Pop-Location }
