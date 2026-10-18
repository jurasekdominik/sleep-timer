$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    dotnet build validation/WindowsChecks.csproj -c Release --nologo -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Windows validation build failed' }
    $process = Start-Process ./validation/bin/Release/net48/WindowsChecks.exe -NoNewWindow -PassThru
    if (-not $process.WaitForExit(180000)) {
        # Capture managed stacks before terminating an unresponsive UI check.
        $traceDir = Join-Path $env:TEMP 'sleep-timer-check-stacks'
        New-Item $traceDir -ItemType Directory -Force | Out-Null
        @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.Diagnostics.Runtime" Version="3.1.512801" /></ItemGroup></Project>
'@ | Set-Content (Join-Path $traceDir 'Stacks.csproj')
        @'
using System;
using Microsoft.Diagnostics.Runtime;
using var target = DataTarget.AttachToProcess(int.Parse(args[0]), suspend: true);
foreach (var version in target.ClrVersions) {
    var runtime = version.CreateRuntime();
    foreach (var thread in runtime.Threads) {
        Console.WriteLine("THREAD " + thread.OSThreadId);
        int count = 0;
        foreach (var frame in thread.EnumerateStackTrace()) {
            Console.WriteLine("  " + frame);
            if (++count == 20) break;
        }
    }
}
'@ | Set-Content (Join-Path $traceDir 'Program.cs')
        $trace = Start-Process dotnet -WorkingDirectory $traceDir -ArgumentList @('run', '--project', 'Stacks.csproj', '--', $process.Id) -NoNewWindow -PassThru
        if (-not $trace.WaitForExit(60000)) { $trace.Kill() }
        $process.Kill()
        throw 'Windows checks timed out after 3 minutes; see the last CHECK/PREVIEW marker'
    }
    if ($process.ExitCode -ne 0) { throw "Windows checks failed: $($process.ExitCode)" }
} finally { Pop-Location }
