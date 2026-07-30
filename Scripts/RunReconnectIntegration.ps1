[CmdletBinding()]
param(
    [string]$EngineRoot = '',
    [string]$ProjectPath = '',
    [int]$Port = 17777,
    [int]$TimeoutSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\ProjectEnvironment.psm1') -Force
$projectRoot = Resolve-MahjongProjectRoot
$EngineRoot = Resolve-UnrealEngineRoot -ExplicitRoot $EngineRoot
$ProjectPath = Resolve-MahjongProjectFile -ExplicitPath $ProjectPath -ProjectRoot $projectRoot

function Test-LogMarker {
    param([string]$Path, [string]$Marker)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    return [bool](Select-String -LiteralPath $Path -SimpleMatch -Pattern $Marker -Quiet)
}

function Wait-LogMarker {
    param([string]$Path, [string]$Marker, [int]$Seconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-LogMarker -Path $Path -Marker $Marker) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Get-LogMarkerLine {
    param([string]$Path, [string]$Marker)
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $match = Select-String -LiteralPath $Path -SimpleMatch -Pattern $Marker | Select-Object -Last 1
    if ($match) { return $match.Line }
    return ''
}

$editor = Join-Path $EngineRoot 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
if (-not (Test-Path -LiteralPath $editor)) { throw "UnrealEditor-Cmd.exe not found: $editor" }
if (-not (Test-Path -LiteralPath $ProjectPath)) { throw "Project not found: $ProjectPath" }
if ($Port -lt 1024 -or $Port -gt 65535) { throw "Port must be between 1024 and 65535." }

$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$outputRoot = Join-Path $projectRoot "Saved\Integration\Phase17Reconnect\$runId"
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$serverLog = Join-Path $outputRoot 'server.log'
$clientLogs = 0..3 | ForEach-Object { Join-Path $outputRoot "client-$_.log" }
$resultPath = Join-Path $outputRoot 'result.json'
$processes = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$startedAt = [DateTime]::UtcNow
$passed = $false
$failure = ''

try {
    $serverArgs = @(
        $ProjectPath,
        '/Engine/Maps/Entry?listen',
        '-server',
        "-port=$Port",
        '-unattended', '-nop4', '-nosplash', '-nullrhi', '-nosound', '-Multiprocess',
        '-MahjongEnableIntegrationHooks',
        "-AbsLog=$serverLog"
    )
    $server = Start-Process -FilePath $editor -ArgumentList $serverArgs -PassThru -WindowStyle Hidden
    $processes.Add($server)

    if (-not (Wait-LogMarker -Path $serverLog -Marker "listening on port $Port" -Seconds 30)) {
        if ($server.HasExited) { throw "Dedicated server exited before listening. See $serverLog" }
        throw "Dedicated server did not listen on port $Port within 30 seconds. See $serverLog"
    }

    foreach ($index in 0..3) {
        $clientArgs = @(
            $ProjectPath,
            "127.0.0.1:$Port",
            '-game', '-unattended', '-nop4', '-nosplash', '-nullrhi', '-nosound', '-Multiprocess',
            '-windowed', '-ResX=640', '-ResY=360',
            '-MahjongEnableIntegrationHooks',
            "-MahjongIntegrationClient=$index",
            "-MahjongIntegrationServer=127.0.0.1:$Port",
            "-AbsLog=$($clientLogs[$index])"
        )
        $client = Start-Process -FilePath $editor -ArgumentList $clientArgs -PassThru -WindowStyle Hidden
        $processes.Add($client)
        Start-Sleep -Milliseconds 500
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $serverReconnectLine = Get-LogMarkerLine $serverLog 'MAHJONG_INTEGRATION_RECONNECT_OK'
        $clientReconnectLine = Get-LogMarkerLine $clientLogs[0] 'MAHJONG_INTEGRATION_CLIENT_RESTORED'
        $allClientsReceivedPrivateState = @(
            0..3 | Where-Object {
                Test-LogMarker $clientLogs[$_] "MAHJONG_INTEGRATION_PRIVATE_STATE Client=$_"
            }
        ).Count -eq 4
        $serverReady = (Test-LogMarker $serverLog 'MAHJONG_INTEGRATION_TABLE_STARTED') -and
            (Test-LogMarker $serverLog 'MAHJONG_INTEGRATION_DISCONNECT_TRIGGERED') -and
            ($serverReconnectLine -match 'Player=integration-client-0 .*Online=4 Hand=1[34] Round=[1-9]')
        $clientRestored = $clientReconnectLine -match 'Client=0 Seat=0 Hand=1[34] Round=[1-9]'
        if ($serverReady -and $clientRestored -and $allClientsReceivedPrivateState) {
            $passed = $true
            break
        }
        $unexpectedExit = $processes | Where-Object { $_.HasExited -and $_.ExitCode -ne 0 }
        if ($unexpectedExit) {
            $ids = ($unexpectedExit | ForEach-Object Id) -join ', '
            throw "Integration process exited unexpectedly: $ids"
        }
        Start-Sleep -Milliseconds 500
    }

    if (-not $passed) {
        throw "Reconnect integration markers were not completed within $TimeoutSeconds seconds."
    }
}
catch {
    $failure = $_.Exception.Message
}
finally {
    foreach ($process in $processes) {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
    $markers = [ordered]@{
        TableStarted = Test-LogMarker $serverLog 'MAHJONG_INTEGRATION_TABLE_STARTED'
        AllClientsReceivedPrivateState = @(
            0..3 | Where-Object {
                Test-LogMarker $clientLogs[$_] "MAHJONG_INTEGRATION_PRIVATE_STATE Client=$_"
            }
        ).Count -eq 4
        DisconnectTriggered = Test-LogMarker $serverLog 'MAHJONG_INTEGRATION_DISCONNECT_TRIGGERED'
        ServerReconnectOk = (Get-LogMarkerLine $serverLog 'MAHJONG_INTEGRATION_RECONNECT_OK') -match
            'Player=integration-client-0 .*Online=4 Hand=1[34] Round=[1-9]'
        ClientReconnectRestored = (Get-LogMarkerLine $clientLogs[0] 'MAHJONG_INTEGRATION_CLIENT_RESTORED') -match
            'Client=0 Seat=0 Hand=1[34] Round=[1-9]'
    }
    $result = [ordered]@{
        RunId = $runId
        Passed = $passed
        Failure = $failure
        Port = $Port
        DurationSeconds = [Math]::Round(([DateTime]::UtcNow - $startedAt).TotalSeconds, 2)
        Markers = $markers
        ServerReconnectLine = Get-LogMarkerLine $serverLog 'MAHJONG_INTEGRATION_RECONNECT_OK'
        ClientReconnectLine = Get-LogMarkerLine $clientLogs[0] 'MAHJONG_INTEGRATION_CLIENT_RESTORED'
        ServerLog = $serverLog
        ClientLogs = $clientLogs
    }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultPath -Encoding utf8
}

Get-Content -Raw -LiteralPath $resultPath
if (-not $passed) { exit 1 }
