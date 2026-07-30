[CmdletBinding()]
param(
    [string]$EngineRoot = '',
    [string]$ProjectPath = '',
    [string]$MapPath = '/Game/Maps/UIReviewMap',
    [string[]]$Resolutions = @('1920x1080', '1280x720', '2400x1080', '2340x1080'),
    [string]$ScreenName = 'Login',
    [string]$OutputPhase = 'Phase18',
    [int]$TimeoutSeconds = 45
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\ProjectEnvironment.psm1') -Force
$projectRoot = Resolve-MahjongProjectRoot
$EngineRoot = Resolve-UnrealEngineRoot -ExplicitRoot $EngineRoot
$ProjectPath = Resolve-MahjongProjectFile -ExplicitPath $ProjectPath -ProjectRoot $projectRoot
Add-Type -AssemblyName System.Drawing

function Get-ImageMetrics {
    param([string]$Path, [int]$ExpectedWidth, [int]$ExpectedHeight)

    $bitmap = [System.Drawing.Bitmap]::FromFile($Path)
    try {
        $samples = 0
        $nonBlack = 0
        $luminanceSum = 0.0
        $step = [Math]::Max(1, [Math]::Floor([Math]::Min($bitmap.Width, $bitmap.Height) / 90))
        $edgeBandX = [Math]::Max($step, [Math]::Floor($bitmap.Width * 0.04))
        $edgeBandY = [Math]::Max($step, [Math]::Floor($bitmap.Height * 0.04))
        $edgeSamples = [ordered]@{ Left = 0; Right = 0; Top = 0; Bottom = 0 }
        $edgeNonBlack = [ordered]@{ Left = 0; Right = 0; Top = 0; Bottom = 0 }
        for ($y = 0; $y -lt $bitmap.Height; $y += $step) {
            for ($x = 0; $x -lt $bitmap.Width; $x += $step) {
                $pixel = $bitmap.GetPixel($x, $y)
                $luminance = 0.2126 * $pixel.R + 0.7152 * $pixel.G + 0.0722 * $pixel.B
                $isNonBlack = $pixel.R -gt 5 -or $pixel.G -gt 5 -or $pixel.B -gt 5
                if ($isNonBlack) { $nonBlack++ }
                if ($x -lt $edgeBandX) { $edgeSamples.Left++; if ($isNonBlack) { $edgeNonBlack.Left++ } }
                if ($x -ge $bitmap.Width - $edgeBandX) { $edgeSamples.Right++; if ($isNonBlack) { $edgeNonBlack.Right++ } }
                if ($y -lt $edgeBandY) { $edgeSamples.Top++; if ($isNonBlack) { $edgeNonBlack.Top++ } }
                if ($y -ge $bitmap.Height - $edgeBandY) { $edgeSamples.Bottom++; if ($isNonBlack) { $edgeNonBlack.Bottom++ } }
                $luminanceSum += $luminance
                $samples++
            }
        }
        $nonBlackRatio = if ($samples -gt 0) { $nonBlack / $samples } else { 0.0 }
        $averageLuminance = if ($samples -gt 0) { $luminanceSum / $samples } else { 0.0 }
        $edgeRatios = [ordered]@{}
        foreach ($edge in @('Left', 'Right', 'Top', 'Bottom')) {
            $edgeRatios[$edge] = if ($edgeSamples[$edge] -gt 0) {
                [Math]::Round($edgeNonBlack[$edge] / $edgeSamples[$edge], 4)
            } else { 0.0 }
        }
        $minimumEdgeRatio = ($edgeRatios.Values | Measure-Object -Minimum).Minimum
        [pscustomobject]@{
            Width = $bitmap.Width
            Height = $bitmap.Height
            DimensionsMatch = $bitmap.Width -eq $ExpectedWidth -and $bitmap.Height -eq $ExpectedHeight
            NonBlackRatio = [Math]::Round($nonBlackRatio, 4)
            AverageLuminance = [Math]::Round($averageLuminance, 2)
            EdgeNonBlackRatio = $edgeRatios
            MinimumEdgeNonBlackRatio = $minimumEdgeRatio
            FileBytes = (Get-Item -LiteralPath $Path).Length
            Passed = ($bitmap.Width -eq $ExpectedWidth -and $bitmap.Height -eq $ExpectedHeight -and
                $nonBlackRatio -ge 0.15 -and $averageLuminance -ge 8.0 -and $minimumEdgeRatio -ge 0.10)
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

$editor = Join-Path $EngineRoot 'Engine\Binaries\Win64\UnrealEditor.exe'
if (-not (Test-Path -LiteralPath $editor)) { throw "UnrealEditor.exe not found: $editor" }
if (-not (Test-Path -LiteralPath $ProjectPath)) { throw "Project not found: $ProjectPath" }
if ($MapPath -notmatch '^/Game/[A-Za-z0-9_/-]+$' -or $MapPath.Contains('..')) {
    throw 'MapPath contains unsupported characters.'
}
if ($TimeoutSeconds -lt 10) { throw 'TimeoutSeconds must be at least 10.' }
if ($ScreenName -notmatch '^[A-Za-z0-9_-]+$') { throw 'ScreenName contains unsupported characters.' }
if ($OutputPhase -notmatch '^[A-Za-z0-9_\\/-]+$' -or $OutputPhase.Contains('..')) {
    throw 'OutputPhase contains unsupported characters.'
}

$outputRoot = Join-Path $projectRoot "Saved\UIReview\$OutputPhase"
$logRoot = Join-Path $outputRoot 'Logs'
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
$results = [System.Collections.Generic.List[object]]::new()

foreach ($resolution in $Resolutions) {
    if ($resolution -notmatch '^(?<Width>[0-9]{3,5})x(?<Height>[0-9]{3,5})$') {
        throw "Invalid resolution: $resolution"
    }
    $width = [int]$Matches.Width
    $height = [int]$Matches.Height
    $normalizedPhase = $OutputPhase.Replace('\', '/')
    $relativeName = "$normalizedPhase/$resolution/$ScreenName"
    $screenshotPath = Join-Path $outputRoot "$resolution\$ScreenName.png"
    $logPath = Join-Path $logRoot "$ScreenName-$resolution.log"
    New-Item -ItemType Directory -Path (Split-Path -Parent $screenshotPath) -Force | Out-Null
    Remove-Item -LiteralPath $screenshotPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue

    $arguments = @(
        $ProjectPath, $MapPath, '-game', '-RenderOffscreen',
        "-ResX=$width", "-ResY=$height", '-ForceRes',
        '-NoSplash', '-NoSound', '-unattended', '-nop4', '-d3d12', '-Multiprocess',
        '-UIReviewScreenshot', "-UIReviewScreen=$ScreenName", "-UIReviewName=$relativeName", '-UIReviewDelaySeconds=3',
        "-AbsLog=$logPath"
    )
    $process = Start-Process -FilePath $editor -ArgumentList $arguments -PassThru
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while (-not (Test-Path -LiteralPath $screenshotPath) -and [DateTime]::UtcNow -lt $deadline) {
            $process.Refresh()
            if ($process.HasExited -and -not (Test-Path -LiteralPath $screenshotPath)) {
                throw "UnrealEditor exited before creating screenshot for $resolution. See $logPath"
            }
            Start-Sleep -Milliseconds 250
        }
        if (-not (Test-Path -LiteralPath $screenshotPath)) {
            throw "Screenshot timed out for $resolution. See $logPath"
        }
        Start-Sleep -Milliseconds 500
        $metrics = Get-ImageMetrics -Path $screenshotPath -ExpectedWidth $width -ExpectedHeight $height
        $results.Add([pscustomobject]@{
            Resolution = $resolution
            Screen = $ScreenName
            Screenshot = $screenshotPath
            Log = $logPath
            Width = $metrics.Width
            Height = $metrics.Height
            DimensionsMatch = $metrics.DimensionsMatch
            NonBlackRatio = $metrics.NonBlackRatio
            AverageLuminance = $metrics.AverageLuminance
            EdgeNonBlackRatio = $metrics.EdgeNonBlackRatio
            MinimumEdgeNonBlackRatio = $metrics.MinimumEdgeNonBlackRatio
            FileBytes = $metrics.FileBytes
            Passed = $metrics.Passed
        })
    }
    finally {
        $process.Refresh()
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    }
}

$passed = @($results | Where-Object Passed).Count -eq $results.Count
$report = [ordered]@{
    GeneratedAt = [DateTime]::UtcNow.ToString('o')
    Passed = $passed
    Screen = $ScreenName
    Map = $MapPath
    Results = $results
}
$resultPath = Join-Path $outputRoot 'result.json'
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding UTF8
$report | ConvertTo-Json -Depth 6
if (-not $passed) { exit 1 }
