<#
按设备尺寸和界面场景矩阵执行 UI 视觉审查，每个目标窗口独立截图并命名。
矩阵中单项失败不得中断证据汇总，但最终退出码必须反映整体失败状态。
#>
[CmdletBinding()]
param(
    [string]$EngineRoot = '',
    [string]$ProjectPath = '',
    [string[]]$Resolutions = @('1920x1080', '1280x720'),
    [string[]]$Screens = @(
        'Login', 'Lobby', 'CreateRoomDialog', 'JoinRoomDialog', 'Room',
        'RuleConfig', 'GameHUD', 'Settlement', 'ErrorToast', 'ReconnectOverlay'
    ),
    [string]$OutputPhase = 'Phase19',
    [int]$TimeoutSeconds = 45
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\ProjectEnvironment.psm1') -Force
$projectRoot = Resolve-MahjongProjectRoot
$EngineRoot = Resolve-UnrealEngineRoot -ExplicitRoot $EngineRoot
$ProjectPath = Resolve-MahjongProjectFile -ExplicitPath $ProjectPath -ProjectRoot $projectRoot

$runner = Join-Path $PSScriptRoot 'RunUIVisualReview.ps1'
if (-not (Test-Path -LiteralPath $runner)) { throw "Visual review runner not found: $runner" }
if ($OutputPhase -notmatch '^[A-Za-z0-9_-]+$') { throw 'OutputPhase contains unsupported characters.' }

$matrixRoot = Join-Path $projectRoot "Saved\UIReview\$OutputPhase"
New-Item -ItemType Directory -Path $matrixRoot -Force | Out-Null
$allResults = [System.Collections.Generic.List[object]]::new()
$startedAt = [DateTime]::UtcNow

foreach ($screen in $Screens) {
    if ($screen -notmatch '^[A-Za-z0-9_-]+$') { throw "Invalid screen name: $screen" }
    $screenPhase = "$OutputPhase/$screen"
    & $runner -EngineRoot $EngineRoot -ProjectPath $ProjectPath -Resolutions $Resolutions `
        -ScreenName $screen -OutputPhase $screenPhase -TimeoutSeconds $TimeoutSeconds | Out-Null
    $screenResultPath = Join-Path $projectRoot "Saved\UIReview\$screenPhase\result.json"
    if (-not (Test-Path -LiteralPath $screenResultPath)) {
        throw "Screen result was not created: $screenResultPath"
    }
    $screenResult = Get-Content -Raw -Encoding UTF8 $screenResultPath | ConvertFrom-Json
    foreach ($result in $screenResult.Results) { $allResults.Add($result) }
}

$failedResults = @($allResults | Where-Object { -not $_.Passed })
$report = [ordered]@{
    GeneratedAt = [DateTime]::UtcNow.ToString('o')
    DurationSeconds = [Math]::Round(([DateTime]::UtcNow - $startedAt).TotalSeconds, 2)
    Passed = $failedResults.Count -eq 0
    ScreenCount = $Screens.Count
    ResolutionCount = $Resolutions.Count
    CaseCount = $allResults.Count
    FailedCount = $failedResults.Count
    Screens = $Screens
    Resolutions = $Resolutions
    Results = $allResults
}
$resultPath = Join-Path $matrixRoot 'result.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding UTF8
$report | ConvertTo-Json -Depth 8
if ($failedResults.Count -gt 0) { exit 1 }
