[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ClientExecutable,
    [string]$AuthBaseUrl = 'http://127.0.0.1:18082',
    [string]$LobbyBaseUrl = 'http://127.0.0.1:18080',
    [string]$SessionRoot = '',
    [ValidateRange(10, 60)]
    [int]$LoginScreenTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$client = (Resolve-Path -LiteralPath $ClientExecutable).Path
$packagedBinary = Join-Path (Split-Path -Parent $client) `
    'GuiyangMahjong\Binaries\Win64\GuiyangMahjongClient.exe'
if (Test-Path -LiteralPath $packagedBinary) {
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

foreach ($endpoint in @($AuthBaseUrl, $LobbyBaseUrl)) {
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
$allowLocalReviewHttp = Test-LoopbackHttpUrl $AuthBaseUrl

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
        "-MahjongAuthBaseUrl=$AuthBaseUrl",
        '-MahjongLobbyBackend=RemoteLobby',
        "-MahjongLobbyBaseUrl=$LobbyBaseUrl",
        '-log',
        "-AbsLog=$logPath"
    )
    if ($allowLocalReviewHttp) {
        $arguments += '-MahjongAllowInsecureLoopbackAuth'
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
