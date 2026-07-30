<#
验证阶段四托管游戏服的启动、Allocator 注册、心跳、房间绑定和安全退出。
测试使用隔离端口和临时实例身份，失败时不得遗留后台游戏服进程。
#>
param(
    [int]$LobbyPort = 28100,
    [int]$AllocatorPort = 28101,
    [int]$GameServerPortStart = 29100,
    [string]$ServerExecutable = '',
    [switch]$VerifySettlementOutboxRecovery
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $ServerExecutable) {
    $stagedExecutable = Join-Path $root 'Saved/StagedBuilds/Phase4Server/WindowsServer/GuiyangMahjong/Binaries/Win64/GuiyangMahjongServer.exe'
    $builtExecutable = Join-Path $root 'Binaries/Win64/GuiyangMahjongServer.exe'
    $ServerExecutable = if (Test-Path -LiteralPath $stagedExecutable) {
        $stagedExecutable
    }
    else {
        $builtExecutable
    }
}
if (-not (Test-Path -LiteralPath $ServerExecutable)) {
    throw "GuiyangMahjongServer executable was not found: $ServerExecutable"
}

$prefixArguments = @(
    '/Engine/Maps/Entry',
    '-unattended',
    '-nullrhi',
    '-nosound'
)

& (Join-Path $PSScriptRoot 'Test-Phase3Integration.ps1') `
    -LobbyPort $LobbyPort `
    -AllocatorPort $AllocatorPort `
    -GameServerPortStart $GameServerPortStart `
    -GameServerExecutablePath (Resolve-Path -LiteralPath $ServerExecutable).Path `
    -GameServerPrefixArguments $prefixArguments `
    -RegistrationTimeoutSeconds 120 `
    -HeartbeatTimeoutSeconds 8 `
    -RouteWaitAttempts 480 `
    -FailureWaitAttempts 80 `
    -VerifySettlementOutboxRecovery:$VerifySettlementOutboxRecovery
