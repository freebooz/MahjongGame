[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ClientExecutable,
    [ValidateRange(1, 4)]
    [int]$ClientCount = 4,
    [string]$ApiBaseUrl = 'http://127.0.0.1:18085',
    [string]$SessionRoot = '',
    [ValidateRange(10, 120)]
    [int]$ConnectTimeoutSeconds = 60,
    [ValidateRange(30, 300)]
    [int]$StableSeconds = 120,
    [switch]$KeepClientsOpen
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$client = (Resolve-Path -LiteralPath $ClientExecutable).Path
$packagedBinaryRoot = Join-Path (Split-Path -Parent $client) `
    'GuiyangMahjong\Binaries\Win64'
$packagedBinary = @(
    (Join-Path $packagedBinaryRoot 'GuiyangMahjongClient-Win64-Shipping.exe'),
    (Join-Path $packagedBinaryRoot 'GuiyangMahjongClient.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ($packagedBinary) {
    $client = (Resolve-Path -LiteralPath $packagedBinary).Path
}
if ([string]::IsNullOrWhiteSpace($SessionRoot)) {
    $SessionRoot = Join-Path $root ('Saved\ManualMatch\RealFourClient-{0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$SessionRoot = [IO.Path]::GetFullPath($SessionRoot)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $root 'Saved\ManualMatch'))
if (!$SessionRoot.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "SessionRoot must stay below $allowedRoot"
}
New-Item -ItemType Directory -Path $SessionRoot -Force | Out-Null

function New-RequestHeaders([string]$AccessToken, [switch]$Idempotent) {
    $requestId = [guid]::NewGuid().ToString()
    $headers = @{
        'X-Request-Id' = $requestId
        'X-Correlation-Id' = $requestId
        'X-Client-Version' = '1.0.0'
        'X-Protocol-Version' = '1'
        'X-Platform' = 'Windows'
        'X-Channel' = 'development'
    }
    if (![string]::IsNullOrWhiteSpace($AccessToken)) {
        $headers.Authorization = "Bearer $AccessToken"
    }
    if ($Idempotent) {
        $headers['Idempotency-Key'] = [guid]::NewGuid().ToString()
    }
    return $headers
}

function Invoke-JsonPost(
    [string]$Uri,
    [object]$Body,
    [hashtable]$Headers = @{}
) {
    try {
        # Windows PowerShell 5 直接发送字符串时会使用系统代码页；显式编码为 UTF-8，避免中文昵称变成问号。
        $jsonBytes = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 8 -Compress))
        Invoke-RestMethod -Method Post -Uri $Uri -Headers $Headers `
            -ContentType 'application/json; charset=utf-8' -Body $jsonBytes
    }
    catch {
        # 保留服务端业务错误正文，避免自动验收只能看到笼统的 HTTP 状态码。
        $responseBody = $_.ErrorDetails.Message
        if ([string]::IsNullOrWhiteSpace($responseBody) -and $_.Exception.Response) {
            # Windows PowerShell 5 不会自动填充 ErrorDetails，需从响应流读取业务错误正文。
            $responseStream = $_.Exception.Response.GetResponseStream()
            if ($responseStream) {
                $reader = [System.IO.StreamReader]::new($responseStream)
                try { $responseBody = $reader.ReadToEnd() }
                finally { $reader.Dispose() }
            }
        }
        if ([string]::IsNullOrWhiteSpace($responseBody)) {
            $responseBody = $_.Exception.Message
        }
        throw "POST $Uri failed: $responseBody"
    }
}

