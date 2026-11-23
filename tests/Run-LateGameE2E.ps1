param(
    [string]$GameDir = 'E:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2',
    [string]$RuntimeDir = 'D:\Work\STS2mods\.codex_tmp\midrun-runtime',
    [string]$GuestChar = 'SILENT',
    [string]$Guest4Char = 'DEFECT',
    [int]$Phase1TimeoutSeconds = 240,
    [int]$Phase2TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$projectRoot = Split-Path $PSScriptRoot
$runId = [guid]::NewGuid().ToString('N').Substring(0, 8)
$workRoot = Join-Path $RuntimeDir "e2e-lategame-$runId"
[void][IO.Directory]::CreateDirectory($workRoot)

Write-Host "=== [LateGame E2E 1/4] Building MidRunJoin & MidRunJoinSmoke (Release) ==="
dotnet build (Join-Path $projectRoot 'MidRunJoin.csproj') -c Release "-p:GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw "MidRunJoin build failed" }
dotnet build (Join-Path $PSScriptRoot 'MidRunJoinSmoke.csproj') -c Release "-p:GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw "MidRunJoinSmoke build failed" }

Write-Host "=== [LateGame E2E 2/4] Staging mods into isolated runtime: $RuntimeDir/mods ==="
$modDest = Join-Path $RuntimeDir 'mods/MidRunJoin'
$smokeDest = Join-Path $RuntimeDir 'mods/MidRunJoinSmoke'
[void][IO.Directory]::CreateDirectory($modDest)
[void][IO.Directory]::CreateDirectory($smokeDest)
Copy-Item -Force (Join-Path $projectRoot 'dist/MidRunJoin/MidRunJoin.dll') $modDest
Copy-Item -Force (Join-Path $projectRoot 'dist/MidRunJoin/MidRunJoin.json') $modDest
Copy-Item -Force (Join-Path $PSScriptRoot 'bin/Release/net9.0/MidRunJoinSmoke.dll') $smokeDest
Copy-Item -Force (Join-Path $PSScriptRoot 'bin/Release/net9.0/MidRunJoinSmoke.json') $smokeDest

$templateSettings = Join-Path $RuntimeDir 'testprofile/SlayTheSpire2/default/1/settings.save'
$exe = Join-Path $RuntimeDir 'SlayTheSpire2.exe'
$pck = Join-Path $GameDir 'SlayTheSpire2.pck'

function Init-PeerProfile([string]$profileDir, [int]$id) {
    $dest = Join-Path $profileDir "SlayTheSpire2/default/$id"
    [void][IO.Directory]::CreateDirectory($dest)
    $settingsPath = Join-Path $dest 'settings.save'
    if (!(Test-Path $settingsPath)) {
        if (Test-Path $templateSettings) {
            $settings = Get-Content $templateSettings -Raw | ConvertFrom-Json
        } else {
            $settings = [pscustomobject]@{}
        }
        $settings | Add-Member -NotePropertyName mod_settings -NotePropertyValue @{ mods_enabled = $true; mod_list = @() } -Force
        $settings | Add-Member -NotePropertyName seen_ea_disclaimer -NotePropertyValue $true -Force
        $settings | ConvertTo-Json -Depth 12 | Set-Content -Encoding utf8 $settingsPath
    }
}

function Start-Peer([string]$profileDir, [int]$id, [string]$fastmp, [string]$phase, [string]$logName, [Collections.Generic.List[Diagnostics.Process]]$bag) {
    Init-PeerProfile $profileDir $id
    $log = Join-Path $profileDir $logName
    if (Test-Path $log) { Remove-Item -Force $log }
    $psi = [Diagnostics.ProcessStartInfo]::new($exe)
    $psi.WorkingDirectory = $RuntimeDir
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.WindowStyle = 'Hidden'
    foreach ($k in 'APPDATA','LOCALAPPDATA','TEMP','TMP') { $psi.Environment[$k] = $profileDir }
    foreach ($a in '--main-pack', $pck, '--headless', '--max-fps', '30', '--log-file', $log,
                   '--force-steam=off', "--fastmp=$fastmp", "--clientId=$id",
                   "--midrunjoin-e2e=$phase", "--midrunjoin-guest-char=$GuestChar", "--midrunjoin-guest4-char=$Guest4Char") {
        $psi.ArgumentList.Add($a)
    }
    $proc = [Diagnostics.Process]::Start($psi)
    $bag.Add($proc)
    Write-Host "  Started peer$id ($phase/$fastmp) PID=$($proc.Id) -> $log"
    return @{ Proc = $proc; Log = $log; Id = $id }
}

function Read-Text([string]$path) {
    if (Test-Path $path) { return (Get-Content -LiteralPath $path -Encoding utf8 -Raw) }
    return ''
}

function Wait-ForPort([Diagnostics.Process]$hostProc, [string]$hostLog, [int]$seconds) {
    $deadline = [datetime]::UtcNow.AddSeconds($seconds)
    do {
        if ($hostProc.HasExited) { throw "Host exited early with code $($hostProc.ExitCode). Log: $hostLog" }
        $txt = Read-Text $hostLog
        if ($txt -match '\[MidRunJoinE2E\] FAIL|Exception thrown when calling mod initializer|Assemblies not found|Failed to create host!') {
            throw "Host failed before binding port. Log: $hostLog"
        }
        $owned = netstat -ano -p udp | Select-String "UDP\s+\S+:33771\s+\*:\*\s+$($hostProc.Id)\s*$"
        if ($owned) { return }
        Start-Sleep -Milliseconds 400
    } while ([datetime]::UtcNow -lt $deadline)
    throw "Host did not bind UDP 33771 within ${seconds}s. Log: $hostLog"
}

function Wait-ForMarker([array]$peers, [string]$marker, [string]$label, [int]$seconds) {
    $deadline = [datetime]::UtcNow.AddSeconds($seconds)
    while ([datetime]::UtcNow -lt $deadline) {
        $allPass = $true
        foreach ($p in $peers) {
            $txt = Read-Text $p.Log
            if ($txt -match '\[MidRunJoinE2E\] FAIL|Exception thrown when calling mod initializer') {
                throw "$label FAIL on peer$($p.Id). Log: $($p.Log)"
            }
            if ($txt -notmatch "\[MidRunJoinE2E\] $marker") {
                $allPass = $false
                if ($p.Proc.HasExited) {
                    throw "$label peer$($p.Id) exited ($($p.Proc.ExitCode)) without $marker. Log: $($p.Log)"
                }
            }
        }
        if ($allPass) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "$label timed out after ${seconds}s waiting for $marker. Logs in $workRoot"
}

function Stop-All([Collections.Generic.List[Diagnostics.Process]]$bag) {
    foreach ($p in $bag) {
        try { if (!$p.HasExited) { $p.Kill(); [void]$p.WaitForExit(5000) } } catch {} finally { $p.Dispose() }
    }
    $bag.Clear()
}

$children = [Collections.Generic.List[Diagnostics.Process]]::new()
$peer1Dir = Join-Path $workRoot 'peer1'
$peer2Dir = Join-Path $workRoot 'peer2'
$peer3Dir = Join-Path $workRoot 'peer3'
$peer4Dir = Join-Path $workRoot 'peer4'

try {
    Write-Host "=== [LateGame E2E 3/4] Phase 1: 2P full Act 1 blitz ('kill all') -> Act 1 Boss -> Act 2 save ==="
    $p1 = Start-Peer $peer1Dir 1 'host_standard' 'lategame_p1' 'lategame_p1.log' $children
    Wait-ForPort $p1.Proc $p1.Log 60
    $p2 = Start-Peer $peer2Dir 2 'join' 'lategame_p1' 'lategame_p1.log' $children
    Wait-ForMarker @($p1, $p2) 'LATEGAME_P1_PASS' 'LateGame Phase 1' $Phase1TimeoutSeconds
    (Read-Text $p1.Log) -split "`n" | Where-Object { $_ -match 'LATEGAME_P1_SAVE|LATEGAME_P1_PASS' } | ForEach-Object { Write-Host "  $_" }
    Stop-All $children
    Start-Sleep -Seconds 1

    Write-Host "=== [LateGame E2E 4/4] Phase 2: Act 2 2P -> 4P ($GuestChar + $Guest4Char) + Full HistoryBell Rewards + Act 2 4P Combat ==="
    $p1 = Start-Peer $peer1Dir 1 'load' 'lategame_p2' 'lategame_p2.log' $children
    Wait-ForPort $p1.Proc $p1.Log 60
    $p2 = Start-Peer $peer2Dir 2 'join' 'lategame_p2' 'lategame_p2.log' $children
    $p3 = Start-Peer $peer3Dir 3 'join' 'lategame_p2' 'lategame_p2.log' $children
    $p4 = Start-Peer $peer4Dir 4 'join' 'lategame_p2' 'lategame_p2.log' $children
    $p2Peers = @($p1, $p2, $p3, $p4)
    Wait-ForMarker $p2Peers 'PHASE2_PASS' 'LateGame Phase 2' $Phase2TimeoutSeconds

    foreach ($p in $p2Peers) {
        $txt = Read-Text $p.Log
        foreach ($marker in 'SCALING_OK', 'PLAYED', 'END_TURN_1', 'REWARDS_OK', 'ROOM_NEXT_ENTERED', 'PHASE2_PASS') {
            if ($txt -notmatch "\[MidRunJoinE2E\] $marker") {
                throw "Missing marker $marker in LateGame Phase 2 peer$($p.Id) log: $($p.Log)"
            }
        }
        if ($p.Id -eq 1 -and $txt -notmatch '\[MidRunJoinE2E\] TURN_2_REACHED') {
            throw "Missing TURN_2_REACHED in LateGame Phase 2 host log: $($p.Log)"
        }
        if ($p.Id -in 3, 4) {
            foreach ($gm in 'LATEGAME_BELL_BREAKDOWN', 'CATCHUP_BELL_REWARDS_OK') {
                if ($txt -notmatch "\[MidRunJoinE2E\] $gm") {
                    throw "Missing $gm in LateGame Phase 2 guest peer$($p.Id) log: $($p.Log)"
                }
            }
        }
        Write-Host "--- peer$($p.Id) LateGame Phase 2 highlights ---"
        ($txt -split "`n" | Where-Object { $_ -match 'HistoryBell built|LATEGAME_BELL_BREAKDOWN|CATCHUP_BELL_REWARDS_OK|SCALING_OK|SAVE4P_OK|PHASE2_PASS' }) | ForEach-Object { Write-Host "  $_" }
    }
    Write-Host "=== LATE-GAME ACT 2 E2E PASSED (workRoot=$workRoot) ==="
} finally {
    Stop-All $children
    Write-Host "Logs saved at: $workRoot"
}
