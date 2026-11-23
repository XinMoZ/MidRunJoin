param(
    [string]$GameDir = 'E:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2',
    [string]$RuntimeDir = 'D:\Work\STS2mods\.codex_tmp\midrun-runtime',
    [string]$GuestChar = 'SILENT',
    [string]$Guest4Char = 'DEFECT',
    [int]$PhaseTimeoutSeconds = 150
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$projectRoot = Split-Path $PSScriptRoot
$runId = [guid]::NewGuid().ToString('N').Substring(0, 8)
$workRoot = Join-Path $RuntimeDir "e2e-$runId"
[void][IO.Directory]::CreateDirectory($workRoot)

Write-Host "=== [E2E 1/6] Building MidRunJoin & MidRunJoinSmoke (Release) ==="
dotnet build (Join-Path $projectRoot 'MidRunJoin.csproj') -c Release "-p:GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw "MidRunJoin build failed" }
dotnet build (Join-Path $PSScriptRoot 'MidRunJoinSmoke.csproj') -c Release "-p:GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw "MidRunJoinSmoke build failed" }

Write-Host "=== [E2E 2/6] Staging mods into isolated runtime: $RuntimeDir/mods ==="
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
if (!(Test-Path $exe)) { throw "Missing isolated runtime exe: $exe" }
if (!(Test-Path $pck)) { throw "Missing game pck: $pck" }

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
$cancel1Dir = Join-Path $workRoot 'cancel-peer1'
$cancel2Dir = Join-Path $workRoot 'cancel-peer2'

try {
    Write-Host "=== [E2E 3/6] Phase 1: 2P run -> beat 1st monster -> return to Map (PreFinishedRoom checkpoint) ==="
    $p1 = Start-Peer $peer1Dir 1 'host_standard' 'phase1' 'phase1.log' $children
    Wait-ForPort $p1.Proc $p1.Log 60
    $p2 = Start-Peer $peer2Dir 2 'join' 'phase1' 'phase1.log' $children
    Wait-ForMarker @($p1, $p2) 'PHASE1_PASS' 'Phase 1' $PhaseTimeoutSeconds
    Write-Host "  Phase 1 PASS! (Real map checkpoint saved)"
    Stop-All $children
    Start-Sleep -Seconds 1

    Copy-Item -Recurse -Force $peer1Dir $cancel1Dir
    Copy-Item -Recurse -Force $peer2Dir $cancel2Dir

    Write-Host "=== [E2E 4/6] Cancel-2P: Shift-load map checkpoint -> Cancel -> normal 2P load -> verify 2P scaling ==="
    $c1 = Start-Peer $cancel1Dir 1 'load' 'cancel2p' 'cancel2p.log' $children
    Wait-ForPort $c1.Proc $c1.Log 60
    $c2 = Start-Peer $cancel2Dir 2 'join' 'cancel2p' 'cancel2p.log' $children
    Wait-ForMarker @($c1, $c2) 'CANCEL2P_PASS' 'Cancel-2P' $PhaseTimeoutSeconds
    Write-Host "  Cancel-2P PASS!"
    Stop-All $children
    Start-Sleep -Seconds 1

    Write-Host "=== [E2E 5/6] Phase 2: 2P -> 4P Multi-Guest ($GuestChar + $Guest4Char) + CatchUp (Ancient/Gold/TempCourier/Shop) + 4P Combat ==="
    $p1 = Start-Peer $peer1Dir 1 'load' 'phase2' 'phase2.log' $children
    Wait-ForPort $p1.Proc $p1.Log 60
    $p2 = Start-Peer $peer2Dir 2 'join' 'phase2' 'phase2.log' $children
    $p3 = Start-Peer $peer3Dir 3 'join' 'phase2' 'phase2.log' $children
    $p4 = Start-Peer $peer4Dir 4 'join' 'phase2' 'phase2.log' $children
    $p2Peers = @($p1, $p2, $p3, $p4)
    Wait-ForMarker $p2Peers 'PHASE2_PASS' 'Phase 2' $PhaseTimeoutSeconds

    foreach ($p in $p2Peers) {
        $txt = Read-Text $p.Log
        foreach ($marker in 'SCALING_OK', 'PLAYED', 'END_TURN_1', 'TURN_2_REACHED', 'REWARDS_OK', 'ROOM_NEXT_ENTERED', 'PHASE2_PASS') {
            if ($txt -notmatch "\[MidRunJoinE2E\] $marker") {
                throw "Missing marker $marker in Phase 2 peer$($p.Id) log: $($p.Log)"
            }
        }
        if ($p.Id -in 3, 4 -and $txt -notmatch '\[MidRunJoinE2E\] CATCHUP_BELL_REWARDS_OK') {
            throw "Missing CATCHUP_BELL_REWARDS_OK in Phase 2 guest peer$($p.Id) log: $($p.Log)"
        }
        if ($p.Id -eq 1 -and $txt -notmatch '\[MidRunJoinE2E\] SAVE4P_OK') {
            throw "Missing SAVE4P_OK in Phase 2 host log: $($p.Log)"
        }
    }
    Write-Host "  Phase 2 PASS! (4P multi-guest catch-up, sync, 4P combat, rewards & save verified)"
    Stop-All $children
    Start-Sleep -Seconds 1

    Write-Host "=== [E2E 6/6] Phase 3: Remove absent peer 4 via GUI (4P -> 3P) & verify 3P reload/scaling ==="
    $r1 = Start-Peer $peer1Dir 1 'load' 'phase3' 'phase3.log' $children
    Wait-ForPort $r1.Proc $r1.Log 60
    $r2 = Start-Peer $peer2Dir 2 'join' 'phase3' 'phase3.log' $children
    $r3 = Start-Peer $peer3Dir 3 'join' 'phase3' 'phase3.log' $children
    $p3Peers = @($r1, $r2, $r3)
    Wait-ForMarker $p3Peers 'PHASE3_PASS' 'Phase 3' $PhaseTimeoutSeconds

    foreach ($p in $p3Peers) {
        $txt = Read-Text $p.Log
        Write-Host "--- peer$($p.Id) Phase 3 summary ---"
        ($txt -split "`n" | Where-Object { $_ -match '\[MidRunJoin' }) | ForEach-Object { Write-Host "  $_" }
    }
    Write-Host "=== ALL E2E PHASES PASSED (MidCombat Phase1 + Cancel2P + Phase2 2P->4P CatchUp + Phase3 4P->3P Removal | workRoot=$workRoot) ==="
} finally {
    Stop-All $children
    Write-Host "Logs saved at: $workRoot"
}
