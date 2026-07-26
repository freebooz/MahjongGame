[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:\UnrealEngine-5.8.0-release',
    [ValidateSet('Development', 'Shipping')]
    [string]$Configuration = 'Development',
    [string]$Distribution = 'Ubuntu',
    [string]$WslUser = 'root',
    [string]$LinuxRepositoryPath = '/home/administrator/src/MahjongGame',
    [string]$Version = '',
    [switch]$ReuseExistingBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifact = Join-Path $root 'Artifacts\LinuxServer'
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = 'ue-linux-{0}' -f [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
}
if ($Version -notmatch '^[A-Za-z0-9._-]+$') { throw "Invalid deployment version: $Version" }
if (!$LinuxRepositoryPath.StartsWith('/') -or $LinuxRepositoryPath -eq '/') {
    throw 'LinuxRepositoryPath must be a non-root absolute Linux path.'
}

function Quote-Bash([string]$Value) {
    if ($Value.Contains("'")) { throw 'Single quotes are not supported in Linux deployment paths.' }
    return "'$Value'"
}

function Convert-ToWslPath([string]$WindowsPath) {
    $resolved = (Resolve-Path -LiteralPath $WindowsPath).Path
    if ($resolved -notmatch '^([A-Za-z]):\\(.*)$') {
        throw "Only drive-qualified Windows paths can be translated to WSL: $resolved"
    }

    $drive = $Matches[1].ToLowerInvariant()
    $relative = $Matches[2].Replace('\', '/')
    return "/mnt/$drive/$relative"
}

function Invoke-WslBash([string]$Command) {
    $Command = $Command.Replace("`r`n", "`n").Replace("`r", "`n")
    $arguments = @('-d', $Distribution)
    if (![string]::IsNullOrWhiteSpace($WslUser)) {
        $arguments += @('-u', $WslUser)
    }
    $arguments += @('--', 'bash', '-lc', $Command)
    & wsl.exe @arguments
}

$buildArguments = @{
    EngineRoot = $EngineRoot
    Configuration = $Configuration
}
if ($ReuseExistingBuild) {
    $buildArguments.PostProcessOnly = $true
}
& (Join-Path $PSScriptRoot 'Build-LinuxServer.ps1') @buildArguments
if ($LASTEXITCODE -ne 0) { throw "LinuxServer post-processing failed with exit code $LASTEXITCODE" }

$manifestPath = Join-Path $artifact 'build-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
$binary = Join-Path $artifact ($manifest.executable.Replace('/', [IO.Path]::DirectorySeparatorChar))
$actualHash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $manifest.executableSha256) { throw 'LinuxServer artifact checksum validation failed.' }

$artifactLinux = Convert-ToWslPath $artifact
$rootLinux = Convert-ToWslPath $root
$linuxBinary = "$artifactLinux/$($manifest.executable)"
$inspect = @"
set -e
file $(Quote-Bash $linuxBinary)
readelf -h $(Quote-Bash $linuxBinary) | grep -E 'Class:.*ELF64|Machine:.*X86-64'
"@
Invoke-WslBash $inspect
if ($LASTEXITCODE -ne 0) { throw 'LinuxServer ELF inspection failed.' }

$syncAndDeploy = @"
set -Eeuo pipefail
mkdir -p $(Quote-Bash "$LinuxRepositoryPath/Artifacts/LinuxServer")
mkdir -p $(Quote-Bash "$LinuxRepositoryPath/Contracts") $(Quote-Bash "$LinuxRepositoryPath/Services") $(Quote-Bash "$LinuxRepositoryPath/Scripts/Linux") $(Quote-Bash "$LinuxRepositoryPath/Deploy/linux")
rsync -a --delete $(Quote-Bash "$rootLinux/Contracts/") $(Quote-Bash "$LinuxRepositoryPath/Contracts/")
rsync -a --delete --exclude 'bin/' --exclude 'obj/' $(Quote-Bash "$rootLinux/Services/") $(Quote-Bash "$LinuxRepositoryPath/Services/")
rsync -a --delete $(Quote-Bash "$rootLinux/Scripts/Linux/") $(Quote-Bash "$LinuxRepositoryPath/Scripts/Linux/")
rsync -a --delete --exclude '.env' --exclude '.deployed-version' --exclude '.previous-version' $(Quote-Bash "$rootLinux/Deploy/linux/") $(Quote-Bash "$LinuxRepositoryPath/Deploy/linux/")
rsync -a $(Quote-Bash "$rootLinux/.dockerignore") $(Quote-Bash "$LinuxRepositoryPath/.dockerignore")
rsync -a --delete $(Quote-Bash "$artifactLinux/") $(Quote-Bash "$LinuxRepositoryPath/Artifacts/LinuxServer/")
cd $(Quote-Bash $LinuxRepositoryPath)
sed -i '/^GAME_SERVER_MAP=/d' Deploy/linux/.env
printf '%s\n' 'GAME_SERVER_MAP=/Game/Maps/MahjongRoomMap?game=/Script/GuiyangMahjongServer.GuiyangMahjongGameMode' >> Deploy/linux/.env
./Deploy/linux/deploy.sh upgrade --refresh-advertised-ip --version $(Quote-Bash $Version)
"@
Invoke-WslBash $syncAndDeploy
if ($LASTEXITCODE -ne 0) { throw "Real UE LinuxServer deployment failed with exit code $LASTEXITCODE" }

Write-Host "UE_LINUX_DEPLOYMENT_OK version=$Version binary=$($manifest.executable) sha256=$actualHash"
