[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [int]$ReadyTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $projectRoot 'Deploy\linux\compose.yaml'
$envFile = Join-Path $projectRoot 'Deploy\linux\.env'
$linuxServerArtifact = Join-Path $projectRoot 'Artifacts\LinuxServer'
$composeArguments = @('--env-file', $envFile, '-f', $composeFile)

function Get-EnvironmentMap {
    $result = @{}
    if (!(Test-Path -LiteralPath $envFile)) {
        return $result
    }

    foreach ($line in Get-Content -LiteralPath $envFile) {
        if ($line -match '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
            $result[$Matches[1]] = $Matches[2]
        }
    }
    return $result
}

function New-RandomSecret {
    $bytes = [byte[]]::new(32)
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
    } finally {
        $generator.Dispose()
    }
    return ([BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
}

function Set-MissingEnvironmentValue {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Value,
        [switch]$ReplacePlaceholder,
        [switch]$Force
    )

    $lines = [Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $envFile) {
        foreach ($line in Get-Content -LiteralPath $envFile) {
            $lines.Add($line)
        }
    }

    $pattern = '^{0}=(.*)$' -f [regex]::Escape($Name)
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -notmatch $pattern) {
            continue
        }

        $currentValue = $Matches[1]
        if ($Force -or
            [string]::IsNullOrWhiteSpace($currentValue) -or
            ($ReplacePlaceholder -and $currentValue -match '^replace-with-')) {
            $lines[$index] = "$Name=$Value"
            [IO.File]::WriteAllLines($envFile, $lines, [Text.UTF8Encoding]::new($false))
        }
        return
    }

    $lines.Add("$Name=$Value")
    [IO.File]::WriteAllLines($envFile, $lines, [Text.UTF8Encoding]::new($false))
}

function Invoke-Compose {
    & docker compose @composeArguments @args
    if ($LASTEXITCODE -ne 0) {
        throw "Docker Compose failed with exit code $LASTEXITCODE."
    }
}

function Wait-ContainerHealthy {
    param([Parameter(Mandatory)][string]$Service)

    $deadline = [DateTimeOffset]::Now.AddSeconds($ReadyTimeoutSeconds)
    do {
        $containerId = (& docker compose @composeArguments ps --quiet $Service).Trim()
        if ($containerId) {
            $health = (& docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' $containerId).Trim()
            if ($health -eq 'healthy' -or $health -eq 'running') {
                return
            }
            if ($health -eq 'unhealthy' -or $health -eq 'exited' -or $health -eq 'dead') {
                throw "$Service entered the $health state."
            }
        }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::Now -lt $deadline)

    throw "$Service did not become healthy within $ReadyTimeoutSeconds seconds."
}

function Wait-Endpoint {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Uri
    )

    $deadline = [DateTimeOffset]::Now.AddSeconds($ReadyTimeoutSeconds)
    do {
        try {
            Invoke-RestMethod -Uri $Uri -TimeoutSec 5 | Out-Null
            return
        } catch {
            Start-Sleep -Seconds 2
        }
    } while ([DateTimeOffset]::Now -lt $deadline)

    throw "$Name did not become ready at $Uri within $ReadyTimeoutSeconds seconds."
}

if (!(Test-Path -LiteralPath $composeFile)) {
    throw "Compose file was not found: $composeFile"
}
if (!(Test-Path -LiteralPath $linuxServerArtifact)) {
    throw "Linux Dedicated Server artifact was not found: $linuxServerArtifact"
}
$serverBinary = Get-ChildItem -LiteralPath $linuxServerArtifact -Recurse -File -Filter 'GuiyangMahjongServer' |
    Select-Object -First 1
if ($null -eq $serverBinary) {
    throw "GuiyangMahjongServer is missing from $linuxServerArtifact"
}

& docker info *> $null
if ($LASTEXITCODE -ne 0) {
    throw 'Docker Desktop Linux engine is not ready.'
}

