[CmdletBinding()]
param(
    [string]$Root = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'lib\ProjectEnvironment.psm1') -Force
$projectRoot = Resolve-MahjongProjectRoot -ExplicitRoot $Root
$failures = [Collections.Generic.List[string]]::new()

<#
.SYNOPSIS
记录结构门禁失败项。
.DESCRIPTION
集中收集问题后一次输出，方便开发者在单轮修改中处理全部目录违规。
#>
function Add-GovernanceFailure {
    param([Parameter(Mandatory)][string]$Message)
    $failures.Add($Message)
}

# 可重建产物即使仍在 Git 索引中，只要工作树已删除就视为本轮清理成功；
# 合并后的 CI 会通过同一检查阻止它们再次出现。
$trackedExistingFiles = @(
    & git -c core.quotepath=false -C $projectRoot ls-files |
        Where-Object {
            Test-Path -LiteralPath (Join-Path $projectRoot $_) -PathType Leaf
        }
)
if ($LASTEXITCODE -ne 0) {
    throw '无法读取 Git 跟踪文件，结构门禁不能给出可信结论。'
}

$forbiddenTrackedFiles = @(
    $trackedExistingFiles |
        Where-Object {
            $relative = $_.Replace('\', '/')
            $relative -match '(^|/)__pycache__/' -or
            $relative -match '\.py[co]$' -or
            $relative.StartsWith('Artifacts/WorkflowC/', [StringComparison]::Ordinal) -or
            ($relative.StartsWith(
                    'Services/GuiyangMahjong.Admin/wwwroot/',
                    [StringComparison]::Ordinal) -and
                $relative -ne 'Services/GuiyangMahjong.Admin/wwwroot/.gitkeep')
        }
)
foreach ($file in $forbiddenTrackedFiles) {
    Add-GovernanceFailure "发现被跟踪的可重建产物：$file"
}

$requiredFiles = @(
    'README.md',
    'Docs/README.md',
    'Scripts/README.md',
    'SourceArt/README.md',
    'Artifacts/README.md',
    'Evidence/README.md',
    'Contracts/README.md',
    'Services/Directory.Build.props',
    'Services/Directory.Packages.props',
    'Contracts/Authentication/player-access-token-v1.contract.json',
    'Contracts/Monitoring/runtime-telemetry-v1.schema.json',
    'Scripts/Linux/build-service-images.sh',
    '.github/CODEOWNERS'
)
foreach ($relative in $requiredFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $projectRoot $relative) -PathType Leaf)) {
        Add-GovernanceFailure "缺少结构治理入口：$relative"
    }
}

# Docs 只允许保留当前架构、监控、安全、UI 和运行手册；阶段流水账应由 Git/CI 证据追溯，
# 不再回流到解决方案工作树。白名单同时作为必需清单，避免清理时误删运维入口。
$coreDocs = @(
    'Docs/README.md',
    'Docs/FULL_APPLICATION_ARCHITECTURE.md',
    'Docs/REALTIME_SERVER_PLAYER_MONITORING_REVIEW_20260728.md',
    'Docs/PLAYER_MONITORING_ADMIN_DESIGN.md',
    'Docs/UI_ASSET_AND_VISUAL_STANDARD.md',
    'Docs/POSTGRES_LEAST_PRIVILEGE_AND_PRODUCTION_IDENTITY.md',
    'Docs/OBSERVABILITY_LOGGING_STANDARD.md',
    'Docs/RUNBOOKS/OBSERVABILITY_ALERTS.md',
    'Docs/RUNBOOKS/SLO_MULTI_CLUSTER_GOVERNANCE.md'
)
foreach ($relative in $coreDocs) {
    if (!(Test-Path -LiteralPath (Join-Path $projectRoot $relative) -PathType Leaf)) {
        Add-GovernanceFailure "缺少核心文档：$relative"
    }
}
$docsRoot = Join-Path $projectRoot 'Docs'
$projectRootPrefix =
    $projectRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
