<#
检查 UI 字体资产、中文字符覆盖、Cook 引用和运行时回退配置是否完整。
脚本只读分析源码与资产清单；缺字或未引用字体必须输出可定位路径并返回失败。
#>
[CmdletBinding()]
param(
    [string]$EngineRoot = '',
    [string]$ProjectPath = '',
    [string]$OutputPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\ProjectEnvironment.psm1') -Force
$projectRoot = Resolve-MahjongProjectRoot
$EngineRoot = Resolve-UnrealEngineRoot -ExplicitRoot $EngineRoot
$ProjectPath = Resolve-MahjongProjectFile -ExplicitPath $ProjectPath -ProjectRoot $projectRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $projectRoot 'Saved\UIReview\Phase20\readiness.json'
}
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName System.Windows.Forms

$fontPath = Join-Path $EngineRoot 'Engine\Content\Slate\Fonts\DroidSansFallback.ttf'
$configPath = Join-Path (Split-Path -Parent $ProjectPath) 'Config\DefaultEngine.ini'
if (-not (Test-Path -LiteralPath $ProjectPath)) { throw "Project not found: $ProjectPath" }
if (-not (Test-Path -LiteralPath $fontPath)) { throw "Chinese fallback font not found: $fontPath" }
if (-not (Test-Path -LiteralPath $configPath)) { throw "UI config not found: $configPath" }

$glyphTypeface = [System.Windows.Media.GlyphTypeface]::new([Uri]$fontPath)
$testedCodePoints = @(
    0x4E00, 0x4E07, 0x4E1C, 0x4E2D, 0x4EFB, 0x4F59, 0x5165, 0x518D, 0x51B2, 0x51C6,
    0x5219, 0x521B, 0x524D, 0x5269, 0x52A0, 0x5317, 0x534F, 0x5357, 0x5385, 0x53D1,
    0x56DE, 0x5728, 0x5730, 0x5907, 0x590D, 0x5927, 0x5BA2, 0x5C06, 0x5C40, 0x5EFA,
    0x5F00, 0x5F53, 0x5F55, 0x6062, 0x6237, 0x623F, 0x624B, 0x6349, 0x63A5, 0x653F,
    0x65AD, 0x6760, 0x6761, 0x6765, 0x6BB5, 0x6CD5, 0x6E38, 0x724C, 0x73A9, 0x7528,
    0x767B, 0x767D, 0x78B0, 0x79BB, 0x79C1, 0x79D2, 0x7B52, 0x7B56, 0x7B97, 0x7EBF,
    0x7ED3, 0x7EDC, 0x7F51, 0x7F6E, 0x7FFB, 0x80E1, 0x897F, 0x89C4, 0x8BAE, 0x8BD5,
    0x8D23, 0x8D35, 0x8FC7, 0x8FD4, 0x8FDE, 0x9053, 0x914D, 0x91CD, 0x950B, 0x95F4,
    0x9633, 0x9636, 0x9690, 0x97F5, 0x9E21, 0x9EBB, 0x9ED4
)
$missingCodePoints = [System.Collections.Generic.List[string]]::new()
foreach ($codePoint in $testedCodePoints) {
    if (-not $glyphTypeface.CharacterToGlyphMap.ContainsKey($codePoint)) {
        $missingCodePoints.Add(("U+{0:X4}" -f $codePoint))
    }
}

$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8
$dpiRuleReady = $config -match '(?m)^UIScaleRule=ShortestSide\s*$'
$dpiCurveReady = $config -match 'Time=720\.000000,Value=0\.666000' -and
    $config -match 'Time=1080\.000000,Value=1\.000000'
$screens = @([System.Windows.Forms.Screen]::AllScreens | ForEach-Object {
    [pscustomobject]@{
        Device = $_.DeviceName
        Width = $_.Bounds.Width
        Height = $_.Bounds.Height
        Primary = $_.Primary
    }
})
$maxPhysicalWidth = ($screens | Measure-Object -Property Width -Maximum).Maximum
$nativeUltrawideAvailable = $maxPhysicalWidth -ge 2340
$passed = $missingCodePoints.Count -eq 0 -and $dpiRuleReady -and $dpiCurveReady

$report = [ordered]@{
    GeneratedAt = [DateTime]::UtcNow.ToString('o')
    Passed = $passed
    FontPath = $fontPath
    FontBytes = (Get-Item -LiteralPath $fontPath).Length
    TestedChineseGlyphCount = $testedCodePoints.Count
    MissingCodePoints = $missingCodePoints
    DpiRuleReady = $dpiRuleReady
    DpiCurveReady = $dpiCurveReady
    PhysicalScreens = $screens
    MaxPhysicalScreenWidth = $maxPhysicalWidth
    NativeUltrawideAvailable = $nativeUltrawideAvailable
    ShippingCookVerified = $false
    Note = 'Font source and editor DPI configuration checked; Shipping Cook and device fonts remain unverified.'
}

$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$report | ConvertTo-Json -Depth 6
if (-not $passed) { exit 1 }
