# Fast, hardware-free logic tests. No NuGet packages are needed (see tests\OnDemandPerf.Tests).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = "$root\tests\OnDemandPerf.Tests\OnDemandPerf.Tests.csproj"
dotnet restore $project --configfile "$root\NuGet.Config"
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
dotnet run --project $project -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
