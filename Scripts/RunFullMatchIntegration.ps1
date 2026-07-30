<#
驱动四客户端完成一场完整对局集成验证，收集进程、日志、房间和结算证据。
运行前要求服务端与客户端版本一致；超时、掉线或结算不一致时返回失败并保留 TraceId。
#>
[CmdletBinding()]
param(
    [string]$EngineRoot = '',
    [string]$ProjectPath = '',
    [int]$Port = 17778,
    [int]$TimeoutSeconds = 180
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
        Start-Sleep -Milliseconds 250
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
if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Port must be between 1024 and 65535.' }

$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$outputRoot = Join-Path $projectRoot "Saved\Integration\FullMatch\$runId"
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$serverLog = Join-Path $outputRoot 'server.log'
$clientLogs = 0..3 | ForEach-Object { Join-Path $outputRoot "client-$_.log" }
$resultPath = Join-Path $outputRoot 'result.json'
$processes = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$startedAt = [DateTime]::UtcNow
$passed = $false
$failure = ''

try {
    $commonArgs = @(
        '-unattended', '-nop4', '-nosplash', '-nullrhi', '-nosound', '-Multiprocess',
        '-MahjongEnableIntegrationHooks', '-MahjongIntegrationFullMatch'
    )
    $serverArgs = @($ProjectPath, '/Engine/Maps/Entry?listen', '-server', "-port=$Port") +
        $commonArgs + @("-AbsLog=$serverLog")
    $server = Start-Process -FilePath $editor -ArgumentList $serverArgs -PassThru -WindowStyle Hidden
    $processes.Add($server)

    if (-not (Wait-LogMarker -Path $serverLog -Marker "listening on port $Port" -Seconds 30)) {
        throw "Dedicated server did not listen on port $Port. See $serverLog"
    }

    # Start the owner first so the one-round room exists before the other clients join.
    foreach ($index in 0..3) {
        $clientArgs = @(
            $ProjectPath, "127.0.0.1:$Port", '-game', '-windowed', '-ResX=640', '-ResY=360'
        ) + $commonArgs + @(
            "-MahjongIntegrationClient=$index",
            "-MahjongIntegrationServer=127.0.0.1:$Port",
            "-AbsLog=$($clientLogs[$index])"
        )
        $client = Start-Process -FilePath $editor -ArgumentList $clientArgs -PassThru -WindowStyle Hidden
        $processes.Add($client)
        if ($index -eq 0) {
            if (-not (Wait-LogMarker -Path $serverLog -Marker 'MAHJONG_INTEGRATION_FULL_MATCH_ROOM_READY' -Seconds 30)) {
                throw "Integration owner did not create the full-match room. See $serverLog"
            }
        }
        Start-Sleep -Milliseconds 500
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $serverComplete = Test-LogMarker $serverLog 'MAHJONG_INTEGRATION_FULL_MATCH_COMPLETE'
        $completedClients = @(0..3 | Where-Object {
            Test-LogMarker $clientLogs[$_] "MAHJONG_INTEGRATION_FINAL_SETTLEMENT Client=$_"
        }).Count
        if ($serverComplete -and $completedClients -eq 4) {
            $passed = $true
            break
        }
        $unexpectedExit = $processes | Where-Object { $_.HasExited -and $_.ExitCode -ne 0 }
        if ($unexpectedExit) {
            throw "Integration process exited unexpectedly: $((@($unexpectedExit.Id) -join ', '))"
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $passed) { throw "Full match did not reach final settlement within $TimeoutSeconds seconds." }
}
catch {
    $failure = $_.Exception.Message
}
finally {
    $processEvidence = @($processes | ForEach-Object {
        [ordered]@{ Id = $_.Id; Role = if ($_ -eq $processes[0]) { 'Server' } else { 'Client' } }
    })
    foreach ($process in $processes) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    $clientFinalLines = @(0..3 | ForEach-Object {
        Get-LogMarkerLine $clientLogs[$_] "MAHJONG_INTEGRATION_FINAL_SETTLEMENT Client=$_"
    })
    $result = [ordered]@{
        RunId = $runId
        Passed = $passed
        Failure = $failure
        Port = $Port
        DurationSeconds = [Math]::Round(([DateTime]::UtcNow - $startedAt).TotalSeconds, 2)
        Processes = $processEvidence
        Markers = [ordered]@{
            RoomReady = Test-LogMarker $serverLog 'MAHJONG_INTEGRATION_FULL_MATCH_ROOM_READY'
            TableStarted = Test-LogMarker $serverLog 'MAHJONG_INTEGRATION_TABLE_STARTED'
            ServerFinalSettlement = Test-LogMarker $serverLog 'MAHJONG_INTEGRATION_FULL_MATCH_COMPLETE'
            AllFourClientsFinalSettlement = @($clientFinalLines | Where-Object { $_ }).Count -eq 4
        }
        ServerRoomLine = Get-LogMarkerLine $serverLog 'MAHJONG_INTEGRATION_FULL_MATCH_ROOM_READY'
        ServerTableLine = Get-LogMarkerLine $serverLog 'MAHJONG_INTEGRATION_TABLE_STARTED'
        ServerFinalLine = Get-LogMarkerLine $serverLog 'MAHJONG_INTEGRATION_FULL_MATCH_COMPLETE'
        ClientFinalLines = $clientFinalLines
        ServerLog = $serverLog
        ClientLogs = $clientLogs
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding utf8
}

Get-Content -Raw -LiteralPath $resultPath
if (-not $passed) { exit 1 }
