[CmdletBinding()]
param(
    [string]$ClientExecutable = '',
    [ValidateRange(1, 4)]
    [int]$ClientCount = 4,
    [string]$AuthBaseUrl = 'http://127.0.0.1:18082',
    [string]$LobbyBaseUrl = 'http://127.0.0.1:18080',
    [int]$WindowWidth = 1920,
    [int]$WindowHeight = 1080,
    [string]$SessionRoot = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ClientExecutable)) {
    $ClientExecutable = Join-Path $root `
        'Saved\StagedBuilds\Win64Client\WindowsClient\GuiyangMahjongClient.exe'
}
if (!(Test-Path -LiteralPath $ClientExecutable -PathType Leaf)) {
    throw "Packaged client executable was not found: $ClientExecutable"
}

function Test-LoopbackHttpUrl([string]$Value) {
    $uri = $null
    if (![Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri)) { return $false }
    if ($uri.Scheme -ne 'http') { return $false }
    return $uri.Host -in @('localhost', '127.0.0.1', '::1')
}

foreach ($endpoint in @($AuthBaseUrl, $LobbyBaseUrl)) {
    $uri = $null
    if (![Uri]::TryCreate($endpoint, [UriKind]::Absolute, [ref]$uri)) {
        throw "Invalid service endpoint: $endpoint"
    }
    if ($uri.Scheme -notin @('http', 'https')) {
        throw "Only HTTP(S) service endpoints are supported: $endpoint"
    }
    if ($uri.Scheme -eq 'http' -and !(Test-LoopbackHttpUrl $endpoint)) {
        throw "Non-loopback HTTP is forbidden. Use HTTPS for remote services: $endpoint"
    }
}

if ([string]::IsNullOrWhiteSpace($SessionRoot)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $SessionRoot = Join-Path $root "Saved\ManualMatch\$stamp"
}
New-Item -ItemType Directory -Path $SessionRoot -Force | Out-Null

$positions = @(
    @(0, 0),
    @($WindowWidth, 0),
    @(0, $WindowHeight),
    @($WindowWidth, $WindowHeight)
)
$allowLocalReviewHttp = Test-LoopbackHttpUrl $AuthBaseUrl
$started = @()
for ($index = 0; $index -lt $ClientCount; ++$index) {
    $number = $index + 1
    $userDirectory = Join-Path $SessionRoot "UserDir-$number"
    New-Item -ItemType Directory -Path $userDirectory -Force | Out-Null
    $absoluteLog = Join-Path $SessionRoot "Client-$number.log"
    $arguments = @(
        '-windowed',
        "-ResX=$WindowWidth",
        "-ResY=$WindowHeight",
        '-ForceRes',
        "-WinX=$($positions[$index][0])",
        "-WinY=$($positions[$index][1])",
        '-Multiprocess',
        "-UserDir=$userDirectory",
        '-MahjongAuthMode=RemoteAuth',
        "-MahjongAuthBaseUrl=$AuthBaseUrl",
        '-MahjongLobbyBackend=RemoteLobby',
        "-MahjongLobbyBaseUrl=$LobbyBaseUrl",
        '-log',
        "-AbsLog=$absoluteLog"
    )
    if ($allowLocalReviewHttp) {
        # Shipping clients require this explicit opt-in for local manual review.
        # The runtime still rejects every non-loopback HTTP endpoint.
        $arguments += '-MahjongAllowInsecureLoopbackAuth'
    }
    $process = Start-Process -FilePath $ClientExecutable -ArgumentList $arguments -PassThru
    $started += [pscustomobject]@{
        Client = $number
        BootstrapPid = $process.Id
        UserDirectory = $userDirectory
        Log = $absoluteLog
        LocalReviewHttpOptIn = $allowLocalReviewHttp
    }
}

$manifestPath = Join-Path $SessionRoot 'launch-manifest.json'
$started | ConvertTo-Json -Depth 3 |
    Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Host "MANUAL_CLIENTS_STARTED root=$SessionRoot count=$ClientCount localReviewHttp=$allowLocalReviewHttp"
$started | Format-Table -AutoSize
