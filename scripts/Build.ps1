param([string]$Configuration = 'Release', [switch]$SelfSign)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$destination = Join-Path $root 'artifacts\OnDemandPerf'
$running = @(Get-Process -Name OnDemandPerf, OnDemandPerf.Worker -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($destination, [StringComparison]::OrdinalIgnoreCase) })
if ($running.Count -gt 0) { throw "Close OnDemand Perf first (process $($running.Id -join ', ') runs from $destination)." }
# Start from empty output folders so files from earlier builds cannot linger in the package.
$outputs = @(
    (Join-Path $root "src\OnDemandPerf\bin\$Configuration"),
    (Join-Path $root "src\OnDemandPerf.Worker\bin\$Configuration"),
    $destination)
foreach ($folder in $outputs) { if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force } }
dotnet restore "$root\OnDemandPerf.sln" --configfile "$root\NuGet.Config"
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
dotnet build "$root\OnDemandPerf.sln" -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item "$root\src\OnDemandPerf\bin\$Configuration\net10.0-windows\*" $destination -Recurse -Force
if ($SelfSign) { & "$PSScriptRoot\Sign-Local.ps1" -Path $destination }
Write-Host "Ready: $destination\OnDemandPerf.exe"
