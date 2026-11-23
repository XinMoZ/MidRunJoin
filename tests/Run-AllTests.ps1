param(
    [string]$GameDir = 'E:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2',
    [string]$GuestChar = 'SILENT',
    [switch]$SkipE2E
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$sw = [Diagnostics.Stopwatch]::StartNew()

Write-Host "=========================================================="
Write-Host " MidRunJoin Comprehensive Test Suite"
Write-Host "=========================================================="

Write-Host "`n>>> [Layer 1/4] Pure Offline Unit Tests & Packaging Hygiene <<<"
dotnet run --project (Join-Path $PSScriptRoot 'UnitTests/MidRunJoinUnitTests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "Layer 1 (Unit Tests) failed" }

Write-Host "`n>>> [Layer 2/4] Assembly & Harmony Compatibility Probe (MegaDot headless) <<<"
& (Join-Path $PSScriptRoot 'Run-CompatProbe.ps1') -GameDir $GameDir
if ($LASTEXITCODE -ne 0) { throw "Layer 2 (Compat Probe) failed" }

Write-Host "`n>>> [Layer 3/4] In-Game Fixture & Regression Suite (Headless Game) <<<"
& (Join-Path $PSScriptRoot 'Run-Fixture.ps1') -GameDir $GameDir
if ($LASTEXITCODE -ne 0) { throw "Layer 3 (In-Game Fixture) failed" }

if (-not $SkipE2E) {
    Write-Host "`n>>> [Layer 4/4] Multi-Process Real ENet E2E (Phase1 + Cancel2P + Phase2[$GuestChar] + Phase3 Reload) <<<"
    & (Join-Path $PSScriptRoot 'Run-E2E.ps1') -GameDir $GameDir -GuestChar $GuestChar
    if ($LASTEXITCODE -ne 0) { throw "Layer 4 (Multi-Process E2E) failed" }
}

$sw.Stop()
Write-Host "`n=========================================================="
Write-Host " ALL TEST LAYERS PASSED in $([math]::Round($sw.Elapsed.TotalSeconds, 1))s"
Write-Host "=========================================================="
