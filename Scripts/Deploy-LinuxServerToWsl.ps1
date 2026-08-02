[CmdletBinding()]
param(
    [string]$EngineRoot = '',
    [ValidateSet('Development', 'Shipping')]
    [string]$Configuration = 'Development',
    [string]$Distribution = 'Ubuntu-22.04',
    [string]$WslUser = 'root',
    [string]$LinuxRepositoryPath = '',
    [string]$Version = '',
    [switch]$ReuseExistingBuild
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'lib\ProjectEnvironment.psm1') -Force
$root = Resolve-MahjongProjectRoot
$EngineRoot = Resolve-UnrealEngineRoot -ExplicitRoot $EngineRoot
# WSL 同步目标允许环境覆盖；通用默认值避免把个人用户名写入自动化入口。
if ([string]::IsNullOrWhiteSpace($LinuxRepositoryPath)) {
    if ($env:MAHJONG_LINUX_REPOSITORY_PATH) {
        $LinuxRepositoryPath = $env:MAHJONG_LINUX_REPOSITORY_PATH
    } else {
        $LinuxRepositoryPath = '/srv/guiyang-mahjong'
    }
}
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
    # Docker/BuildKit 会把正常构建进度写入 stderr。临时允许该输出通过，
    # 再由每个调用点根据 $LASTEXITCODE 统一判定失败，避免把进度误当异常中断部署。
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & wsl.exe @arguments
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
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
mkdir -p $(Quote-Bash "$LinuxRepositoryPath/Contracts") $(Quote-Bash "$LinuxRepositoryPath/Services") $(Quote-Bash "$LinuxRepositoryPath/Scripts/Linux") $(Quote-Bash "$LinuxRepositoryPath/Deploy/linux") $(Quote-Bash "$LinuxRepositoryPath/Deploy/nats") $(Quote-Bash "$LinuxRepositoryPath/Deploy/postgres")
rsync -a --delete $(Quote-Bash "$rootLinux/Contracts/") $(Quote-Bash "$LinuxRepositoryPath/Contracts/")
rsync -a --delete --exclude 'bin/' --exclude 'obj/' $(Quote-Bash "$rootLinux/Services/") $(Quote-Bash "$LinuxRepositoryPath/Services/")
rsync -a --delete $(Quote-Bash "$rootLinux/Scripts/Linux/") $(Quote-Bash "$LinuxRepositoryPath/Scripts/Linux/")
rsync -a --delete --exclude '.env' --exclude '.deployed-version' --exclude '.previous-version' $(Quote-Bash "$rootLinux/Deploy/linux/") $(Quote-Bash "$LinuxRepositoryPath/Deploy/linux/")
# NATS 和 PostgreSQL 权限脚本是 Compose 的只读挂载；缺失时 Docker 会创建错误目录并导致健康检查失败。
if [[ -d $(Quote-Bash "$LinuxRepositoryPath/Deploy/nats/local.conf") ]]; then
  if find $(Quote-Bash "$LinuxRepositoryPath/Deploy/nats/local.conf") -mindepth 1 -print -quit | grep -q .; then
    echo 'Refusing to replace non-empty directory at Deploy/nats/local.conf.' >&2
    exit 1
  fi
  rmdir $(Quote-Bash "$LinuxRepositoryPath/Deploy/nats/local.conf")
fi
rsync -a $(Quote-Bash "$rootLinux/Deploy/nats/local.conf") $(Quote-Bash "$LinuxRepositoryPath/Deploy/nats/local.conf")
rsync -a --delete $(Quote-Bash "$rootLinux/Deploy/postgres/") $(Quote-Bash "$LinuxRepositoryPath/Deploy/postgres/")
rsync -a $(Quote-Bash "$rootLinux/.dockerignore") $(Quote-Bash "$LinuxRepositoryPath/.dockerignore")
rsync -a --delete $(Quote-Bash "$artifactLinux/") $(Quote-Bash "$LinuxRepositoryPath/Artifacts/LinuxServer/")
cd $(Quote-Bash $LinuxRepositoryPath)
# 首次部署尚不存在 .env，必须让 deploy.sh 生成完整的安全配置；提前创建空文件会跳过密钥初始化。
# 已有部署才在升级前显式覆盖地图，确保新烹饪的 MahjongRoomMap 被独立服务器使用。
if [[ -f Deploy/linux/.env ]]; then
  sed -i '/^GAME_SERVER_MAP=/d' Deploy/linux/.env
  printf '%s\n' 'GAME_SERVER_MAP=/Game/Maps/MahjongRoomMap?game=/Script/GuiyangMahjongServer.GuiyangMahjongGameMode' >> Deploy/linux/.env
fi
./Deploy/linux/deploy.sh upgrade --refresh-advertised-ip --version $(Quote-Bash $Version)
"@
Invoke-WslBash $syncAndDeploy
if ($LASTEXITCODE -ne 0) { throw "Real UE LinuxServer deployment failed with exit code $LASTEXITCODE" }

Write-Host "UE_LINUX_DEPLOYMENT_OK version=$Version binary=$($manifest.executable) sha256=$actualHash"
