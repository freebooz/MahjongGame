param(
    [int]$LobbyPort = 28080,
    [int]$AllocatorPort = 28081,
    [int]$GameServerPortStart = 29000,
    [string]$GameServerExecutablePath = '',
    [string[]]$GameServerPrefixArguments = @(),
    [int]$RegistrationTimeoutSeconds = 10,
    [int]$HeartbeatTimeoutSeconds = 3,
    [int]$HealthWaitAttempts = 120,
    [int]$RouteWaitAttempts = 40,
    [int]$FailureWaitAttempts = 40,
    [switch]$VerifySettlementOutboxRecovery
)

# 本机首次加载 .NET 运行时或系统处于高负载时，服务启动可能超过原来的 10 秒；
# 健康检查参数只扩大测试服务的启动等待，不改变任何业务请求超时。
$ErrorActionPreference = 'Stop'
$lobby = $null
$allocator = $null
$lobbySuspended = $false
$serviceToken = 'integration-allocator-service-token-which-is-long-enough'
$lobbyToken = 'integration-lobby-callback-token-which-is-long-enough'
$playerKey = 'integration-player-signing-key-which-is-long-enough'
$joinKey = 'integration-join-ticket-key-which-is-long-enough'
$root = Split-Path -Parent $PSScriptRoot
$lobbyDll = Join-Path $root 'Services/GuiyangMahjong.Lobby/bin/Release/net10.0/GuiyangMahjong.Lobby.dll'
$allocatorDll = Join-Path $root 'Services/GuiyangMahjong.Allocator/bin/Release/net10.0/GuiyangMahjong.Allocator.dll'
$outboxDirectory = Join-Path $root "Saved/Automation/Phase6SettlementOutbox-$PID"
if ([string]::IsNullOrWhiteSpace($GameServerExecutablePath) -or
    -not (Test-Path -LiteralPath $GameServerExecutablePath)) {
    throw "A real GameServer executable is required: $GameServerExecutablePath"
}

