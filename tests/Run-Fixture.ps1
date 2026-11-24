param(
    [string]$GameDir = $(if ($env:STS2_GAME_DIR) { $env:STS2_GAME_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2' }),
    [string]$RuntimeDir = $(if ($env:STS2_TEST_RUNTIME_DIR) { $env:STS2_TEST_RUNTIME_DIR } else { Join-Path (Split-Path $PSScriptRoot) '.build/runtime' }),
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$projectRoot = Split-Path $PSScriptRoot
$runId = [guid]::NewGuid().ToString('N').Substring(0, 8)
$workRoot = Join-Path $RuntimeDir "fixture-$runId"
[void][IO.Directory]::CreateDirectory($workRoot)

Write-Host "=== [Fixture 1/3] Building MidRunJoin & MidRunJoinSmoke (Release) ==="
dotnet build (Join-Path $projectRoot 'MidRunJoin.csproj') -c Release "-p:GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw "MidRunJoin build failed" }
dotnet build (Join-Path $PSScriptRoot 'MidRunJoinSmoke.csproj') -c Release "-p:GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw "MidRunJoinSmoke build failed" }

Write-Host "=== [Fixture 2/3] Staging mods into $RuntimeDir/mods ==="
$modDest = Join-Path $RuntimeDir 'mods/MidRunJoin'
$smokeDest = Join-Path $RuntimeDir 'mods/MidRunJoinSmoke'
[void][IO.Directory]::CreateDirectory($modDest)
[void][IO.Directory]::CreateDirectory($smokeDest)
Copy-Item -Force (Join-Path $projectRoot 'dist/MidRunJoin/MidRunJoin.dll') $modDest
Copy-Item -Force (Join-Path $projectRoot 'dist/MidRunJoin/MidRunJoin.json') $modDest
Copy-Item -Force (Join-Path $PSScriptRoot 'bin/Release/net9.0/MidRunJoinSmoke.dll') $smokeDest
Copy-Item -Force (Join-Path $PSScriptRoot 'bin/Release/net9.0/MidRunJoinSmoke.json') $smokeDest

$exe = Join-Path $RuntimeDir 'SlayTheSpire2.exe'
$pck = Join-Path $GameDir 'SlayTheSpire2.pck'
if (!(Test-Path $exe)) { throw "Missing isolated runtime exe: $exe" }
if (!(Test-Path $pck)) { throw "Missing game pck: $pck" }

# Keep isolated runtime assemblies synced with $GameDir whenever Steam switches between public and public-beta.
$srcDll = Join-Path $GameDir 'data_sts2_windows_x86_64/sts2.dll'
$dstDll = Join-Path $RuntimeDir 'data_sts2_windows_x86_64/sts2.dll'
if ((Test-Path $srcDll) -and (!(Test-Path $dstDll) -or (Get-FileHash $srcDll -Algorithm SHA256).Hash -ne (Get-FileHash $dstDll -Algorithm SHA256).Hash)) {
    Write-Host "  Syncing isolated runtime assemblies from $GameDir..."
    Copy-Item -Recurse -Force (Join-Path $GameDir 'data_sts2_windows_x86_64/*') (Join-Path $RuntimeDir 'data_sts2_windows_x86_64')
    if (Test-Path (Join-Path $GameDir 'release_info.json')) {
        Copy-Item -Force (Join-Path $GameDir 'release_info.json') $RuntimeDir
    }
}

$profileDir = Join-Path $workRoot 'profile'
$dest = Join-Path $profileDir 'SlayTheSpire2/default/1'
[void][IO.Directory]::CreateDirectory($dest)
$settings = [pscustomobject]@{
    mod_settings = @{ mods_enabled = $true; mod_list = @() }
    seen_ea_disclaimer = $true
}
$settings | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $dest 'settings.save')

$log = Join-Path $workRoot 'fixture.log'
Write-Host "=== [Fixture 3/3] Running headless in-game fixture regression ==="
$psi = [Diagnostics.ProcessStartInfo]::new($exe)
$psi.WorkingDirectory = $RuntimeDir
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.WindowStyle = 'Hidden'
foreach ($k in 'APPDATA', 'LOCALAPPDATA', 'TEMP', 'TMP') { $psi.Environment[$k] = $profileDir }
foreach ($a in '--main-pack', $pck, '--headless', '--max-fps', '30', '--log-file', $log,
               '--force-steam=off', '--fastmp=load', '--clientId=1', '--midrunjoin-smoke') {
    $psi.ArgumentList.Add($a)
}

$proc = [Diagnostics.Process]::Start($psi)
try {
    $exited = $proc.WaitForExit($TimeoutSeconds * 1000)
    if (!$exited) {
        $proc.Kill()
        throw "Fixture test timed out after ${TimeoutSeconds}s. Log: $log"
    }
    $txt = if (Test-Path $log) { Get-Content -LiteralPath $log -Encoding utf8 -Raw } else { '' }
    ($txt -split "`n" | Where-Object { $_ -match '\[MidRunJoin' }) | ForEach-Object { Write-Host "  $_" }
    if ($proc.ExitCode -ne 0 -or $txt -match '\[MidRunJoinSmoke\] FAIL' -or $txt -notmatch '(?m)\[MidRunJoinSmoke\] PASS\r?$') {
        throw "Fixture test failed (exit=$($proc.ExitCode)). Log: $log"
    }
    Write-Host "=== [Fixture] ALL CHECKS PASSED (log=$log) ==="
} finally {
    if (!$proc.HasExited) { try { $proc.Kill() } catch {} }
    $proc.Dispose()
}
