<#
验证 Kubernetes/Agones 清单、Fleet、Allocation 和 GameServer SDK 基本契约。
默认只检查测试命名空间；任何可能影响集群状态的操作都必须限定资源名并保留诊断。
#>
[CmdletBinding()]
param(
    [string]$Namespace = 'guiyang-mahjong',
    [int]$AuthPort = 28082,
    [int]$LobbyPort = 28080,
    [int]$AllocatorPort = 28081,
    [int]$RouteTimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
$kubectl = (Get-Command kubectl -ErrorAction Stop).Source
$forwards = @()
$route = $null
$room = $null

function Read-SecretValue([string]$Name) {
    $json = kubectl get secret mahjong-secrets -n $Namespace -o json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Could not read mahjong-secrets.' }
    $encoded = $json.data.$Name
    if ([string]::IsNullOrWhiteSpace($encoded)) { throw "Secret key is missing: $Name" }
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encoded))
}

function Wait-Ready([string]$Uri) {
    $deadline = (Get-Date).AddSeconds(30)
    do {
        try { return Invoke-RestMethod -Uri $Uri -TimeoutSec 2 }
        catch { Start-Sleep -Milliseconds 500 }
    } until ((Get-Date) -gt $deadline)
    throw "Readiness timeout: $Uri"
}

try {
    $forwards += Start-Process -FilePath $kubectl -WindowStyle Hidden -PassThru `
        -ArgumentList "port-forward -n $Namespace service/mahjong-auth ${AuthPort}:8080"
    $forwards += Start-Process -FilePath $kubectl -WindowStyle Hidden -PassThru `
        -ArgumentList "port-forward -n $Namespace service/mahjong-lobby ${LobbyPort}:8080"
    $forwards += Start-Process -FilePath $kubectl -WindowStyle Hidden -PassThru `
        -ArgumentList "port-forward -n $Namespace service/mahjong-allocator ${AllocatorPort}:8080"

    $authHealth = Wait-Ready "http://127.0.0.1:$AuthPort/health/ready"
    $lobbyHealth = Wait-Ready "http://127.0.0.1:$LobbyPort/health/ready"
    $allocatorHealth = Wait-Ready "http://127.0.0.1:$AllocatorPort/health/ready"

    $session = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$AuthPort/v1/auth/guest" `
        -ContentType 'application/json' -Body (@{
            installationId = 'k8s-smoke-' + [Guid]::NewGuid().ToString('N')
            displayName = 'K8sSmoke'
        } | ConvertTo-Json)
    $headers = @{
        Authorization = "Bearer $($session.accessToken)"
        'X-Request-Id' = [Guid]::NewGuid().ToString()
        'Idempotency-Key' = 'k8s-smoke-' + [Guid]::NewGuid().ToString('N')
    }
    $room = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$LobbyPort/v1/rooms" `
        -Headers $headers -ContentType 'application/json' -Body (@{
            roundCount = 4
            publicRoom = $true
            autoStart = $true
            passwordProtected = $false
            ruleSnapshot = @{ ruleId = 'GuiyangMainstreamV1' }
        } | ConvertTo-Json -Depth 5)

    $deadline = (Get-Date).AddSeconds($RouteTimeoutSeconds)
    do {
        try {
            $route = Invoke-RestMethod `
                -Uri "http://127.0.0.1:$LobbyPort/v1/rooms/$($room.roomCode)/route" `
                -Headers @{
                    Authorization = "Bearer $($session.accessToken)"
                    'X-Request-Id' = [Guid]::NewGuid().ToString()
                } -TimeoutSec 3
        }
        catch { Start-Sleep -Seconds 1 }
    } until ($route -or (Get-Date) -gt $deadline)
    if (!$route) { throw 'Kubernetes room did not receive an Agones route.' }

    [ordered]@{
        auth = $authHealth.status
        lobby = $lobbyHealth.status
        allocator = $allocatorHealth.status
        allocatorBackend = $allocatorHealth.backend
        roomId = $room.roomId
        roomCode = $room.roomCode
        serverInstanceId = $route.serverInstanceId
        serverIp = $route.serverIp
        serverPort = $route.serverPort
        joinTicketPresent = ![string]::IsNullOrWhiteSpace($route.joinTicket)
    } | ConvertTo-Json

    $internalToken = Read-SecretValue 'Lobby__InternalServiceToken'
    Invoke-RestMethod -Method Post `
        -Uri "http://127.0.0.1:$LobbyPort/internal/gameservers/failure" `
        -Headers @{
            Authorization = "Bearer $internalToken"
            'X-Request-Id' = [Guid]::NewGuid().ToString()
        } -ContentType 'application/json' -Body (@{
            serverInstanceId = $route.serverInstanceId
            roomId = $room.roomId
            reason = 'Kubernetes deployment smoke cleanup'
        } | ConvertTo-Json) | Out-Null

    $allocatorToken = Read-SecretValue 'Allocator__ServiceToken'
    Invoke-RestMethod -Method Post `
        -Uri "http://127.0.0.1:$AllocatorPort/internal/instances/$($route.serverInstanceId)/drain" `
        -Headers @{
            Authorization = "Bearer $allocatorToken"
            'X-Request-Id' = [Guid]::NewGuid().ToString()
        } | Out-Null
    Write-Host 'KUBERNETES_AGONES_SMOKE_OK cleanup=complete'
}
finally {
    foreach ($process in $forwards) {
        if ($process -and !$process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}