$players = @()
$processes = @()
try {
    for ($index = 0; $index -lt $ClientCount; ++$index) {
        $number = $index + 1
        $session = Invoke-JsonPost -Uri "$ApiBaseUrl/api/v1/auth/guest" `
            -Headers (New-RequestHeaders -AccessToken '') -Body @{
            installationId = "four-client-$([guid]::NewGuid())"
            displayName = "联机验收$number"
        }
        $players += [pscustomobject]@{
            Number = $number
            PlayerId = $session.playerId
            AccessToken = $session.accessToken
            Route = $null
        }
    }

    $owner = $players[0]
    $created = Invoke-JsonPost -Uri "$ApiBaseUrl/api/v1/rooms" `
        -Headers (New-RequestHeaders -AccessToken $owner.AccessToken -Idempotent) `
        -Body @{
            roundCount = 4
            publicRoom = $true
            autoStart = $true
            passwordProtected = $false
            password = $null
            # 玩法版本供 UE 规则快照解析，规则包版本则绑定 Join Ticket 与 Dedicated Server。
            ruleSnapshot = @{
                ruleId = 'GuiyangMainstreamV1'
                ruleVersion = 1
                ruleSetVersion = 'guiyang-zhuoji-v1'
            }
        }
    $roomCode = $created.roomCode
    if ([string]::IsNullOrWhiteSpace($roomCode)) {
        throw 'Lobby did not return a room code.'
    }

    $routeDeadline = [DateTimeOffset]::Now.AddSeconds($ConnectTimeoutSeconds)
    do {
        try {
            $owner.Route = Invoke-RestMethod -Method Get `
                -Uri "$ApiBaseUrl/api/v1/rooms/$roomCode/route" `
                -Headers (New-RequestHeaders -AccessToken $owner.AccessToken)
        } catch {
            if ([DateTimeOffset]::Now -ge $routeDeadline) { throw }
            Start-Sleep -Milliseconds 500
        }
    } while ($null -eq $owner.Route)

    for ($index = 1; $index -lt $players.Count; ++$index) {
        $player = $players[$index]
        $player.Route = Invoke-JsonPost -Uri "$ApiBaseUrl/api/v1/rooms/$roomCode/join" `
            -Headers (New-RequestHeaders -AccessToken $player.AccessToken -Idempotent) `
            -Body @{ password = $null; clientProtocolVersion = 1 }
    }

    $positions = @(@(0, 0), @(1920, 0), @(0, 1080), @(1920, 1080))
    foreach ($player in $players) {
        $userDirectory = Join-Path $SessionRoot "UserDir-$($player.Number)"
        $logPath = Join-Path $SessionRoot "Client-$($player.Number).log"
        New-Item -ItemType Directory -Path $userDirectory -Force | Out-Null
        $route = $player.Route
        $travelUrl = '{0}:{1}/Engine/Maps/Entry?PlayerId={2}?JoinTicket={3}' -f `
            $route.serverIp, $route.serverPort, $player.PlayerId, $route.joinTicket
        $position = $positions[$player.Number - 1]
        $arguments = @(
            $travelUrl,
            '-windowed',
            '-ResX=1920',
            '-ResY=1080',
            '-ForceRes',
            "-WinX=$($position[0])",
            "-WinY=$($position[1])",
            '-Multiprocess',
            "-UserDir=$userDirectory",
            '-log',
            "-AbsLog=$logPath"
        )
        $process = Start-Process -FilePath $client -ArgumentList $arguments -PassThru
        $processes += [pscustomobject]@{
            Number = $player.Number
            Process = $process
            LogPath = $logPath
        }
    }

    $failurePattern = 'NetChecksumMismatch|Connection failed|BroadcastNetworkFailure|Network Failure:|ConnectionLost|入场票据与玩家身份不匹配'
    $connectedPattern = 'UPendingNetGame::TravelCompleted Pending net game travel completed'
    # 房间场景生成是四端一致的可见性信号；HUD 背景日志受复制到达顺序影响，不能作为唯一条件。
    $roomReadyPattern = 'Spawned local room presentation: /Game/Client/Room/Presentation/BP_MahjongRoomPresentation'
    $deadline = [DateTimeOffset]::Now.AddSeconds($ConnectTimeoutSeconds)
    do {
        $allConnectedAndReady = $true
        foreach ($entry in $processes) {
            if ($entry.Process.HasExited) {
                throw "Client $($entry.Number) exited before joining the room."
            }
            if (!(Test-Path -LiteralPath $entry.LogPath)) {
                $allConnectedAndReady = $false
                continue
            }
            $text = Get-Content -LiteralPath $entry.LogPath -Raw
            if ($text -match $failurePattern) {
                throw "Client $($entry.Number) reported a network failure. See $($entry.LogPath)"
            }
            if ($text -notmatch $connectedPattern -or $text -notmatch $roomReadyPattern) {
                $allConnectedAndReady = $false
            }
        }
        if (!$allConnectedAndReady) { Start-Sleep -Milliseconds 500 }
    } while (!$allConnectedAndReady -and [DateTimeOffset]::Now -lt $deadline)
    if (!$allConnectedAndReady) {
        throw "Not all clients entered the visible Mahjong room within $ConnectTimeoutSeconds seconds."
    }

    Start-Sleep -Seconds $StableSeconds
    foreach ($entry in $processes) {
        $text = Get-Content -LiteralPath $entry.LogPath -Raw
        if ($entry.Process.HasExited -or $text -match $failurePattern) {
            throw "Client $($entry.Number) did not remain connected. See $($entry.LogPath)"
        }
    }

    $summary = [ordered]@{
        schemaVersion = 1
        roomCode = $roomCode
        server = "$($owner.Route.serverIp):$($owner.Route.serverPort)"
        serverInstanceId = $owner.Route.serverInstanceId
        clientCount = $processes.Count
        stableSeconds = $StableSeconds
        verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        clients = @($processes | ForEach-Object {
            [ordered]@{
                number = $_.Number
                processId = $_.Process.Id
                log = $_.LogPath
                travelCompleted = $true
                roomUiReady = $true
                networkFailure = $false
            }
        })
    }
    $summaryPath = Join-Path $SessionRoot 'verification-summary.json'
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    Write-Host "REAL_FOUR_CLIENT_ROOM_OK roomCode=$roomCode server=$($summary.server) clients=$($summary.clientCount) summary=$summaryPath"
} finally {
    if (!$KeepClientsOpen) {
        foreach ($entry in $processes) {
            if (!$entry.Process.HasExited) {
                Stop-Process -Id $entry.Process.Id -Force
            }
        }
    }
}
