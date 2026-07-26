[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:\UnrealEngine-5.8.0-release',
    [string]$ProjectPath = '',
    [ValidateSet('Development', 'Shipping')]
    [string]$Configuration = 'Development',
    [string]$ArtifactDirectory = '',
    [switch]$SkipCook,
    [switch]$PostProcessOnly,
    [switch]$IncludeDebugSymbols
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = Join-Path $root 'GuiyangMahjong.uproject'
}
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $root 'Artifacts\LinuxServer'
}

$project = (Resolve-Path -LiteralPath $ProjectPath).Path
$engine = (Resolve-Path -LiteralPath $EngineRoot).Path
$artifact = [IO.Path]::GetFullPath($ArtifactDirectory)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $root 'Artifacts'))
if (!$artifact.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "ArtifactDirectory must stay below $artifactRoot"
}

$toolchain = $env:LINUX_MULTIARCH_ROOT
if ([string]::IsNullOrWhiteSpace($toolchain)) {
    $toolchain = [Environment]::GetEnvironmentVariable('LINUX_MULTIARCH_ROOT', 'Machine')
}
if ([string]::IsNullOrWhiteSpace($toolchain)) {
    $toolchain = [Environment]::GetEnvironmentVariable('LINUX_MULTIARCH_ROOT', 'User')
}
if ([string]::IsNullOrWhiteSpace($toolchain) -or !(Test-Path -LiteralPath $toolchain)) {
    throw 'UE 5.8 Linux v26 toolchain is not installed or LINUX_MULTIARCH_ROOT is missing.'
}
$toolchain = [IO.Path]::GetFullPath($toolchain)
$versionFile = Join-Path $toolchain 'ToolchainVersion.txt'
$toolchainVersion = (Get-Content -LiteralPath $versionFile -Raw).Trim()
if ($toolchainVersion -ne 'v26_clang-20.1.8-rockylinux8') {
    throw "Unexpected Linux toolchain: $toolchainVersion"
}
$env:LINUX_MULTIARCH_ROOT = $toolchain

