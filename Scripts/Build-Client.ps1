<#
构建 Windows 客户端：解析项目与 Unreal 根目录，编译指定配置并输出可追踪日志。
输入路径必须显式或由项目环境模块解析；失败时保留原始退出码，不得把未完成产物标记为成功。
#>
[CmdletBinding()]
param(
    [string]$EngineRoot = '',
    [ValidateSet('Win64', 'Android')]
    [string]$Platform = 'Win64',
    [ValidateSet('Development', 'Shipping')]
    [string]$Configuration = 'Development',
    [string]$StagingDirectory = ''
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\ProjectEnvironment.psm1') -Force
$root = Resolve-MahjongProjectRoot
$EngineRoot = Resolve-UnrealEngineRoot -ExplicitRoot $EngineRoot
$project = Join-Path $root 'GuiyangMahjong.uproject'
$uat = Join-Path $EngineRoot 'Engine\Build\BatchFiles\RunUAT.bat'
if ([string]::IsNullOrWhiteSpace($StagingDirectory)) {
    $StagingDirectory = Join-Path $root "Saved\StagedBuilds\${Platform}Client"
}
foreach ($required in @($project, $uat)) {
    if (!(Test-Path -LiteralPath $required)) { throw "Required path not found: $required" }
}

& $uat BuildCookRun `
    "-project=$project" `
    -noP4 `
    -utf8output `
    -client `
    -clienttarget=GuiyangMahjongClient `
    "-clientplatform=$Platform" `
    "-clientconfig=$Configuration" `
    -build `
    -cook `
    -stage `
    -pak `
    -SkipCookingEditorContent `
    -nodebuginfo `
    "-stagingdirectory=$StagingDirectory"
if ($LASTEXITCODE -ne 0) { throw "Client package failed with exit code $LASTEXITCODE" }

$clientContainer = Get-ChildItem -LiteralPath $StagingDirectory -Recurse -File `
    -Filter 'GuiyangMahjong-*Client.utoc' | Select-Object -First 1
if ($null -eq $clientContainer) {
    throw "Client IoStore container was not found below $StagingDirectory"
}
$unrealPak = Join-Path $EngineRoot 'Engine\Binaries\Win64\UnrealPak.exe'
$containerListing = @(& $unrealPak $clientContainer.FullName -List 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw "Could not inspect client IoStore container (exit code $LASTEXITCODE)."
}
if (!($containerListing -match 'GuiyangMahjong/Content/Client/Room/Presentation/BP_MahjongRoomPresentation.uasset')) {
    throw 'Client content validation failed: BP_MahjongRoomPresentation is absent.'
}
if ($containerListing -match 'MahjongRoomVisualPreviewMap') {
    throw 'Client content validation failed: editor-only room preview map is packaged.'
}
Write-Host 'CLIENT_CONTENT_ISOLATION_OK'

$clientReceipt = Join-Path $root "Binaries\$Platform\GuiyangMahjongClient-$Platform-$Configuration.target"
if (!(Test-Path -LiteralPath $clientReceipt -PathType Leaf)) {
    # UE 5.8 emits the canonical target receipt without platform/configuration suffixes for this
    # staged client target. Retain compatibility with older engines that used the long filename.
    $clientReceipt = Join-Path $root "Binaries\$Platform\GuiyangMahjongClient.target"
}
& (Join-Path $PSScriptRoot 'Test-PackageIsolation.ps1') -Root $root -Role Client `
    -ClientReceipt $clientReceipt
Write-Host "CLIENT_PACKAGE_OK target=GuiyangMahjongClient platform=$Platform stage=$StagingDirectory"
