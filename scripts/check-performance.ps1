param([string]$BaselineRef)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    New-Item validation-output -ItemType Directory -Force | Out-Null
    function Invoke-Measurement($executable, $arguments, $timeout) {
        $process = Start-Process $executable -ArgumentList $arguments -NoNewWindow -PassThru
        if (-not $process.WaitForExit($timeout)) { $process.Kill(); throw 'Performance check timed out' }
        if ($process.ExitCode -ne 0) { throw "Performance check failed: $($process.ExitCode)" }
    }
    if ($BaselineRef) {
        $baselineCommit = git rev-parse --verify --end-of-options "${BaselineRef}^{commit}"
        if ($LASTEXITCODE -ne 0 -or $baselineCommit -notmatch '^[0-9a-f]{40}$') { throw 'Baseline must resolve to an existing commit' }
        $baseline = Join-Path ([IO.Path]::GetTempPath()) ('sleep-timer-performance-' + [Guid]::NewGuid().ToString('N'))
        New-Item $baseline -ItemType Directory -Force | Out-Null
        $archive = Join-Path $baseline 'baseline.zip'
        git archive $baselineCommit -o $archive
        if ($LASTEXITCODE -ne 0) { throw 'Could not read the baseline source' }
        Expand-Archive $archive (Join-Path $baseline 'source') -Force
        $baselineSource = Join-Path $baseline 'source/src'
        $baselineOutput = Join-Path $baseline 'bin'
        dotnet build validation/PerformanceChecks.csproj -c Release --nologo -warnaserror "-p:SleepTimerSourceRoot=$baselineSource" --output $baselineOutput
        if ($LASTEXITCODE -ne 0) { throw 'Baseline performance build failed' }
        Invoke-Measurement (Join-Path $baselineOutput 'PerformanceChecks.exe') @('--baseline', '--adjustments-only', 'validation-output/performance-baseline.csv') 120000
        Invoke-Measurement (Join-Path $baselineOutput 'PerformanceChecks.exe') @('--baseline', '--native', '--adjustments-only', 'validation-output/performance-baseline-native.csv') 120000
    }
    dotnet build validation/PerformanceChecks.csproj -c Release --nologo -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Performance build failed' }
    Invoke-Measurement ./validation/bin/Release/net48/PerformanceChecks.exe @('validation-output/performance.csv') 300000
    Invoke-Measurement ./validation/bin/Release/net48/PerformanceChecks.exe @('--native', 'validation-output/performance-native.csv') 300000
} finally {
    if ($baseline -and (Test-Path $baseline)) { Remove-Item $baseline -Recurse -Force }
    Pop-Location
}
