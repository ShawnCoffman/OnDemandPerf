#Requires -Version 7.0
param([switch]$Keep)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'artifacts\OnDemandPerf\worker\OnDemandPerf.Worker.exe'
$testRoot = Join-Path $root ('artifacts\smoke-' + [guid]::NewGuid().ToString('N'))
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Start-Worker([string]$mode, [string]$directory) {
    $info = [System.Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.ArgumentList.Add($mode)
    $info.ArgumentList.Add('/output')
    $info.ArgumentList.Add($directory)
    return [System.Diagnostics.Process]::Start($info)
}
function Finish-Worker($process, [int]$expected) {
    Assert ($process.WaitForExit(25000)) 'Worker exceeded smoke-test deadline.'
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    Assert ($process.ExitCode -eq $expected) "Expected exit $expected, got $($process.ExitCode): $stdout $stderr"
    $process.Dispose()
}
$inventoryPath = Join-Path $testRoot 'inventory'
Finish-Worker (Start-Worker '/inventory' $inventoryPath) 0
$report = Get-Content -Raw "$inventoryPath\Latest.json" | ConvertFrom-Json
Assert ($report.status -eq 'Success' -and $null -eq $report.cpu) 'Inventory unexpectedly ran a benchmark.'
Assert ($report.device.logicalProcessors -gt 0) 'Missing CPU inventory.'
Assert ($report.device.physicalCores -gt 0) 'Missing physical core count.'
Assert ($report.device.systemLogicalProcessors -ge $report.device.physicalCores) 'Invalid CPU topology.'
Assert ($report.environment.totalPhysicalBytes -gt 0) 'Missing RAM inventory.'
Assert ($report.environment.usedPhysicalBytes + $report.environment.availablePhysicalBytes -eq $report.environment.totalPhysicalBytes) 'RAM accounting mismatch.'
Assert ($report.environment.ramUtilizationPercent -ge 0 -and $report.environment.ramUtilizationPercent -le 100) 'Invalid RAM utilization.'
if ($report.environment.cpuSampleStatus -eq 'Success') {
    Assert ($report.environment.cpuUtilizationPercent -ge 0 -and $report.environment.cpuUtilizationPercent -le 100) 'Invalid CPU activity.'
    Assert ($report.environment.cpuSampleDurationSeconds -ge 2) 'CPU baseline was too short.'
}
Assert ($report.device.windowsBuild) 'Missing Windows build.'
Assert (Test-Path "$inventoryPath\Latest.txt") 'Missing text report.'

$cpuPath = Join-Path $testRoot 'cpu'
$cpu = Start-Worker '/cpu' $cpuPath
# Wait for the progress line, proving the first worker holds its lock before starting the second.
Assert ($cpu.StandardOutput.ReadLine().Contains('Sampling')) 'CPU worker failed to start.'
Finish-Worker (Start-Worker '/inventory' (Join-Path $testRoot 'overlap')) 3
Finish-Worker $cpu 0
$report = Get-Content -Raw "$cpuPath\Latest.json" | ConvertFrom-Json
Assert ($report.cpu.multiThread.passes.Count -eq 3) 'Expected three measured passes.'
Assert ($report.cpu.multiThread.mBps -gt 0) 'Invalid CPU throughput.'
Assert ($report.cpu.profileHash -match '^[0-9a-f]{8}$') 'Missing CPU profile fingerprint.'
Assert ($report.cpu.multiThread.medianMBps -gt 0) 'Missing median pass rate.'
Assert ($report.schemaVersion -eq '2.2') 'Wrong result schema.'
Assert ($report.cpu.benchmarkVersion -eq 'deflate-v4') 'Incorrect benchmark version.'
Assert ($report.cpu.outputVerified) 'Compression output was not verified.'
Assert ($report.cpu.durationSeconds -lt 20) 'CPU stage exceeded safety budget.'
foreach ($stage in @($report.cpu.singleThread, $report.cpu.multiThread)) {
    Assert ($stage.passes.Count -eq 3) 'Missing stage passes.'
    Assert ($stage.mBps -gt 0) 'Invalid stage throughput.'
    $sumBytes = 0.0
    $sumSeconds = 0.0
    foreach ($pass in $stage.passes) {
        Assert ($pass.participatingWorkers -eq $stage.requestedWorkers) 'Not all workers participated.'
        Assert ($pass.workerBytesProcessed.Count -eq $stage.requestedWorkers) 'Missing per-worker counters.'
        Assert (($pass.workerBytesProcessed | Measure-Object -Sum).Sum -eq $pass.bytesProcessed) 'Worker byte accounting mismatch.'
        Assert ($pass.outputVerified) 'Pass output verification missing.'
        Assert ($pass.processCpuSeconds -gt 0) 'Missing measured process CPU time.'
        Assert ($pass.durationSeconds -ge ($stage.requestedMillisecondsPerPass / 1000)) 'Pass shorter than requested.'
        $sumBytes += $pass.bytesProcessed
        $sumSeconds += $pass.durationSeconds
    }
    Assert ([math]::Abs($stage.mBps - $sumBytes / $sumSeconds / 1000000) -lt 0.001) 'Stage aggregation mismatch.'
}
Assert ($report.cpu.singleThread.requestedWorkers -eq 1) 'Single-thread test used multiple workers.'
foreach ($pass in $report.cpu.multiThread.passes) {
    Assert ($pass.bytesProcessed -gt 0 -and $pass.durationSeconds -ge 3) 'Invalid pass measurement.'
    Assert ([math]::Abs($pass.mBps - ($pass.bytesProcessed / $pass.durationSeconds / 1000000)) -lt 0.001) 'Throughput accounting mismatch.'
}
Assert (@(Get-ChildItem "$cpuPath\Results\*.json").Count -eq 1) 'Missing historical JSON.'

$cancelPath = Join-Path $testRoot 'cancel'
$cancel = Start-Worker '/cpu' $cancelPath
$null = $cancel.StandardOutput.ReadLine()
$cancel.StandardInput.WriteLine('cancel')
$cancel.StandardInput.Flush()
Finish-Worker $cancel 1
$cancelReport = Get-Content -Raw "$cancelPath\Latest.json" | ConvertFrom-Json
Assert ($cancelReport.status -eq 'Cancelled') 'Cancellation did not persist a result.'
# Exercise cancellation while the new multithread stage is active, then prove the lock was released.
$activeCancelPath = Join-Path $testRoot 'cancel-active'
$activeCancel = Start-Worker '/cpu' $activeCancelPath
while (($line = $activeCancel.StandardOutput.ReadLine()) -and -not $line.StartsWith('Multithread')) { }
Assert ($line -and $line.StartsWith('Multithread')) 'Multithread stage did not start.'
Start-Sleep -Milliseconds 500
$activeCancel.StandardInput.WriteLine('cancel')
$activeCancel.StandardInput.Flush()
Finish-Worker $activeCancel 1
$activeCancelReport = Get-Content -Raw "$activeCancelPath\Latest.json" | ConvertFrom-Json
Assert ($activeCancelReport.status -eq 'Cancelled') 'Active workers did not cancel.'
Finish-Worker (Start-Worker '/inventory' (Join-Path $testRoot 'after-cancel')) 0
Finish-Worker (Start-Worker '/invalid' (Join-Path $testRoot 'invalid')) 2

# Force an output error using a file where a directory is required.
$blocked = Join-Path $testRoot 'blocked'
Set-Content -LiteralPath $blocked -Value 'Not a directory'
Finish-Worker (Start-Worker '/inventory' $blocked) 2

$tray = Start-Process -FilePath (Join-Path $root 'artifacts\OnDemandPerf\OnDemandPerf.exe') -WindowStyle Hidden -PassThru
try {
    Start-Sleep -Seconds 2
    $tray.Refresh()
    Assert (-not $tray.HasExited) 'Tray failed to remain running.'
    Assert ($tray.MainWindowHandle -eq 0) 'Tray opened a window at startup.'
    Assert (@(Get-Process -Name OnDemandPerf.Worker -ErrorAction SilentlyContinue).Count -eq 0) 'Tray launched a worker at startup.'
    $cpuBefore = $tray.TotalProcessorTime.TotalMilliseconds
    Start-Sleep -Seconds 3
    $tray.Refresh()
    Write-Host "Tray working set: $([math]::Round($tray.WorkingSet64 / 1MB, 1)) MiB; CPU time over 3 seconds: $($tray.TotalProcessorTime.TotalMilliseconds - $cpuBefore) ms."
} finally { if (-not $tray.HasExited) { $tray.Kill(); $tray.WaitForExit() }; $tray.Dispose() }
if ($Keep) { Write-Host "Results kept: $testRoot" } else { Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host 'PASS: inventory, CPU accounting, overlap prevention, cancellation, bad arguments, output failure, idle tray.'


