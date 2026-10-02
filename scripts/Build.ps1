param([string]$Configuration = 'Release', [switch]$SelfSign)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
dotnet restore "$root\OnDemandPerf.sln" --configfile "$root\NuGet.Config"
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
dotnet build "$root\OnDemandPerf.sln" -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$destination = Join-Path $root 'artifacts\OnDemandPerf'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item "$root\src\OnDemandPerf\bin\$Configuration\net10.0-windows\*" $destination -Recurse -Force
if ($SelfSign) { & "$PSScriptRoot\Sign-Local.ps1" -Path $destination }
Write-Host "Ready: $destination\OnDemandPerf.exe"