$unexpectedDocs = @(
    Get-ChildItem -LiteralPath $docsRoot -Recurse -File -Filter '*.md' |
        ForEach-Object {
            $_.FullName.Substring($projectRootPrefix.Length).Replace('\', '/')
        } |
        Where-Object { $_ -notin $coreDocs }
)
foreach ($relative in $unexpectedDocs) {
    Add-GovernanceFailure "Docs 出现未登记的非核心文档：$relative"
}

# claudedocs 曾用于临时阶段日志，现已由核心文档和版本历史替代；即使目录被重新创建，
# 其中任何 Markdown 都必须被门禁拒绝。
$legacyDocsRoot = Join-Path $projectRoot 'claudedocs'
if (Test-Path -LiteralPath $legacyDocsRoot -PathType Container) {
    $legacyDocs = @(Get-ChildItem -LiteralPath $legacyDocsRoot -Recurse -File -Filter '*.md')
    foreach ($file in $legacyDocs) {
        $relative = $file.FullName.Substring($projectRootPrefix.Length).Replace('\', '/')
        Add-GovernanceFailure "发现已废弃的 claudedocs 文档：$relative"
    }
}

$solutionPath = Join-Path $projectRoot 'Services/GuiyangMahjong.Services.slnx'
$solution = Get-Content -LiteralPath $solutionPath -Raw
if ($solution -notmatch 'GuiyangMahjong\.Observability/GuiyangMahjong\.Observability\.csproj') {
    Add-GovernanceFailure 'Observability 项目未纳入 Services 解决方案。'
}

# 仅检查本轮已迁移的活跃入口；历史脚本在后续分类迁移时逐批纳入，避免一次性破坏外部调用方。
$portableScripts = @(
    'Scripts/Build-Client.ps1',
    'Scripts/Build-LinuxServer.ps1',
    'Scripts/Build-Phase4Server.ps1',
    'Scripts/Deploy-LinuxServerToWsl.ps1',
    'Scripts/Wait-And-Deploy-LinuxServer.ps1',
    'Scripts/Test-TargetModuleGraph.ps1',
    'Scripts/RunFullMatchIntegration.ps1',
    'Scripts/RunReconnectIntegration.ps1',
    'Scripts/RunUIVisualReview.ps1',
    'Scripts/RunUIVisualReviewMatrix.ps1',
    'Scripts/TestUIFontReadiness.ps1',
    'Scripts/Diagnose-WslNetwork.ps1',
    'Scripts/Start-WslServerStack.ps1',
    'Scripts/RemoveMahjong50PhysicalOrphans.ps1'
)
$personalPathPattern =
    '(?i)([A-Z]:[\\/](MahjongGame|UnrealEngine)|/home/(freebooz|administrator)/)'
foreach ($relative in $portableScripts) {
    $path = Join-Path $projectRoot $relative
    $match = Select-String -LiteralPath $path -Pattern $personalPathPattern
    if ($match) {
        Add-GovernanceFailure "活跃脚本仍含本机专属路径：$relative"
    }
}

# 冲突标记必须在进入编译器、Blender 或 Unreal Editor 之前被拒绝；只扫描已跟踪且仍存在的代码文件，
# 避免构建缓存、日志和文档示例造成误报，同时覆盖本轮暴露问题的 Python 资产脚本。
$mergeMarkerExtensions = @('.cs', '.cpp', '.h', '.py', '.ps1', '.psm1', '.ts')
$mergeMarkerPattern = '^(<<<<<<< .+|=======|>>>>>>> .+)$'
foreach ($relative in $trackedExistingFiles) {
    if ([IO.Path]::GetExtension($relative) -notin $mergeMarkerExtensions) {
        continue
    }
    $path = Join-Path $projectRoot $relative
    if (Select-String -LiteralPath $path -Pattern $mergeMarkerPattern -Quiet) {
        Add-GovernanceFailure "代码文件仍含 Git 合并冲突标记：$relative"
    }
}

# 测试若通过 extern alias 引用另一生产服务，内部重构就会污染契约验证边界。
# 本门禁要求跨服务兼容性由 Contracts 下的机器契约或进程边界测试承担。
$testProjectFiles = @(
    $trackedExistingFiles |
        Where-Object {
            $_.Replace('\', '/') -match
                '^Services/GuiyangMahjong\.[^/]+\.Tests/.*\.csproj$'
        }
)
foreach ($relative in $testProjectFiles) {
    $path = Join-Path $projectRoot $relative
    if (Select-String -LiteralPath $path -Pattern '<Aliases>' -Quiet) {
        Add-GovernanceFailure "测试项目仍使用生产程序集别名：$relative"
    }
}
$testSourceFiles = @(
    $trackedExistingFiles |
        Where-Object {
            $_.Replace('\', '/') -match
                '^Services/GuiyangMahjong\.[^/]+\.Tests/.*\.cs$'
        }
)
foreach ($relative in $testSourceFiles) {
    $path = Join-Path $projectRoot $relative
    if (Select-String -LiteralPath $path -Pattern '^\s*extern\s+alias\s+' -Quiet) {
        Add-GovernanceFailure "测试源码仍使用 extern alias：$relative"
    }
}

# Docker 矩阵的本地入口和 CI 矩阵必须同时存在，防止只在单台开发机验证集中 props。
$servicesWorkflowPath = Join-Path $projectRoot '.github/workflows/services-ci.yml'
$servicesWorkflow = Get-Content -LiteralPath $servicesWorkflowPath -Raw
foreach ($requiredToken in @('docker-build-matrix:', 'allocator-build',
        'Services/GuiyangMahjong.Admin/Dockerfile')) {
    if ($servicesWorkflow.IndexOf($requiredToken, [StringComparison]::Ordinal) -lt 0) {
        Add-GovernanceFailure "Services CI 缺少 Docker 构建矩阵标识：$requiredToken"
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "PROJECT_STRUCTURE_GOVERNANCE_FAILED count=$($failures.Count)"
}

Write-Host (
    "PROJECT_STRUCTURE_GOVERNANCE_OK trackedFiles={0} portableScripts={1} testProjects={2} coreDocs={3}" -f
    $trackedExistingFiles.Count,
    $portableScripts.Count,
    $testProjectFiles.Count,
    $coreDocs.Count)
