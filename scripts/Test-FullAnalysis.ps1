#Requires -Version 7.0
param([switch]$Keep)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'artifacts\OnDemandPerf\worker\OnDemandPerf.Worker.exe'
$testRoot = Join-Path $root ('artifacts\full-smoke-' + [guid]::NewGuid().ToString('N'))
$diskPath = Join-Path $testRoot 'disk-data'
New-Item -ItemType Directory -Force -Path $diskPath | Out-Null
$sentinel = Join-Path $diskPath 'existing-user-file.txt'
Set-Content -LiteralPath $sentinel -Value 'Do not touch this file.'
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Start-Worker([string]$mode, [string]$name, [string]$disk = $diskPath) {
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($arg in @($mode, '/output', (Join-Path $testRoot $name), '/diskpath', $disk)) { $info.ArgumentList.Add($arg) }
    return [Diagnostics.Process]::Start($info)
}
function Finish($process, [int]$expected) {
    try {
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'Worker exceeded 60 seconds.' }
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        Assert ($process.ExitCode -eq $expected) "Unexpected exit $($process.ExitCode): $stdout $stderr"
    } finally { $process.Dispose() }
}
function Wait-Phase($process, [string]$phase) {
    do {
        $read = $process.StandardOutput.ReadLineAsync()
        if (-not $read.Wait(30000)) { $process.Kill($true); throw "Timed out waiting for $phase" }
        $line = $read.Result
    } while ($null -ne $line -and -not $line.StartsWith($phase))
    Assert ($null -ne $line) "Worker exited before $phase"
}
function Check-Cleanup {
    Assert (@(Get-ChildItem -LiteralPath $diskPath -Filter 'odp-*.tmp').Count -eq 0) 'Benchmark file leaked.'
    Assert ((Get-Content -Raw -LiteralPath $sentinel).Trim() -eq 'Do not touch this file.') 'Existing file was modified.'
}
Finish (Start-Worker '/run' 'complete') 0
$report = Get-Content -Raw (Join-Path $testRoot 'complete\Latest.json') | ConvertFrom-Json
Assert ($report.durationSeconds -lt 60) 'Full analysis exceeded target.'
Assert ($report.schemaVersion -eq '2.2') 'Wrong result schema.'
Assert ($report.device.hardware -and $report.device.testDrive.busType) 'Hardware identity (SMBIOS / test drive) missing from the report.'
Assert ($report.cpu.benchmarkVersion -eq 'deflate-v4') 'Wrong CPU profile.'
Assert ($report.memory.benchmarkVersion -eq 'memory-v4') 'Wrong memory profile.'
Assert ($report.disk.benchmarkVersion -eq 'disk-qd1-v3') 'Wrong disk profile.'
foreach ($stage in @($report.cpu, $report.memory, $report.disk)) { Assert ($stage.profileHash -match '^[0-9a-f]{8}$') 'Missing profile fingerprint.' }
Assert ($report.activity.samples.Count -gt 10) 'Missing activity timeline.'
foreach ($sample in $report.activity.samples) {
    Assert ($sample.intervalSeconds -gt 0) 'Invalid activity sample interval.'
    if ($null -ne $sample.systemCpuPercent) { Assert ($sample.systemCpuPercent -ge 0 -and $sample.systemCpuPercent -le 100) 'Invalid sampled CPU load.' }
}
Assert ($report.memory.latency.status -eq 'Success' -and $report.memory.latency.cycleVerified) 'Latency chain validation failed.'
Assert ($report.memory.latency.passes.Count -eq 3) 'Missing latency passes.'
foreach ($pass in $report.memory.latency.passes) {
    Assert ($pass.accesses -gt 0 -and $pass.durationSeconds -ge 1.25) 'Latency pass too short.'
    Assert ([math]::Abs($pass.nanosecondsPerAccess - $pass.durationSeconds * 1e9 / $pass.accesses) -lt .0001) 'Latency accounting mismatch.'
}
$latencyTime = ($report.memory.latency.passes | Measure-Object durationSeconds -Sum).Sum
$accesses = ($report.memory.latency.passes | Measure-Object accesses -Sum).Sum
Assert ([math]::Abs($report.memory.latency.nanosecondsPerAccess - $latencyTime * 1e9 / $accesses) -lt .0001) 'Latency aggregate mismatch.'
$multi = [Environment]::ProcessorCount -ge 2
$variations = if ($multi) { 5 } else { 3 }
Assert (@($report.memory.variationPercent.PSObject.Properties).Count -eq $variations) 'Missing memory variation.'
if ($multi) {
    Assert ($report.memory.multiThreadWorkers -ge 2) 'Multithread RAM pass did not run.'
    Assert ($report.memory.multiThreadReadGBps -gt 0 -and $report.memory.multiThreadCopyGBps -gt 0) 'Missing multithread RAM rates.'
}
Assert ($report.cpu.outputVerified) 'CPU stage failed.'
$expectedPasses = if ($multi) { 15 } else { 9 }
Assert ($report.memory.passes.Count -eq $expectedPasses) "RAM did not complete all $expectedPasses passes."
foreach ($pass in $report.memory.passes) {
    Assert ($pass.verified) 'RAM verification failed.'
    $minimum = if ($pass.operation -like '*MT') { .4 } else { .9 }
    Assert ($pass.bytesProcessed -gt 0 -and $pass.durationSeconds -ge $minimum) 'Invalid RAM pass.'
    Assert ([math]::Abs($pass.gBps - $pass.bytesProcessed / $pass.durationSeconds / 1e9) -lt .0001) 'RAM accounting mismatch.'
}
Assert ($report.disk.status -eq 'Success') 'Disk stage failed.'
Assert ($report.disk.measurements.Count -eq 4) 'Missing disk measurements.'
Assert ($report.disk.totalBytesWritten -le 1088MB) 'Write budget exceeded.'
Assert ($report.disk.cleanupSucceeded) 'Reported cleanup failure.'
foreach ($measurement in $report.disk.measurements) {
    Assert ($measurement.shortBurst -eq ($measurement.durationSeconds -lt 1)) 'Short-burst classification mismatch.'
    if ($measurement.operation -eq 'SequentialRead') {
        Assert ($measurement.durationSeconds -ge 6 -or $measurement.stopReason -eq 'SampleCap') 'Sequential read observation too short.'
        Assert ($null -ne $measurement.filePasses) 'Missing sequential read pass count.'
        if ($measurement.filePasses -ge 1) { Assert ($measurement.firstPassMBps -gt 0) 'Missing first-pass sequential read rate.' }
    }
    Assert ($measurement.verified) 'Disk output not verified.'
    Assert ($measurement.bytesProcessed -eq $measurement.operations * $measurement.blockBytes) 'Disk byte accounting mismatch.'
    Assert ([math]::Abs($measurement.mBps - $measurement.bytesProcessed / $measurement.durationSeconds / 1e6) -lt .0001) 'Disk throughput mismatch.'
    Assert ($measurement.meanLatencyMs -gt 0 -and $measurement.p95LatencyMs -gt 0) 'Missing latency.'
}
Check-Cleanup

