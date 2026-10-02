param([switch]$Disable)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'artifacts\OnDemandPerf\OnDemandPerf.exe'
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($Disable) {
    Remove-ItemProperty -LiteralPath $key -Name OnDemandPerf -ErrorAction SilentlyContinue
    Write-Host 'OnDemand Perf sign-in startup disabled.'
} else {
    if (-not (Test-Path -LiteralPath $exe)) { throw 'Run scripts\Build.ps1 first.' }
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -LiteralPath $key -Name OnDemandPerf -Value ('"' + $exe + '"') -PropertyType String -Force | Out-Null
    Write-Host 'OnDemand Perf will start in the tray at your next sign-in. No test runs at startup.'
}
