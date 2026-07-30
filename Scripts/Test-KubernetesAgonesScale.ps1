<#
验证 Agones Fleet 扩缩容、实例注册和容量回收，不用于直接调整生产副本数。
测试必须设置容量上限和超时，结束后恢复测试命名空间原始规模。
#>
[CmdletBinding()]
param(
    [ValidateRange(4, 2000)]
    [int]$ClientCount = 200,
    [ValidateRange(1, 50)]
    [int]$GameRoomCount = 8,
    [ValidateRange(1, 64)]
    [int]$ClientConcurrency = 32,
    [ValidateRange(1, 20)]
    [int]$AuthLoadReplicas = 7,
    [string]$Namespace = 'guiyang-mahjong',
    [int]$AuthBasePort = 38100,
    [int]$LobbyPort = 38080,
    [int]$AllocatorPort = 38081,
    [int]$ScaleTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
if ($ClientCount -lt ($GameRoomCount * 4)) {
    throw 'ClientCount must be at least four times GameRoomCount.'
}

$kubectl = (Get-Command kubectl -ErrorAction Stop).Source
$forwards = @()
$rooms = [System.Collections.Generic.List[object]]::new()
$clients = @()
$runId = [Guid]::NewGuid().ToString('N')
$maximumDesired = 0
$maximumCurrent = 0
$maximumAllocated = 0
$originalAuthReplicas = $null

function Wait-HttpReady([string]$Uri) {
    $deadline = (Get-Date).AddSeconds(45)
    do {
        try { return Invoke-RestMethod -Uri $Uri -TimeoutSec 2 }
        catch { Start-Sleep -Milliseconds 500 }
    } until ((Get-Date) -gt $deadline)
    throw "Readiness timeout: $Uri"
}

function Get-FleetSnapshot {
    $fleet = kubectl get fleet guiyang-mahjong -n $Namespace -o json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read Agones Fleet state.' }
    [pscustomobject]@{
        Time = Get-Date
        Desired = [int]$fleet.spec.replicas
        Current = [int]$fleet.status.replicas
        Ready = [int]$fleet.status.readyReplicas
        Allocated = [int]$fleet.status.allocatedReplicas
    }
}

function Record-FleetSnapshot {
    $snapshot = Get-FleetSnapshot
    $script:maximumDesired = [Math]::Max($script:maximumDesired, $snapshot.Desired)
    $script:maximumCurrent = [Math]::Max($script:maximumCurrent, $snapshot.Current)
    $script:maximumAllocated = [Math]::Max($script:maximumAllocated, $snapshot.Allocated)
    $snapshot
}

function Wait-FleetReadySlot {
    $deadline = (Get-Date).AddSeconds($ScaleTimeoutSeconds)
    do {
        $snapshot = Record-FleetSnapshot
        if ($snapshot.Ready -gt 0) { return $snapshot }
        Start-Sleep -Seconds 2
    } until ((Get-Date) -gt $deadline)
    throw 'Timed out waiting for an Agones Ready GameServer.'
}

function New-LoadClients {
    $pool = [RunspaceFactory]::CreateRunspacePool(1, $ClientConcurrency)
    $pool.Open()
    $jobs = [System.Collections.Generic.List[object]]::new()
    try {
        foreach ($index in 0..($ClientCount - 1)) {
            $pipeline = [PowerShell]::Create()
            $pipeline.RunspacePool = $pool
            [void]$pipeline.AddScript({
                param($Index, $RunId, $AuthUris, $LobbyUri)
                $ErrorActionPreference = 'Stop'
                $AuthUri = $AuthUris[$Index % $AuthUris.Count]
                $login = Invoke-RestMethod -Method Post -Uri "$AuthUri/v1/auth/guest" `
                    -ContentType 'application/json' -Body (@{
                        installationId = "agones-scale-$RunId-$Index"
                        displayName = "ScaleClient$Index"
                    } | ConvertTo-Json)
                $headers = @{
                    Authorization = "Bearer $($login.accessToken)"
                    'X-Request-Id' = [Guid]::NewGuid().ToString()
                }
                $bootstrap = Invoke-RestMethod -Uri "$LobbyUri/v1/lobby/bootstrap" `
                    -Headers $headers -TimeoutSec 10
                [pscustomobject]@{
                    Index = $Index
                    PlayerId = $login.playerId
                    AccessToken = $login.accessToken
                    OnlineCount = [int]$bootstrap.onlinePlayerCount
                }
            }).AddArgument($index).AddArgument($runId).AddArgument(
                $authUris).AddArgument("http://127.0.0.1:$LobbyPort")
            $jobs.Add([pscustomobject]@{
                Pipeline = $pipeline
                Async = $pipeline.BeginInvoke()
            })
        }

        $results = [System.Collections.Generic.List[object]]::new()
        $failures = [System.Collections.Generic.List[string]]::new()
        foreach ($job in $jobs) {
            try {
                foreach ($item in $job.Pipeline.EndInvoke($job.Async)) { $results.Add($item) }
                foreach ($errorRecord in $job.Pipeline.Streams.Error) {
                    $failures.Add($errorRecord.ToString())
                }
            }
            catch { $failures.Add($_.Exception.Message) }
            finally { $job.Pipeline.Dispose() }
        }
        if ($failures.Count -gt 0) {
            throw "Client bootstrap failures: $($failures.Count). First: $($failures[0])"
        }
        $results | Sort-Object Index
    }
    finally {
        foreach ($job in $jobs) { if ($job.Pipeline) { $job.Pipeline.Dispose() } }
        $pool.Close()
        $pool.Dispose()
    }
}

function Get-ClientHeaders([object]$Client, [bool]$Idempotent) {
    $headers = @{
        Authorization = "Bearer $($Client.AccessToken)"
        'X-Request-Id' = [Guid]::NewGuid().ToString()
    }
    if ($Idempotent) { $headers['Idempotency-Key'] = [Guid]::NewGuid().ToString() }
    $headers
}

try {
    $originalAuthReplicas = [int](kubectl get deployment mahjong-auth -n $Namespace `
        -o jsonpath='{.spec.replicas}')
    kubectl scale deployment mahjong-auth -n $Namespace --replicas=$AuthLoadReplicas | Out-Null
    kubectl rollout status deployment/mahjong-auth -n $Namespace --timeout=120s | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Auth load replicas failed to become ready.' }

    $authPods = @(kubectl get pods -n $Namespace -l app=mahjong-auth -o name |
        ForEach-Object { $_ -replace '^pod/', '' })
    if ($authPods.Count -lt $AuthLoadReplicas) {
        throw "Expected $AuthLoadReplicas Auth pods, found $($authPods.Count)."
    }
    $authUris = [System.Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt $AuthLoadReplicas; $index++) {
        $port = $AuthBasePort + $index
        $authUris.Add("http://127.0.0.1:$port")
        $forwards += Start-Process -FilePath $kubectl -WindowStyle Hidden -PassThru `
            -ArgumentList "port-forward -n $Namespace pod/$($authPods[$index]) ${port}:8080"
    }
    $forwards += Start-Process -FilePath $kubectl -WindowStyle Hidden -PassThru `
        -ArgumentList "port-forward -n $Namespace service/mahjong-lobby ${LobbyPort}:8080"
    $forwards += Start-Process -FilePath $kubectl -WindowStyle Hidden -PassThru `
        -ArgumentList "port-forward -n $Namespace service/mahjong-allocator ${AllocatorPort}:8080"

    foreach ($authUri in $authUris) { $authHealth = Wait-HttpReady "$authUri/health/ready" }
    $lobbyHealth = Wait-HttpReady "http://127.0.0.1:$LobbyPort/health/ready"
    $allocatorHealth = Wait-HttpReady "http://127.0.0.1:$AllocatorPort/health/ready"
    $initialFleet = Record-FleetSnapshot
    $autoscaler = kubectl get fleetautoscaler guiyang-mahjong-buffer -n $Namespace -o json |
        ConvertFrom-Json
    $bufferSize = [int]$autoscaler.spec.policy.buffer.bufferSize
    $maxReplicas = [int]$autoscaler.spec.policy.buffer.maxReplicas
    $expectedScaledDesired = [Math]::Min($maxReplicas, $GameRoomCount + $bufferSize)

    $clientStarted = Get-Date
    $clients = @(New-LoadClients)
    $clientDuration = (Get-Date) - $clientStarted
    if ($clients.Count -ne $ClientCount) {
        throw "Expected $ClientCount clients but bootstrapped $($clients.Count)."
    }

    for ($roomIndex = 0; $roomIndex -lt $GameRoomCount; $roomIndex++) {
        $null = Wait-FleetReadySlot
        $owner = $clients[$roomIndex * 4]
        $room = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$LobbyPort/v1/rooms" `
            -Headers (Get-ClientHeaders $owner $true) -ContentType 'application/json' -Body (@{
                roundCount = 4
                publicRoom = $true
                autoStart = $true
                passwordProtected = $false
                ruleSnapshot = @{ ruleId = 'GuiyangMainstreamV1' }
            } | ConvertTo-Json -Depth 5)

        $route = $null
        $routeDeadline = (Get-Date).AddSeconds(60)
        do {
            try {
                $route = Invoke-RestMethod -Uri `
                    "http://127.0.0.1:$LobbyPort/v1/rooms/$($room.roomCode)/route" `
                    -Headers (Get-ClientHeaders $owner $false) -TimeoutSec 3
            }
            catch { Start-Sleep -Milliseconds 500 }
        } until ($route -or (Get-Date) -gt $routeDeadline)
        if (!$route) { throw "Room $($room.roomCode) did not receive an Agones route." }

        foreach ($seatOffset in 1..3) {
            $client = $clients[($roomIndex * 4) + $seatOffset]
            $null = Invoke-RestMethod -Method Post `
                -Uri "http://127.0.0.1:$LobbyPort/v1/rooms/$($room.roomCode)/join" `
                -Headers (Get-ClientHeaders $client $true) -ContentType 'application/json' `
                -Body (@{ password = $null; clientProtocolVersion = 1 } | ConvertTo-Json)
        }
        $rooms.Add([pscustomobject]@{ Room = $room; Route = $route; Owner = $owner })
        $null = Record-FleetSnapshot
    }

    $scaleDeadline = (Get-Date).AddSeconds($ScaleTimeoutSeconds)
    do {
        $scaledFleet = Record-FleetSnapshot
        if ($scaledFleet.Allocated -ge $GameRoomCount -and
            $scaledFleet.Desired -ge $expectedScaledDesired -and
            $scaledFleet.Current -ge $expectedScaledDesired) { break }
        Start-Sleep -Seconds 2
    } until ((Get-Date) -gt $scaleDeadline)

    [ordered]@{
        result = 'KUBERNETES_AGONES_SCALE_OK'
        requestedClients = $ClientCount
        authenticatedClients = $clients.Count
        observedOnlinePeak = ($clients | Measure-Object OnlineCount -Maximum).Maximum
        clientBootstrapSeconds = [Math]::Round($clientDuration.TotalSeconds, 2)
        requestedRooms = $GameRoomCount
        allocatedRooms = $rooms.Count
        gameClients = $rooms.Count * 4
        lobbyOnlyClients = $clients.Count - ($rooms.Count * 4)
        allocatorBackend = $allocatorHealth.backend
        initialFleetDesired = $initialFleet.Desired
        maximumFleetDesired = $maximumDesired
        maximumFleetCurrent = $maximumCurrent
        maximumFleetAllocated = $maximumAllocated
        expectedScaledDesired = $expectedScaledDesired
    } | ConvertTo-Json
}
finally {
    foreach ($entry in $rooms) {
        try {
            Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$LobbyPort/v1/rooms/current/close" `
                -Headers (Get-ClientHeaders $entry.Owner $true) -ContentType 'application/json' `
                -Body '{}' -TimeoutSec 5 | Out-Null
        }
        catch { Write-Warning "Room cleanup failed: $($entry.Room.roomCode): $($_.Exception.Message)" }
    }
    if ($rooms.Count -gt 0) {
        $cleanupDeadline = (Get-Date).AddSeconds(180)
        do {
            try {
                $cleanupFleet = Record-FleetSnapshot
                if ($cleanupFleet.Allocated -eq 0 -and
                    $cleanupFleet.Desired -le $initialFleet.Desired -and
                    $cleanupFleet.Ready -ge $initialFleet.Ready) { break }
            }
            catch { Write-Warning "Fleet cleanup observation failed: $($_.Exception.Message)"; break }
            Start-Sleep -Seconds 3
        } until ((Get-Date) -gt $cleanupDeadline)
    }
    foreach ($process in $forwards) {
        if ($process -and !$process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
    if ($null -ne $originalAuthReplicas) {
        kubectl scale deployment mahjong-auth -n $Namespace --replicas=$originalAuthReplicas | Out-Null
    }
}