$scratch = Join-Path $root 'Saved\StagedBuilds\LinuxServerArchive'
if (!$PostProcessOnly) {
    $runUat = Join-Path $engine 'Engine\Build\BatchFiles\RunUAT.bat'
    if (!(Test-Path -LiteralPath $runUat)) {
        throw "RunUAT.bat not found below $engine"
    }

    foreach ($path in @($scratch, $artifact)) {
        $resolved = [IO.Path]::GetFullPath($path)
        if ($resolved.StartsWith([IO.Path]::GetFullPath($root), [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null

    $arguments = @(
        'BuildCookRun',
        "-project=$project",
        '-noP4',
        '-utf8output',
        '-server',
        '-noclient',
        '-serverplatform=Linux',
        "-serverconfig=$Configuration",
        '-map=/Game/Maps/MahjongRoomMap',
        '-build',
        '-SkipCookingEditorContent',
        '-nodebuginfo',
        '-stage',
        '-pak',
        '-archive',
        "-archivedirectory=$scratch"
    )
    if ($SkipCook) { $arguments += '-skipcook' } else { $arguments += '-cook' }

    & $runUat @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "LinuxServer BuildCookRun failed with exit code $LASTEXITCODE"
    }
} elseif (!(Test-Path -LiteralPath $scratch)) {
    throw "PostProcessOnly requires an existing archive below $scratch"
}

$packageRoot = Join-Path $scratch 'LinuxServer'
if (!(Test-Path -LiteralPath $packageRoot)) { $packageRoot = $scratch }
$serverContainer = Get-ChildItem -LiteralPath $packageRoot -Recurse -File `
    -Filter 'GuiyangMahjong-LinuxServer.utoc' | Select-Object -First 1
if ($null -eq $serverContainer) {
    throw "LinuxServer IoStore container was not found below $packageRoot"
}
$unrealPak = Join-Path $engine 'Engine\Binaries\Win64\UnrealPak.exe'
$containerListing = @(& $unrealPak $serverContainer.FullName -List 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw "Could not inspect LinuxServer IoStore container (exit code $LASTEXITCODE)."
}
foreach ($forbiddenPath in @(
    'GuiyangMahjong/Content/Client/',
    'GuiyangMahjong/Content/UI/',
    'GuiyangMahjong/Content/Art/',
    'MahjongRoomVisualPreviewMap')) {
    if ($containerListing -match [regex]::Escape($forbiddenPath)) {
        throw "LinuxServer content isolation failed: $forbiddenPath is packaged."
    }
}
if (!($containerListing -match 'GuiyangMahjong/Content/Maps/MahjongRoomMap.umap')) {
    throw 'LinuxServer content isolation failed: MahjongRoomMap is absent.'
}
Write-Host 'LINUX_SERVER_CONTENT_ISOLATION_OK'

$serverBinary = Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter 'GuiyangMahjongServer*' |
    Where-Object {
        $_.Name -notmatch '\.(debug|sym|target)$' -and
        $_.FullName -match '[\\/]Binaries[\\/]Linux[\\/]GuiyangMahjongServer(?:-Linux-(?:Development|Shipping))?$'
    } |
    Select-Object -First 1
if ($null -eq $serverBinary) {
    throw "Packaged GuiyangMahjongServer ELF was not found below $packageRoot"
}

if (Test-Path -LiteralPath $artifact) {
    Remove-Item -LiteralPath $artifact -Recurse -Force
}
New-Item -ItemType Directory -Path $artifact -Force | Out-Null
if ($IncludeDebugSymbols) {
    Copy-Item -Path (Join-Path $packageRoot '*') -Destination $artifact -Recurse -Force
} else {
    & robocopy.exe $packageRoot $artifact /E /XF '*.debug' '*.sym' 'Manifest_DebugFiles_*.txt' /NFL /NDL /NJH /NJS /NP
    if ($LASTEXITCODE -gt 7) {
        throw "Could not copy the runtime-only LinuxServer artifact (robocopy exit code $LASTEXITCODE)."
    }
}
$packagePrefix = $packageRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$sourceRelativeBinary = $serverBinary.FullName.Substring($packagePrefix.Length).Replace('\', '/')
$relativeBinary = 'GuiyangMahjong/Binaries/Linux/GuiyangMahjongServer'
$artifactBinary = Join-Path $artifact ($relativeBinary.Replace('/', [IO.Path]::DirectorySeparatorChar))
$copiedBinary = Join-Path $artifact ($sourceRelativeBinary.Replace('/', [IO.Path]::DirectorySeparatorChar))
if (![IO.Path]::GetFullPath($copiedBinary).Equals(
        [IO.Path]::GetFullPath($artifactBinary), [StringComparison]::OrdinalIgnoreCase)) {
    Move-Item -LiteralPath $copiedBinary -Destination $artifactBinary -Force
}
$launcher = Join-Path $artifact 'GuiyangMahjongServer.sh'
if (Test-Path -LiteralPath $launcher) {
    $launcherText = Get-Content -LiteralPath $launcher -Raw
    $launcherText = $launcherText.Replace($serverBinary.Name, 'GuiyangMahjongServer')
    [IO.File]::WriteAllText($launcher, $launcherText, [Text.UTF8Encoding]::new($false))
}
$binaryHash = (Get-FileHash -LiteralPath $artifactBinary -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumLine = "$binaryHash  $relativeBinary"
$checksumLine | Set-Content -LiteralPath (Join-Path $artifact 'SHA256SUMS') -Encoding ascii
$commit = (git -C $root rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0) { $commit = 'unknown' }
$manifest = [ordered]@{
    schemaVersion = 1
    target = 'GuiyangMahjongServer'
    platform = 'Linux'
    configuration = $Configuration
    toolchain = $toolchainVersion
    gitCommit = $commit
    executable = $relativeBinary
    executableSha256 = $binaryHash
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifact 'build-manifest.json') -Encoding utf8

$serverReceipt = Join-Path $root "Binaries\Linux\GuiyangMahjongServer-Linux-$Configuration.target"
if (!(Test-Path -LiteralPath $serverReceipt)) {
    $serverReceipt = Get-ChildItem -LiteralPath (Join-Path $root 'Binaries\Linux') `
        -Filter 'GuiyangMahjongServer*.target' -File -ErrorAction SilentlyContinue |
        Where-Object {
            try {
                $candidateReceipt = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
                $candidateReceipt.Platform -eq 'Linux' -and
                    $candidateReceipt.Configuration -eq $Configuration -and
                    $candidateReceipt.TargetType -eq 'Server'
            } catch {
                $false
            }
        } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if ([string]::IsNullOrWhiteSpace($serverReceipt)) {
    throw "No Linux $Configuration server receipt was produced below Binaries\Linux"
}
& (Join-Path $PSScriptRoot 'Test-PackageIsolation.ps1') -Root $root -Role Server `
    -ServerReceipt $serverReceipt

Write-Host "LINUX_SERVER_BUILD_OK artifact=$artifact executable=$relativeBinary toolchain=$toolchainVersion"
