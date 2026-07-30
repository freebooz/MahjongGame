$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot 'lib\ProjectEnvironment.psm1') -Force
$projectRoot = Resolve-MahjongProjectRoot
# 清理范围只允许指向本仓库中这个已知的生成资产族，避免从其他副本误删同名目录。
$targetDirectory = Join-Path $projectRoot 'Content\Art\Mahjong\Mahjong50'
$expectedDirectory = [System.IO.Path]::GetFullPath($targetDirectory)

if (-not (Test-Path -LiteralPath $targetDirectory -PathType Container)) {
    Write-Host "[Mahjong50Orphans] target directory is already absent"
    exit 0
}

$resolvedDirectory = (Resolve-Path -LiteralPath $targetDirectory).Path
if (-not $resolvedDirectory.Equals(
    $expectedDirectory,
    [System.StringComparison]::OrdinalIgnoreCase
)) {
    throw "Refusing unexpected target path: $resolvedDirectory"
}

$files = @(Get-ChildItem -LiteralPath $resolvedDirectory -File -Recurse)
foreach ($file in $files) {
    if ($file.Extension -notin @(".uasset", ".uexp", ".ubulk")) {
        throw "Refusing unexpected file in generated asset directory: $($file.FullName)"
    }
}
foreach ($file in $files) {
    Remove-Item -LiteralPath $file.FullName -Force
}

$directories = @(
    Get-ChildItem -LiteralPath $resolvedDirectory -Directory -Recurse |
        Sort-Object FullName -Descending
)
foreach ($directory in $directories) {
    Remove-Item -LiteralPath $directory.FullName -Force
}
Remove-Item -LiteralPath $resolvedDirectory -Force

Write-Host "[Mahjong50Orphans] PHYSICAL_PURGE_OK deleted=$($files.Count)"
