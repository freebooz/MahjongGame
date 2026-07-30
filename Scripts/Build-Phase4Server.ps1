<#
执行阶段四服务端编译、Cook 和打包验证，面向托管 Dedicated Server 基线。
调用 Unreal Build/UAT 失败时立即终止，并保留日志供人工审查，不自动部署不完整制品。
#>
param(
    [string]$EngineRoot = '',
    [ValidateSet('Debug', 'DebugGame', 'Development', 'Shipping', 'Test')]
    [string]$Configuration = 'Development',
    [string]$Map = '/Game/Maps/MahjongRoomMap',
    [string]$StagingDirectory = ''
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\ProjectEnvironment.psm1') -Force
$root = Resolve-MahjongProjectRoot
$EngineRoot = Resolve-UnrealEngineRoot -ExplicitRoot $EngineRoot
$project = Join-Path $root 'GuiyangMahjong.uproject'
$build = Join-Path $EngineRoot 'Engine/Build/BatchFiles/Build.bat'
$uat = Join-Path $EngineRoot 'Engine/Build/BatchFiles/RunUAT.bat'
if (-not $StagingDirectory) {
    $StagingDirectory = Join-Path $root 'Saved/StagedBuilds/Phase4Server'
}
foreach ($requiredPath in @($project, $build, $uat)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required path was not found: $requiredPath"
    }
}

& $build `
    GuiyangMahjongServer `
    Win64 `
    $Configuration `
    $project `
    -NoHotReloadFromIDE `
    -WaitMutex `
    -NoXGE
if ($LASTEXITCODE -ne 0) {
    throw "Dedicated Server build failed with exit code $LASTEXITCODE."
}

# This source-engine Editor receipt enables experimental content plugins that are
# irrelevant to the headless server and contain editor-only startup assets.
& $uat BuildCookRun `
    "-project=$project" `
    -noP4 `
    -server `
    -noclient `
    "-serverconfig=$Configuration" `
    -servertargetplatform=Win64 `
    -skipbuild `
    -cook `
    -stage `
    -pak `
    "-map=$Map" `
    "-stagingdirectory=$StagingDirectory" `
    -unattended `
    -noxge `
    -nodebuginfo `
    -SkipCookingEditorContent `
    '-additionalcookeroptions=-NoEnginePlugins'
if ($LASTEXITCODE -ne 0) {
    throw "Dedicated Server cook/stage failed with exit code $LASTEXITCODE."
}

$executable = Join-Path $StagingDirectory 'WindowsServer/GuiyangMahjong/Binaries/Win64/GuiyangMahjongServer.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Staged Dedicated Server executable was not found: $executable"
}
& (Join-Path $PSScriptRoot 'Test-PackageIsolation.ps1') -Root $root -Role Server
[pscustomobject]@{
    Status = 'SERVER_STAGE_OK'
    Executable = (Resolve-Path -LiteralPath $executable).Path
    Map = $Map
    Configuration = $Configuration
} | ConvertTo-Json -Compress