if (!(Test-Path -LiteralPath $envFile)) {
    [IO.File]::WriteAllText($envFile, '', [Text.UTF8Encoding]::new($false))
}

$defaultValues = [ordered]@{
    MAHJONG_VERSION = 'dev'
    IMAGE_REGISTRY = 'local'
    GAME_SERVER_MAP = '/Game/Maps/MahjongRoomMap?game=/Script/GuiyangMahjongServer.GuiyangMahjongGameMode'
    MAHJONG_DATA_ROOT = '/var/lib/guiyang-mahjong'
    ADVERTISED_IP = '127.0.0.1'
    AUTH_PORT = '18082'
    LOBBY_PORT = '18080'
    ALLOCATOR_PORT = '18081'
    ADMIN_PORT = '18083'
    PLAYER_DATA_PORT = '18084'
    GAME_PORT_START = '19000'
    GAME_PORT_END = '19099'
    MAHJONG_CLUSTER_ID = 'local-docker'
    MAHJONG_NODE_ID = 'game-node'
    ADMIN_ENTERPRISE_IDENTITY_ENABLED = 'false'
    ADMIN_AUDIT_ARCHIVE_ENABLED = 'false'
    ADMIN_MANAGEMENT_ENABLED = 'false'
    ADMIN_COMMAND_EXECUTION_ENABLED = 'false'
}
foreach ($entry in $defaultValues.GetEnumerator()) {
    Set-MissingEnvironmentValue -Name $entry.Key -Value $entry.Value
}

$secretNames = @(
    'POSTGRES_PASSWORD',
    'MIGRATION_DB_PASSWORD',
    'AUTH_DB_PASSWORD',
    'LOBBY_DB_PASSWORD',
    'PLAYER_DATA_DB_PASSWORD',
    'ADMIN_DB_PASSWORD',
    'MONITOR_DB_PASSWORD',
    'AUDIT_DB_PASSWORD',
    'ARCHIVE_DB_PASSWORD',
    'PLAYER_TOKEN_SIGNING_KEY',
    'GUEST_IDENTITY_PEPPER',
    'JOIN_TICKET_SIGNING_KEY',
    'LOBBY_INTERNAL_TOKEN',
    'ALLOCATOR_SERVICE_TOKEN',
    'MONITORING_READ_ONLY_TOKEN',
    'AUTH_MANAGEMENT_COMMAND_TOKEN',
    'LOBBY_MANAGEMENT_COMMAND_TOKEN',
    'ALLOCATOR_MANAGEMENT_COMMAND_TOKEN',
    'ADMIN_READ_ONLY_TOKEN',
    'ADMIN_EVIDENCE_INGESTION_TOKEN',
    'ADMIN_TOPOLOGY_REGISTRATION_TOKEN',
    'ADMIN_TOPOLOGY_LOBBY_TOKEN',
    'ADMIN_TOPOLOGY_ALLOCATOR_TOKEN',
    'PLAYER_DATA_SOURCE_INGESTION_TOKEN',
    'PLAYER_DATA_ADMIN_COMMAND_TOKEN',
    'PLAYER_DATA_CHAT_GATEWAY_TOKEN',
    'PLAYER_DATA_MONITORING_TOKEN',
    'ADMIN_ROOM_OPERATOR_TOKEN',
    'ADMIN_ROOM_APPROVER_TOKEN',
    'ADMIN_PLAYER_OPERATOR_TOKEN',
    'ADMIN_PLAYER_APPROVER_TOKEN',
    'ADMIN_INFRASTRUCTURE_OPERATOR_TOKEN',
    'ADMIN_SANCTION_OPERATOR_TOKEN',
    'ADMIN_RISK_ANALYST_TOKEN',
    'ADMIN_SUPPORT_OPERATOR_TOKEN',
    'ADMIN_COMPENSATION_OPERATOR_TOKEN',
    'ADMIN_CHAT_COMPLIANCE_TOKEN'
)
foreach ($secretName in $secretNames) {
    Set-MissingEnvironmentValue -Name $secretName -Value (New-RandomSecret) -ReplacePlaceholder
}