function Wait-Health([string]$Url) {
    # 每 250 毫秒探测一次，达到上限后失败关闭，避免后续步骤连接到未就绪服务。
    for ($attempt = 0; $attempt -lt $HealthWaitAttempts; $attempt++) {
        try {
            $null = Invoke-RestMethod -Uri $Url -TimeoutSec 1
            return
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }
    throw "Health check failed: $Url"
}

function ConvertTo-Base64Url([byte[]]$Bytes) {
    [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function New-Headers([string]$Bearer, [string]$Idempotency = '') {
    $headers = @{
        'X-Request-Id' = [Guid]::NewGuid().ToString()
        Authorization = "Bearer $Bearer"
    }
    if ($Idempotency) { $headers['Idempotency-Key'] = $Idempotency }
    $headers
}

function ConvertTo-Items([object]$Response) {
    if ($null -ne $Response.PSObject.Properties['value']) {
        @($Response.value)
        return
    }
    @($Response)
}

function Wait-Route([object]$Room, [string]$PlayerToken) {
    for ($attempt = 0; $attempt -lt $RouteWaitAttempts; $attempt++) {
        try {
            return Invoke-RestMethod `
                -Uri "http://127.0.0.1:$LobbyPort/v1/rooms/$($Room.roomCode)/route" `
                -Headers (New-Headers $PlayerToken)
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }
    throw "Route was not published for room $($Room.roomId)"
}

function New-CreateRoomBody([int]$BaseScore, [int]$TurnTimeoutSeconds) {
    @{
        roundCount = 4
        publicRoom = $true
        autoStart = $true
        passwordProtected = $false
        password = $null
        ruleSnapshot = @{
            ruleId = 'GuiyangMainstreamV1'
            baseScore = $BaseScore
            turnTimeoutSeconds = $TurnTimeoutSeconds
        }
    } | ConvertTo-Json -Depth 5
}

function Wait-AuthoritativeHeartbeats([string[]]$ServerInstanceIds) {
    for ($attempt = 0; $attempt -lt $RouteWaitAttempts; $attempt++) {
        $response = Invoke-RestMethod `
            -Uri "http://127.0.0.1:$AllocatorPort/internal/instances" `
            -Headers (New-Headers $serviceToken)
        $instances = @(ConvertTo-Items $response)
        $matching = @($instances | Where-Object { $ServerInstanceIds -contains $_.serverInstanceId })
        $heartbeating = @($matching | Where-Object {
            $_.registeredAtUtc -and $_.lastHeartbeatAtUtc -and
            [DateTimeOffset]$_.lastHeartbeatAtUtc -gt [DateTimeOffset]$_.registeredAtUtc
        })
        if ($matching.Count -eq $ServerInstanceIds.Count -and $heartbeating.Count -eq $ServerInstanceIds.Count) {
            return $matching
        }
        Start-Sleep -Milliseconds 250
    }
    throw 'Not every GameServer produced a heartbeat after applying its authoritative room bootstrap.'
}

try {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $lobbyArguments = @(
        $lobbyDll,
        "--urls=http://127.0.0.1:$LobbyPort",
        "--Lobby:TokenSigningKey=$playerKey",
        "--Lobby:JoinTicketSigningKey=$joinKey",
        "--Lobby:InternalServiceToken=$lobbyToken",
        '--Lobby:Allocator:Enabled=true',
        "--Lobby:Allocator:BaseUrl=http://127.0.0.1:$AllocatorPort",
        "--Lobby:Allocator:ServiceToken=$serviceToken",
        '--Lobby:Persistence:Mode=InMemory'
    )
    $lobby = Start-Process -FilePath 'dotnet' -ArgumentList $lobbyArguments -WindowStyle Hidden -PassThru
    # Lobby 的就绪状态依赖 Allocator；先等待存活探针，再启动 Allocator，避免形成启动循环等待。
    Wait-Health "http://127.0.0.1:$LobbyPort/health/live"

    $allocatorArguments = @(
        $allocatorDll,
        "--urls=http://127.0.0.1:$AllocatorPort",
        "--Allocator:PortStart=$GameServerPortStart",
        "--Allocator:PortEnd=$($GameServerPortStart + 2)",
        "--Allocator:HeartbeatTimeoutSeconds=$HeartbeatTimeoutSeconds",
        '--Allocator:HeartbeatIntervalSeconds=1',
        '--Allocator:MonitorIntervalMilliseconds=200',
        "--Allocator:RegistrationTimeoutSeconds=$RegistrationTimeoutSeconds",
        '--Allocator:DrainGraceSeconds=0',
        '--Allocator:AdvertisedIp=127.0.0.1',
        "--Allocator:GameServerExecutablePath=$GameServerExecutablePath",
        "--Allocator:MatchResultOutboxDirectory=$outboxDirectory",
        '--Allocator:MatchResultRecoveryDelaySeconds=1',
        "--Allocator:LobbyInternalUrl=http://127.0.0.1:$LobbyPort",
        "--Allocator:ServiceToken=$serviceToken",
        "--Allocator:LobbyCallbackToken=$lobbyToken",
        "--Allocator:JoinTicketSigningKey=$joinKey"
    )
    for ($index = 0; $index -lt $GameServerPrefixArguments.Count; $index++) {
        $allocatorArguments += "--Allocator:GameServerPrefixArguments:$index=$($GameServerPrefixArguments[$index])"
    }
    $allocator = Start-Process -FilePath 'dotnet' -ArgumentList $allocatorArguments -WindowStyle Hidden -PassThru
    Wait-Health "http://127.0.0.1:$AllocatorPort/health/ready"
    # Allocator 就绪后再验证 Lobby 的完整依赖链，确保后续房间请求不会落到半就绪环境。
    Wait-Health "http://127.0.0.1:$LobbyPort/health/ready"

    function New-PlayerToken([string]$PlayerId) {
        $tokenPayloadJson = @{
            Sub = $PlayerId
            Name = $PlayerId
            Provider = 'Test'
            Exp = [DateTimeOffset]::UtcNow.AddMinutes(5).ToUnixTimeSeconds()
        } | ConvertTo-Json -Compress
        $tokenPayload = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($tokenPayloadJson))
        $hmac = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($playerKey))
        try {
            $tokenSignature = ConvertTo-Base64Url (
                $hmac.ComputeHash([Text.Encoding]::ASCII.GetBytes($tokenPayload)))
        }
        finally {
            $hmac.Dispose()
        }
        "$tokenPayload.$tokenSignature"
    }
    $playerTokens = @(
        (New-PlayerToken 'integration-owner-0'),
        (New-PlayerToken 'integration-owner-1')
    )
    $rooms = @()
    for ($index = 0; $index -lt 2; $index++) {
        $createBody = if ($index -eq 0) { New-CreateRoomBody 2 17 } else { New-CreateRoomBody 5 23 }
        $rooms += Invoke-RestMethod `
            -Method Post `
            -Uri "http://127.0.0.1:$LobbyPort/v1/rooms" `
            -Headers (New-Headers $playerTokens[$index] "integration-create-room-$index-0001") `
            -ContentType 'application/json' `
            -Body $createBody
    }
    $routes = @()
    for ($index = 0; $index -lt $rooms.Count; $index++) {
        $routes += Wait-Route $rooms[$index] $playerTokens[$index]
    }

    $null = Wait-AuthoritativeHeartbeats @($routes.serverInstanceId)

    $instancesResponse = Invoke-RestMethod `
        -Uri "http://127.0.0.1:$AllocatorPort/internal/instances" `
        -Headers (New-Headers $serviceToken)
    $instancesBefore = @(ConvertTo-Items $instancesResponse)
    if (@($instancesBefore | Where-Object state -eq 'Allocated').Count -ne 2) {
        throw "Expected two allocated instances: $($instancesBefore | ConvertTo-Json -Compress)"
    }
    if (@($instancesBefore.port | Select-Object -Unique).Count -ne 2) {
        throw 'Expected unique ports.'
    }
    if (@($routes.joinTicket | Where-Object { -not $_ }).Count -ne 0) {
        throw 'Expected non-empty join tickets.'
    }

    $victim = $instancesBefore | Sort-Object port | Select-Object -First 1
    Stop-Process -Id $victim.processId -Force
    $failed = $null
    for ($attempt = 0; $attempt -lt $FailureWaitAttempts -and $null -eq $failed; $attempt++) {
        Start-Sleep -Milliseconds 250
        $currentResponse = Invoke-RestMethod `
            -Uri "http://127.0.0.1:$AllocatorPort/internal/instances" `
            -Headers (New-Headers $serviceToken)
        $current = @(ConvertTo-Items $currentResponse)
        $failed = $current | Where-Object {
            $_.serverInstanceId -eq $victim.serverInstanceId -and $_.state -eq 'Failed'
        }
    }
    if ($null -eq $failed) { throw 'Crashed instance was not marked failed.' }

    $outboxRecovered = $false
    if ($VerifySettlementOutboxRecovery) {
        $victimRoute = $routes | Where-Object serverInstanceId -eq $victim.serverInstanceId
        if ($null -eq $victimRoute) { throw 'Victim route was not found for outbox recovery.' }
        Start-Sleep -Milliseconds 500
        if (-not ('NativeProcessControl' -as [type])) {
            Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NativeProcessControl
{
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr processHandle);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr processHandle);
}
'@
        }
        if ([NativeProcessControl]::NtSuspendProcess($lobby.Handle) -ne 0) {
            throw 'Lobby process could not be suspended for the outage gate.'
        }
        $lobbySuspended = $true
        [IO.Directory]::CreateDirectory($outboxDirectory) | Out-Null
        $outboxPath = Join-Path $outboxDirectory "$($victim.serverInstanceId).json"
        $outbox = @{
            version = 1
            matchId = $victimRoute.matchId
            report = @{
                roomId = $victimRoute.roomId
                serverInstanceId = $victim.serverInstanceId
                resultSequence = 1
                completedRounds = 4
                players = @(@{
                    playerId = $victimRoute.playerId
                    seatIndex = 0
                    rank = 1
                    totalScore = 16
                })
            }
        } | ConvertTo-Json -Depth 6 -Compress
        [IO.File]::WriteAllText($outboxPath, $outbox, [Text.UTF8Encoding]::new($false))
        Start-Sleep -Milliseconds 1600
        if (-not (Test-Path -LiteralPath $outboxPath)) {
            throw 'Outbox must remain durable while Lobby is unavailable.'
        }
        if ([NativeProcessControl]::NtResumeProcess($lobby.Handle) -ne 0) {
            throw 'Lobby process could not be resumed after the outage gate.'
        }
        $lobbySuspended = $false
        for ($attempt = 0; $attempt -lt 80 -and (Test-Path -LiteralPath $outboxPath); $attempt++) {
            Start-Sleep -Milliseconds 250
        }
        if (Test-Path -LiteralPath $outboxPath) {
            throw 'Allocator did not replay and remove the durable settlement outbox.'
        }
        $outboxRecovered = $true
    }

    $thirdPlayerToken = New-PlayerToken 'integration-owner-2'
    $thirdRoom = Invoke-RestMethod `
        -Method Post `
        -Uri "http://127.0.0.1:$LobbyPort/v1/rooms" `
        -Headers (New-Headers $thirdPlayerToken 'integration-create-room-third-0001') `
        -ContentType 'application/json' `
        -Body (New-CreateRoomBody 7 29)
    $thirdRoute = Wait-Route $thirdRoom $thirdPlayerToken
    if ($thirdRoute.serverPort -ne $victim.port) {
        throw "Expected reclaimed port $($victim.port), got $($thirdRoute.serverPort)."
    }

    [pscustomobject]@{
        Status = 'INTEGRATION_OK'
        RoomsCreated = 3
        InitialInstances = 2
        HeartbeatingInstances = 2
        UniqueInitialPorts = 2
        FailedInstance = $victim.serverInstanceId
        SettlementOutboxRecovered = $outboxRecovered
        ReclaimedPort = $thirdRoute.serverPort
        GameServerExecutable = $GameServerExecutablePath
    } | ConvertTo-Json -Compress
}
finally {
    if ($lobbySuspended -and $lobby -and -not $lobby.HasExited) {
        [NativeProcessControl]::NtResumeProcess($lobby.Handle) | Out-Null
        $lobbySuspended = $false
    }
    if ($allocator -and -not $allocator.HasExited) {
        try {
            $remainingResponse = Invoke-RestMethod `
                -Uri "http://127.0.0.1:$AllocatorPort/internal/instances" `
                -Headers (New-Headers $serviceToken) `
                -TimeoutSec 2
            $remaining = @(ConvertTo-Items $remainingResponse)
            foreach ($instance in $remaining) {
                if ($instance.processId) {
                    Stop-Process -Id $instance.processId -Force -ErrorAction SilentlyContinue
                }
            }
        }
        catch {
        }
        Stop-Process -Id $allocator.Id -Force -ErrorAction SilentlyContinue
    }
    if ($lobby -and -not $lobby.HasExited) {
        Stop-Process -Id $lobby.Id -Force -ErrorAction SilentlyContinue
    }
}