$cancelDisk = Start-Worker '/disk' 'cancel-disk'
Wait-Phase $cancelDisk 'Disk sequential read'
$cancelDisk.StandardInput.WriteLine('cancel'); $cancelDisk.StandardInput.Flush()
Finish $cancelDisk 1
$cancelled = Get-Content -Raw (Join-Path $testRoot 'cancel-disk\Latest.json') | ConvertFrom-Json
Assert ($cancelled.status -eq 'Cancelled' -and $cancelled.disk.cleanupSucceeded) 'Disk cancellation failed.'
Check-Cleanup

$killDisk = Start-Worker '/disk' 'kill-disk'
Wait-Phase $killDisk 'Disk random read'
$killDisk.Kill($true)
$killDisk.WaitForExit()
$killDisk.Dispose()
Check-Cleanup

$cancelRam = Start-Worker '/memory' 'cancel-ram'
Wait-Phase $cancelRam 'RAM read'
$cancelRam.StandardInput.WriteLine('cancel'); $cancelRam.StandardInput.Flush()
Finish $cancelRam 1
$cancelledRam = Get-Content -Raw (Join-Path $testRoot 'cancel-ram\Latest.json') | ConvertFrom-Json
Assert ($cancelledRam.status -eq 'Cancelled') 'RAM cancellation failed.'
Assert ($null -eq $cancelledRam.memory.copyGBps) 'Incomplete RAM aggregate was exposed.'

$cancelLatency = Start-Worker '/memory' 'cancel-latency'
Wait-Phase $cancelLatency 'RAM latency: dependent accesses'
$cancelLatency.StandardInput.WriteLine('cancel'); $cancelLatency.StandardInput.Flush()
Finish $cancelLatency 1
$latencyCancelled = Get-Content -Raw (Join-Path $testRoot 'cancel-latency\Latest.json') | ConvertFrom-Json
Assert ($latencyCancelled.status -eq 'Cancelled') 'Latency cancellation failed.'
Assert ($null -eq $latencyCancelled.memory.latency.nanosecondsPerAccess) 'Incomplete latency aggregate exposed.'
Assert ($latencyCancelled.memory.copyGBps -gt 0) 'Completed bandwidth lost during latency cancellation.'

# Directory failure must preserve earlier CPU/RAM results and never overwrite the existing file.
Finish (Start-Worker '/run' 'invalid-target' $sentinel) 1
$partial = Get-Content -Raw (Join-Path $testRoot 'invalid-target\Latest.json') | ConvertFrom-Json
Assert ($partial.status -eq 'Partial' -and $partial.cpu.outputVerified -and $partial.memory.status -eq 'Success') 'Disk failure erased earlier results.'
Assert ($partial.disk.status -eq 'Failed') 'Invalid target was not reported.'
Check-Cleanup
$duration = [math]::Round($report.durationSeconds, 1)
if ($Keep) { Write-Host "Results kept: $testRoot" } else { Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "PASS: full analysis (${duration}s), RAM accounting, disk accounting/write caps, graceful cancellation, forced-exit cleanup, existing-file safety, invalid target."


