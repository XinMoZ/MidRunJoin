param(
    [string]$GameDir = $(if ($env:STS2_GAME_DIR) { $env:STS2_GAME_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2' }),
    [string]$GodotPath = (Join-Path $PSScriptRoot '../../tools/megadot/MegaDot_v4.5.1-stable_mono_win64_console.exe'),
    [string]$DotnetRoot = 'C:/Program Files/dotnet'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$projectRoot = Split-Path $PSScriptRoot
$GameDir = [IO.Path]::GetFullPath($GameDir)
$GodotPath = [IO.Path]::GetFullPath($GodotPath)

$variables = @('APPDATA', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROLL_FORWARD')
$saved = @{}
foreach ($k in $variables) { $saved[$k] = [Environment]::GetEnvironmentVariable($k) }

try {
    $env:APPDATA = Join-Path $projectRoot '.build/appdata'
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.build'
    $env:NUGET_PACKAGES = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    New-Item -ItemType Directory -Path $env:APPDATA -Force | Out-Null
    Write-Host "=== [CompatProbe] Building MidRunJoin (Release) & CompatibilityProbe ==="
    dotnet build (Join-Path $projectRoot 'MidRunJoin.csproj') -c Release "-p:GameDir=$GameDir" --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw "MidRunJoin build failed" }
    dotnet build (Join-Path $PSScriptRoot 'compat/CompatibilityProbe.csproj') -c Debug --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw "CompatibilityProbe build failed" }

    # Isolate .NET 9 runtime (older Harmony cannot run under .NET 10)
    $runtime = Get-ChildItem -LiteralPath (Join-Path $DotnetRoot 'shared/Microsoft.NETCore.App') -Directory |
        Where-Object Name -Like '9.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    $hostFxr = Get-ChildItem -LiteralPath (Join-Path $DotnetRoot 'host/fxr') -Directory |
        Where-Object Name -Like '9.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (!$runtime -or !$hostFxr) { throw "Installed .NET 9 runtime/hostfxr required." }

    $isolated = Join-Path $PSScriptRoot 'compat/.godot/compat-runtime'
    New-Item -ItemType Directory -Path "$isolated/shared/Microsoft.NETCore.App", "$isolated/host/fxr" -Force | Out-Null
    foreach ($pair in @(
        @{ Path = "$isolated/shared/Microsoft.NETCore.App/$($runtime.Name)"; Target = $runtime.FullName },
        @{ Path = "$isolated/host/fxr/$($hostFxr.Name)"; Target = $hostFxr.FullName }
    )) {
        if (!(Test-Path -LiteralPath $pair.Path)) {
            New-Item -ItemType Junction -Path $pair.Path -Target $pair.Target | Out-Null
        }
    }

    $env:DOTNET_ROOT = $isolated
    $env:DOTNET_ROOT_X64 = $isolated
    $env:DOTNET_ROLL_FORWARD = 'Major'

    $gameData = Join-Path $GameDir 'data_sts2_windows_x86_64'
    $modDll = Join-Path $projectRoot 'dist/MidRunJoin/MidRunJoin.dll'
    $gameSha = (Get-FileHash -LiteralPath (Join-Path $gameData 'sts2.dll') -Algorithm SHA256).Hash
    $modSha = (Get-FileHash -LiteralPath $modDll -Algorithm SHA256).Hash
    Write-Host "  sts2.dll SHA-256: $gameSha"
    Write-Host "  MidRunJoin.dll SHA-256: $modSha"

    $log = & $GodotPath --headless --path (Join-Path $PSScriptRoot 'compat') -- $gameData $modDll 2>&1 | ForEach-Object { $_.ToString() }
    $code = $LASTEXITCODE
    $log | Write-Output
    if ($code -ne 0 -or !($log -match 'COMPAT PASS:') -or !($log -match 'HARMONY PASS:')) {
        throw "Compatibility probe failed (exit=$code)"
    }
    Write-Host "=== [CompatProbe] ALL CHECKS PASSED ==="
} finally {
    foreach ($k in $variables) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
}
