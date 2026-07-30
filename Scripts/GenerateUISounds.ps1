<#
确定性生成 UI 审查用音效源文件，供后续 Unreal 资产导入流程使用。
输出仅限项目声明目录；重新生成前应确认目标范围，避免覆盖人工制作的无关音频。
#>
[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$outputDirectory = Join-Path $ProjectRoot 'SourceArt\UI\Audio'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$sampleRate = 48000

function Write-PcmWave {
    param(
        [string]$Name,
        [double]$Duration,
        [object[]]$Events,
        [double]$TransientGain = 0.0,
        [double]$MasterGain = 0.75
    )

    $sampleCount = [int][Math]::Ceiling($Duration * $sampleRate)
    $dataSize = $sampleCount * 2
    $path = Join-Path $outputDirectory ($Name + '.wav')
    $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes('RIFF'))
        $writer.Write([int](36 + $dataSize))
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes('WAVE'))
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes('fmt '))
        $writer.Write([int]16)
        $writer.Write([int16]1)
        $writer.Write([int16]1)
        $writer.Write([int]$sampleRate)
        $writer.Write([int]($sampleRate * 2))
        $writer.Write([int16]2)
        $writer.Write([int16]16)
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes('data'))
        $writer.Write([int]$dataSize)

        for ($index = 0; $index -lt $sampleCount; $index++) {
            $time = $index / [double]$sampleRate
            $value = 0.0
            foreach ($event in $Events) {
                $localTime = $time - [double]$event.Start
                if ($localTime -lt 0.0 -or $localTime -gt [double]$event.Duration) { continue }
                $attack = [Math]::Min(1.0, $localTime / 0.004)
                $release = [Math]::Max(0.0, 1.0 - $localTime / [double]$event.Duration)
                $envelope = $attack * [Math]::Pow($release, [double]$event.Decay)
                $value += [Math]::Sin(2.0 * [Math]::PI * [double]$event.Frequency * $localTime) * [double]$event.Gain * $envelope
            }
            if ($TransientGain -gt 0.0) {
                $value += [Math]::Sin(2.0 * [Math]::PI * 2400.0 * $time) * $TransientGain * [Math]::Exp(-55.0 * $time)
            }
            $value = [Math]::Max(-1.0, [Math]::Min(1.0, $value * $MasterGain))
            $writer.Write([int16][Math]::Round($value * 32767.0))
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
    Get-Item -LiteralPath $path
}

function New-ToneEvent {
    param([double]$Start, [double]$Duration, [double]$Frequency, [double]$Gain, [double]$Decay = 1.5)
    [pscustomobject]@{ Start=$Start; Duration=$Duration; Frequency=$Frequency; Gain=$Gain; Decay=$Decay }
}

$generated = @()
$generated += Write-PcmWave 'SFX_UI_Click' 0.08 @(
    (New-ToneEvent 0.00 0.08 920 0.55 2.2),
    (New-ToneEvent 0.00 0.06 1380 0.25 2.8)
) 0.24 0.68
$generated += Write-PcmWave 'SFX_Tile_Select' 0.10 @(
    (New-ToneEvent 0.00 0.10 760 0.58 2.2),
    (New-ToneEvent 0.01 0.07 1120 0.25 2.5)
) 0.32 0.72
$generated += Write-PcmWave 'SFX_Tile_Play' 0.16 @(
    (New-ToneEvent 0.00 0.16 360 0.62 2.4),
    (New-ToneEvent 0.00 0.09 980 0.34 2.8)
) 0.42 0.78
$generated += Write-PcmWave 'SFX_Peng' 0.26 @(
    (New-ToneEvent 0.00 0.20 523.25 0.54 1.5),
    (New-ToneEvent 0.035 0.22 783.99 0.48 1.7)
) 0.16 0.72
$generated += Write-PcmWave 'SFX_Gang' 0.34 @(
    (New-ToneEvent 0.00 0.30 329.63 0.58 1.4),
    (New-ToneEvent 0.035 0.28 493.88 0.50 1.5),
    (New-ToneEvent 0.075 0.25 659.25 0.36 1.6)
) 0.20 0.74
$generated += Write-PcmWave 'SFX_Hu' 0.58 @(
    (New-ToneEvent 0.00 0.24 523.25 0.50 1.3),
    (New-ToneEvent 0.10 0.26 659.25 0.50 1.3),
    (New-ToneEvent 0.20 0.34 783.99 0.56 1.2),
    (New-ToneEvent 0.28 0.28 1046.50 0.34 1.4)
) 0.12 0.70
$generated += Write-PcmWave 'SFX_Pass' 0.13 @(
    (New-ToneEvent 0.00 0.13 420 0.42 2.3),
    (New-ToneEvent 0.015 0.09 315 0.24 2.5)
) 0.08 0.60

$generated | Select-Object Name,Length,FullName | Format-Table -AutoSize
