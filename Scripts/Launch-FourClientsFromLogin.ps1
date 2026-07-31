<#
从登录流程启动四个独立客户端用于人工联机验证，并隔离窗口参数与玩家会话。
调用前要求客户端已成功编译；脚本不创建正式账号，也不得把测试令牌写入命令日志。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ClientExecutable,
    [string]$ApiBaseUrl = 'http://127.0.0.1:18085',
    [string]$RealtimeBaseUrl = '',
    [string]$PatchBaseUrl = '',
    [string]$SessionRoot = '',
    [ValidateRange(10, 60)]
    [int]$LoginScreenTimeoutSeconds = 30
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
    $SessionRoot = Join-Path $root (
        'Saved\ManualMatch\LoginFirst-{0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$SessionRoot = [IO.Path]::GetFullPath($SessionRoot)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $root 'Saved\ManualMatch'))
if (!$SessionRoot.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "SessionRoot must stay below $allowedRoot"
}
New-Item -ItemType Directory -Path $SessionRoot -Force | Out-Null

function Test-LoopbackHttpUrl([string]$Value) {
    $uri = $null
    if (![Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri)) { return $false }
    return ($uri.Scheme -eq 'http' -and
        $uri.Host -in @('localhost', '127.0.0.1', '::1'))
}

if ([string]::IsNullOrWhiteSpace($RealtimeBaseUrl)) {
    $RealtimeBaseUrl = $ApiBaseUrl
}
foreach ($endpoint in @($ApiBaseUrl, $RealtimeBaseUrl) + @($PatchBaseUrl | Where-Object { $_ })) {
    $uri = $null
    $isValidEndpoint = [Uri]::TryCreate(
        $endpoint, [UriKind]::Absolute, [ref]$uri)
    if (!$isValidEndpoint -or $uri.Scheme -notin @('http', 'https')) {
        throw "Invalid HTTP(S) service endpoint: $endpoint"
    }
    if ($uri.Scheme -eq 'http' -and !(Test-LoopbackHttpUrl $endpoint)) {
        throw "Non-loopback HTTP is forbidden. Use HTTPS for remote services: $endpoint"
    }
}
$allowLocalReviewHttp = Test-LoopbackHttpUrl $ApiBaseUrl

$positions = @(@(0, 0), @(1920, 0), @(0, 1080), @(1920, 1080))
$processes = @()
for ($index = 0; $index -lt 4; ++$index) {
    $number = $index + 1
    $userDirectory = Join-Path $SessionRoot "UserDir-$number"
    $logPath = Join-Path $SessionRoot "Client-$number.log"
    New-Item -ItemType Directory -Path $userDirectory -Force | Out-Null
    $position = $positions[$index]
    $arguments = @(
        '-windowed',
        '-ResX=1920',
        '-ResY=1080',
        '-ForceRes',
        "-WinX=$($position[0])",
        "-WinY=$($position[1])",
        '-Multiprocess',
        "-UserDir=$userDirectory",
        '-MahjongAuthMode=RemoteAuth',
        '-MahjongLobbyBackend=RemoteLobby',
        "-MahjongApiBaseUrl=$ApiBaseUrl",
        "-MahjongRealtimeBaseUrl=$RealtimeBaseUrl",
        '-log',
        "-AbsLog=$logPath"
    )
    if ($allowLocalReviewHttp) {
        $arguments += '-MahjongAllowInsecureLoopbackApi'
    }
    if (![string]::IsNullOrWhiteSpace($PatchBaseUrl)) {
        $arguments += "-MahjongPatchBaseUrl=$PatchBaseUrl"
    }
    $process = Start-Process -FilePath $client -ArgumentList $arguments -PassThru
    $processes += [pscustomobject]@{
        Number = $number
        Process = $process
        LogPath = $logPath
    }
}

$loginPattern =
    'Root HUD backing layer restored for screen /Game/UI/Screens/WBP_Login.WBP_Login_C'
$deadline = [DateTimeOffset]::Now.AddSeconds($LoginScreenTimeoutSeconds)
do {
    $allAtLogin = $true
    foreach ($entry in $processes) {
        if ($entry.Process.HasExited) {
            throw "Client $($entry.Number) exited before showing the login screen."
        }
        $hasLog = Test-Path -LiteralPath $entry.LogPath
        $logText = if ($hasLog) {
            Get-Content -LiteralPath $entry.LogPath -Raw
        } else {
            ''
        }
        if (!$hasLog -or $logText -notmatch $loginPattern) {
            $allAtLogin = $false
        }
    }
    if (!$allAtLogin) { Start-Sleep -Milliseconds 250 }
} while (!$allAtLogin -and [DateTimeOffset]::Now -lt $deadline)
if (!$allAtLogin) {
    throw "Not all clients showed the login screen within $LoginScreenTimeoutSeconds seconds."
}

$summary = [ordered]@{
    schemaVersion = 1
    startedAtLogin = $true
    autoLoginAllowed = $false
    authMode = 'RemoteAuth'
    lobbyBackend = 'RemoteLobby'
    sessionRoot = $SessionRoot
    clients = @($processes | ForEach-Object {
        [ordered]@{
            number = $_.Number
            processId = $_.Process.Id
            log = $_.LogPath
            loginScreenVisible = $true
        }
    })
}
$summary | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $SessionRoot 'launch-summary.json') -Encoding utf8
Write-Host "FOUR_CLIENT_LOGIN_SCREENS_OK clients=4 session=$SessionRoot"