$environment = Get-EnvironmentMap
$invalidSecrets = @($secretNames | Where-Object {
    !$environment.ContainsKey($_) -or $environment[$_].Length -lt 32
})
if ($invalidSecrets.Count -gt 0) {
    throw "Invalid or missing local credentials: $($invalidSecrets -join ', ')"
}
$duplicateSecrets = @(
    $secretNames |
        ForEach-Object { $environment[$_] } |
        Group-Object |
        Where-Object Count -gt 1
)
if ($duplicateSecrets.Count -gt 0) {
    throw 'Local service credentials must be distinct.'
}
foreach ($name in $environment.Keys) {
    Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
}

Invoke-Compose config --quiet

if (!$SkipBuild) {
    Invoke-Compose build
}

Invoke-Compose up --detach postgres redis
Wait-ContainerHealthy -Service postgres
Wait-ContainerHealthy -Service redis

$postgresContainerId = (& docker compose @composeArguments ps --quiet postgres).Trim()
$networkSettings = (& docker inspect --format '{{json .NetworkSettings.Networks}}' $postgresContainerId) |
    ConvertFrom-Json
$allocatorInternalHost = @(
    $networkSettings.PSObject.Properties.Value |
        ForEach-Object { $_.Gateway } |
        Where-Object { ![string]::IsNullOrWhiteSpace($_) }
) | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($allocatorInternalHost)) {
    throw 'Could not detect the Compose gateway for the host-networked Allocator.'
}
Set-MissingEnvironmentValue -Name 'ALLOCATOR_INTERNAL_HOST' `
    -Value $allocatorInternalHost -Force
Remove-Item -LiteralPath 'Env:ALLOCATOR_INTERNAL_HOST' -ErrorAction SilentlyContinue
$environment = Get-EnvironmentMap

$dataRoot = $environment['MAHJONG_DATA_ROOT']
if (!$dataRoot.StartsWith('/') -or $dataRoot -eq '/') {
    throw "MAHJONG_DATA_ROOT must be a non-root Linux path: $dataRoot"
}
$allocatorData = "$dataRoot/allocator"
& docker run --rm --user '0:0' --entrypoint sh `
    --volume "${allocatorData}:/data" redis:8-alpine `
    -c 'mkdir -p /data/state /data/outbox && chown -R 1654:1654 /data' *> $null
if ($LASTEXITCODE -ne 0) {
    throw "Allocator data-directory preparation failed: $allocatorData"
}

$postgresPassword = $environment['POSTGRES_PASSWORD']
$reconcileSql = "\set role_password '$postgresPassword'`nALTER ROLE mahjong WITH PASSWORD :'role_password';"
$reconcileSql | & docker compose @composeArguments exec -T -u postgres postgres `
    psql --no-psqlrc --set=ON_ERROR_STOP=1 --username=mahjong --dbname=mahjong *> $null
if ($LASTEXITCODE -ne 0) {
    throw 'PostgreSQL credential reconciliation failed.'
}

Invoke-Compose up --detach --remove-orphans

$serviceEndpoints = [ordered]@{
    Auth = "http://127.0.0.1:$($environment['AUTH_PORT'])/health/ready"
    Allocator = "http://127.0.0.1:$($environment['ALLOCATOR_PORT'])/health/ready"
    Lobby = "http://127.0.0.1:$($environment['LOBBY_PORT'])/health/ready"
    PlayerData = "http://127.0.0.1:$($environment['PLAYER_DATA_PORT'])/health/ready"
    Admin = "http://127.0.0.1:$($environment['ADMIN_PORT'])/health/ready"
}
foreach ($endpoint in $serviceEndpoints.GetEnumerator()) {
    Wait-Endpoint -Name $endpoint.Key -Uri $endpoint.Value
}

Invoke-Compose ps
Write-Host "ALL_SERVICES_READY admin=http://127.0.0.1:$($environment['ADMIN_PORT'])"
