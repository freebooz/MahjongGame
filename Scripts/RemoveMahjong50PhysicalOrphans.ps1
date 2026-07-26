$ErrorActionPreference = "Stop"

$targetDirectory = "H:\MahjongGame\Content\Art\Mahjong\Mahjong50"
$expectedDirectory = [System.IO.Path]::GetFullPath(
    "H:\MahjongGame\Content\Art\Mahjong\Mahjong50"
)

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
