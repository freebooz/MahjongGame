<#
检查客户端、Dedicated Server 和服务发布包之间的模块、配置、Schema 与敏感文件隔离。
发现服务端代码、开发凭据或不应发布的资产进入客户端包时立即失败。
#>
[CmdletBinding()]
param(
    [string]$Root = '',
    [string]$ClientReceipt = '',
    [string]$ServerReceipt = '',
    [ValidateSet('Client', 'Server', 'Both')]
    [string]$Role = 'Both'
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Split-Path -Parent $PSScriptRoot }
$Root = [IO.Path]::GetFullPath($Root)
if (!$ClientReceipt) {
    $ClientReceipt = Get-ChildItem -LiteralPath (Join-Path $Root 'Binaries\Win64') `
        -Filter 'GuiyangMahjongClient*.target' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (!$ServerReceipt) {
    $ServerReceipt = Get-ChildItem -LiteralPath @(
        (Join-Path $Root 'Binaries\Linux'),
        (Join-Path $Root 'Binaries\Win64')) `
        -Filter 'GuiyangMahjongServer*.target' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}

$projectDescriptor = Get-Content -LiteralPath (Join-Path $Root 'GuiyangMahjong.uproject') -Raw | ConvertFrom-Json
if (!$projectDescriptor.DisableEnginePluginsByDefault) {
    throw 'Engine plugins must be disabled by default and explicitly allow-listed.'
}
$moduleTypes = @{}
foreach ($module in $projectDescriptor.Modules) { $moduleTypes[$module.Name] = $module.Type }
if ($moduleTypes.GuiyangMahjongClient -ne 'ClientOnly' -or
    $moduleTypes.GuiyangMahjongOnline -ne 'ClientOnly' -or
    $moduleTypes.GuiyangMahjongServer -ne 'ServerOnly') {
    throw 'Client, Online, and Server modules are not target-isolated in GuiyangMahjong.uproject.'
}
$agonesReference = $projectDescriptor.Plugins | Where-Object Name -eq 'Agones'
if (!$agonesReference.Enabled -or $agonesReference.TargetAllowList -contains 'Client') {
    throw 'Agones must be enabled only for Server/Editor targets.'
}
$defaultGame = Get-Content -LiteralPath (Join-Path $Root 'Config\DefaultGame.ini') -Raw
if ($defaultGame -match 'DirectoriesToAlwaysCook|MapsToCook') {
    throw 'DefaultGame.ini contains global Cook roots; move them to target-platform configs.'
}
$defaultEngine = Get-Content -LiteralPath (Join-Path $Root 'Config\DefaultEngine.ini') -Raw
if ($defaultEngine -notmatch 'MobileLocalLightSetting=LOCAL_LIGHTS_ENABLED' -or
    $defaultEngine -notmatch 'bMobileAllowMovableSpotlightShadows=False') {
    throw 'Mobile room lighting shader policy is not explicitly configured.'
}

if ($Role -in @('Client', 'Both')) {
    foreach ($asset in @(
        'Content\Client\Room\Presentation\BP_MahjongRoomPresentation.uasset')) {
        if (!(Test-Path -LiteralPath (Join-Path $Root $asset))) {
            throw "Client presentation asset is missing: $asset"
        }
    }
    foreach ($config in @('Config\Windows\WindowsGame.ini', 'Config\Android\AndroidGame.ini')) {
        $text = Get-Content -LiteralPath (Join-Path $Root $config) -Raw
        if ($text -notmatch '/Game/UI' -or $text -notmatch '/Game/Art/Mahjong' -or
            $text -notmatch '/Game/Client/Room/Presentation' -or
            $text -notmatch '/Game/Maps/MahjongRoomMap' -or
            $text -notmatch 'DisallowedConfigFiles=GuiyangMahjong/Config/DefaultServer.ini' -or
            $text -notmatch 'DisallowedConfigFiles=GuiyangMahjong/Config/(WindowsServer|LinuxServer)/' -or
            $text -match '/Game/Maps/MahjongRoomVisualPreviewMap' -or
            $text -match '/Game/Maps/MahjongNetMap') {
            throw "Client Cook allow-list is incomplete: $config"
        }
    }
}
if ($Role -in @('Server', 'Both')) {
    $pakRules = Get-Content -LiteralPath (Join-Path $Root 'Config\DefaultPakFileRules.ini') -Raw
    foreach ($pattern in @(
        'Platforms="Linux"',
        'bExcludeFromPaks=true',
        'Content/Client/',
        'Content/UI/',
        'Content/Art/',
        'MahjongRoomVisualPreviewMap')) {
        if ($pakRules -notmatch [regex]::Escape($pattern)) {
            throw "Linux server Pak exclusion rule is missing: $pattern"
        }
    }
    foreach ($config in @('Config\WindowsServer\WindowsServerGame.ini',
        'Config\LinuxServer\LinuxServerGame.ini')) {
        $text = Get-Content -LiteralPath (Join-Path $Root $config) -Raw
        if ($text -notmatch '/Game/Maps/MahjongRoomMap' -or
            $text -match '/Game/Maps/MahjongNetMap' -or $text -notmatch '/Game/UI' -or
            $text -notmatch '/Game/Art' -or $text -notmatch '/Game/Client' -or
            $text -notmatch 'DisallowedConfigFiles=GuiyangMahjong/Config/Windows/WindowsGame.ini' -or
            $text -notmatch 'DisallowedConfigFiles=GuiyangMahjong/Config/Android/AndroidGame.ini' -or
            $text -match '/Game/Maps/MahjongRoomVisualPreviewMap') {
            throw "Server Cook isolation is incomplete: $config"
        }
    }
}

function Assert-ReceiptClean {
    param([string]$Path, [string]$Role, [string[]]$ForbiddenPatterns, [string[]]$RequiredPlugins)
    if (!(Test-Path -LiteralPath $Path)) { throw "$Role receipt is missing: $Path" }
    $receipt = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $searchable = @($receipt.BuildPlugins) + @($receipt.BuildProducts.Path) + @($receipt.RuntimeDependencies.Path)
    foreach ($pattern in $ForbiddenPatterns) {
        $matches = @($searchable | Where-Object { $_ -match $pattern })
        if ($matches.Count -gt 0) {
            throw "$Role isolation failed: forbidden '$pattern' found: $($matches[0])"
        }
    }
    foreach ($plugin in $RequiredPlugins) {
        if ($receipt.BuildPlugins -notcontains $plugin) {
            throw "$Role isolation failed: required plugin '$plugin' is absent"
        }
    }
}

if ($Role -in @('Client', 'Both')) {
    Assert-ReceiptClean -Path $ClientReceipt -Role 'Client' `
        -ForbiddenPatterns @('GuiyangMahjongServer', 'Agones') -RequiredPlugins @('OnlineSubsystemUtils')
}
if ($Role -in @('Server', 'Both')) {
    Assert-ReceiptClean -Path $ServerReceipt -Role 'Server' `
        -ForbiddenPatterns @('GuiyangMahjongClient', 'GuiyangMahjongOnline', 'NNERuntimeORT', 'NNEDenoiser',
            'MsQuic', '[\\/]Content[\\/]UI[\\/]', '[\\/]Content[\\/]Art[\\/]', '[\\/]Content[\\/]Slate[\\/]') `
        -RequiredPlugins @('Agones', 'OnlineSubsystemUtils')
}

Write-Host 'PACKAGE_ISOLATION_OK'
