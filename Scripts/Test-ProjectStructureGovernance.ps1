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
    'Services/Schema/ServiceSchemaPath.cs',
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

# 已完成迁移的旧入口不得重新出现；该清单把本轮目录与命名决策固化为可执行契约，
# 防止后续合并把共享源码、Angular 功能文件或混合领域测试放回模糊位置。
$deprecatedStructureFiles = @(
    'Services/Build/ServiceSchemaPath.cs',
    'Services/GuiyangMahjong.Admin/ClientApp/src/app/admin-console-core.ts',
    'Services/GuiyangMahjong.Admin/ClientApp/src/app/admin-console-dashboard.ts',
    'Services/GuiyangMahjong.Admin/ClientApp/src/app/admin-console-management.ts',
    'Services/GuiyangMahjong.Admin/ClientApp/src/app/admin-console-realtime.ts',
    'Services/GuiyangMahjong.Admin/ClientApp/src/app/admin-console.ts',
    'Services/GuiyangMahjong.Lobby/Services/IdempotencyStores.cs',
    'Services/GuiyangMahjong.Lobby/Services/PresenceServices.cs',
    'Services/GuiyangMahjong.Lobby/Services/PlayerAccessRevocationStores.cs',
    'Source/GuiyangMahjongEditorTools/Private/Tests/MahjongDeckAndPresentationTests.cpp',
    'Source/GuiyangMahjongEditorTools/Private/Tests/MahjongRulesAndPersistenceTests.cpp'
)
foreach ($relative in $deprecatedStructureFiles) {
    if (Test-Path -LiteralPath (Join-Path $projectRoot $relative)) {
        Add-GovernanceFailure "已废弃的目录或文件命名重新出现：$relative"
    }
}

$requiredNormalizedFiles = @(
    'Services/GuiyangMahjong.Admin/ClientApp/src/app/admin-console/admin-console-state.ts',
    'Services/GuiyangMahjong.Admin/ClientApp/src/app/admin-console/admin-console-dashboard.ts',
    'Services/GuiyangMahjong.Admin/ClientApp/src/app/admin-console/admin-console-management.ts',
    'Services/GuiyangMahjong.Admin/ClientApp/src/app/admin-console/admin-console-realtime.ts',
    'Services/GuiyangMahjong.Admin/ClientApp/src/app/admin-console/admin-console.ts',
    'Services/GuiyangMahjong.Admin/Api/PlayerEvidenceEndpoints.cs',
    'Services/GuiyangMahjong.Admin/Api/PlayerEvidenceEndpoints.Common.cs',
    'Services/GuiyangMahjong.Admin/Api/PlayerEvidenceEndpoints.Ingestion.cs',
    'Services/GuiyangMahjong.Admin/Api/PlayerEvidenceEndpoints.Queries.cs',
    'Services/GuiyangMahjong.Admin/Api/PlayerEvidenceEndpoints.Replays.cs',
    'Services/GuiyangMahjong.Admin/Api/PlayerEvidenceEndpoints.Chat.cs',
    'Services/GuiyangMahjong.Admin/Api/PlayerEvidenceEndpoints.GmOperations.cs',
    'Services/GuiyangMahjong.Lobby/Api/LobbyEndpoints.cs',
    'Services/GuiyangMahjong.Lobby/Api/LobbyEndpoints.Common.cs',
    'Services/GuiyangMahjong.Lobby/Api/LobbyEndpoints.Health.cs',
    'Services/GuiyangMahjong.Lobby/Api/LobbyEndpoints.Internal.cs',
    'Services/GuiyangMahjong.Lobby/Api/LobbyEndpoints.Monitoring.cs',
    'Services/GuiyangMahjong.Lobby/Api/LobbyEndpoints.Public.cs',
    'Services/GuiyangMahjong.Lobby/Services/IdempotentHttpResponse.cs',
    'Services/GuiyangMahjong.Lobby/Services/IIdempotencyStore.cs',
    'Services/GuiyangMahjong.Lobby/Services/InMemoryIdempotencyStore.cs',
    'Services/GuiyangMahjong.Lobby/Services/RedisIdempotencyStore.cs',
    'Services/GuiyangMahjong.Lobby/Services/IOnlinePresenceService.cs',
    'Services/GuiyangMahjong.Lobby/Services/InMemoryOnlinePresenceService.cs',
    'Services/GuiyangMahjong.Lobby/Services/RedisOnlinePresenceService.cs',
    'Services/GuiyangMahjong.Lobby/Services/IPlayerAccessRevocationStore.cs',
    'Services/GuiyangMahjong.Lobby/Services/InMemoryPlayerAccessRevocationStore.cs',
    'Services/GuiyangMahjong.Lobby/Services/RedisPlayerAccessRevocationStore.cs',
    'Source/GuiyangMahjongEditorTools/Private/Tests/MahjongDeckAndRuleSnapshotTests.cpp',
    'Source/GuiyangMahjongEditorTools/Private/Tests/MahjongPresentationTests.cpp',
    'Source/GuiyangMahjongEditorTools/Private/Tests/MahjongRuleTests.cpp',
    'Source/GuiyangMahjongEditorTools/Private/Tests/MahjongClientPersistenceTests.cpp'
)
foreach ($relative in $requiredNormalizedFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $projectRoot $relative) -PathType Leaf)) {
        Add-GovernanceFailure "缺少已归一化的结构入口：$relative"
    }
}

# 玩家证据接口已按安全域拆分；限制每个 partial 文件规模，防止后续功能继续堆回单一入口。
# 350 行覆盖当前公共校验分区并保留小幅演进空间，超过阈值必须创建职责明确的新分区。
$playerEvidenceFiles = @(
    Get-ChildItem -LiteralPath (
        Join-Path $projectRoot 'Services/GuiyangMahjong.Admin/Api') `
        -File `
        -Filter 'PlayerEvidenceEndpoints*.cs'
)
foreach ($file in $playerEvidenceFiles) {
    $lineCount = @(Get-Content -LiteralPath $file.FullName).Count
    if ($lineCount -gt 350) {
        $relative = [IO.Path]::GetRelativePath(
            $projectRoot,
            $file.FullName).Replace('\', '/')
        Add-GovernanceFailure (
            "玩家证据端点分区超过 350 行：$relative lines=$lineCount")
    }
}

# Lobby API 按健康、内部写、只读监控和玩家公开域拆分；每个分区保留少量增长空间，
# 超过 350 行时必须继续下沉到更窄的业务能力，禁止恢复单文件路由聚合。
$lobbyEndpointFiles = @(
    Get-ChildItem -LiteralPath (
        Join-Path $projectRoot 'Services/GuiyangMahjong.Lobby/Api') `
        -File `
        -Filter 'LobbyEndpoints*.cs'
)
foreach ($file in $lobbyEndpointFiles) {
    $lineCount = @(Get-Content -LiteralPath $file.FullName).Count
    if ($lineCount -gt 350) {
        $relative = [IO.Path]::GetRelativePath(
            $projectRoot,
            $file.FullName).Replace('\', '/')
        Add-GovernanceFailure (
            "Lobby 端点分区超过 350 行：$relative lines=$lineCount")
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

# 需要中文审查的人工维护代码必须至少包含一个中文维护说明，先阻止新文件完全脱离项目语境。
# 按项目约定，Python 与 PowerShell 属于工具脚本，不要求中文注释，因此不纳入该语言门禁。
# 该门禁是最低基线，不替代 AGENTS.md 对类型、成员、方法和关键分支的详细注释要求；
# 后续专项门禁会继续检查公共 API XML 文档和高风险异步/事务边界。
$maintainedCodeExtensions = @(
    '.cs', '.cpp', '.h', '.hpp', '.ts', '.sh', '.cshtml')
$maintainedCodeExclusions = @(
    '/Binaries/',
    '/Intermediate/',
    '/Saved/',
    '/DerivedDataCache/',
    '/node_modules/',
    '/ThirdParty/',
    '/wwwroot/')
foreach ($relative in $trackedExistingFiles) {
    $normalized = "/$($relative.Replace('\', '/'))"
    $extension = [IO.Path]::GetExtension($relative).ToLowerInvariant()
    $isExcluded = @(
        $maintainedCodeExclusions |
            Where-Object {
                $normalized.IndexOf(
                    $_,
                    [StringComparison]::OrdinalIgnoreCase) -ge 0
            }
    ).Count -gt 0
    if ($extension -notin $maintainedCodeExtensions -or $isExcluded) {
        continue
    }
    $path = Join-Path $projectRoot $relative
    if (!(Select-String `
            -LiteralPath $path `
            -Pattern '[\p{IsCJKUnifiedIdeographs}]' `
            -Quiet)) {
        Add-GovernanceFailure "人工维护代码缺少中文说明：$relative"
    }
}

# 生产 C# 的公共契约需要在声明附近说明职责和约束，防止仅依赖文件头注释造成维护误判。
# 这是有意保守的近似检查：覆盖公共/内部类型及公共方法，向上查看四行以兼容 XML 文档和特性；
# 测试项目允许使用测试名称表达场景，因此不纳入该生产 API 门禁。
$csharpDeclarationPattern =
    '^\s*(?:public|internal)\s+(?:(?:static|sealed|abstract|partial|readonly)\s+)*(?:class|record|struct|interface|enum)\b' `
    + '|^\s*public\s+(?:(?:static|virtual|override|async|sealed|abstract|partial|new|required)\s+)*(?:[A-Za-z_][\w<>,?\[\]. ]*\s+)?[A-Za-z_]\w*\s*\('
foreach ($relative in $trackedExistingFiles) {
    $normalized = $relative.Replace('\', '/')
    if (!$normalized.EndsWith(
            '.cs',
            [StringComparison]::OrdinalIgnoreCase) `
        -or $normalized.IndexOf(
            '.Tests/',
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        continue
    }
    $path = Join-Path $projectRoot $relative
    $lines = @(Get-Content -LiteralPath $path -Encoding UTF8)
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -notmatch $csharpDeclarationPattern) {
            continue
        }
        $lookBehindStart = [Math]::Max(0, $index - 4)
        $lookBehind = if ($index -eq 0) {
            ''
        }
        else {
            $lines[$lookBehindStart..($index - 1)] -join "`n"
        }
        if ($lookBehind -notmatch '///' `
            -and $lookBehind -notmatch '[\p{IsCJKUnifiedIdeographs}]') {
            Add-GovernanceFailure (
                "生产 C# 声明缺少就近职责说明：{0}:{1}" -f
                $relative,
                ($index + 1))
        }
    }
}

# Unreal 反射类型会进入 UHT、蓝图和序列化契约，必须在宏声明前说明职责、可见范围与关键约束。
# 仅扫描生产 Source 头文件并排除测试；八行窗口允许多行 Doxygen，但不接受仅在类型之后补说明。
$unrealReflectionDeclarationPattern =
    '^\s*U(?:CLASS|STRUCT|ENUM)\s*\('
foreach ($relative in $trackedExistingFiles) {
    $normalized = $relative.Replace('\', '/')
    $extension = [IO.Path]::GetExtension($relative).ToLowerInvariant()
    if (!$normalized.StartsWith(
            'Source/',
            [StringComparison]::OrdinalIgnoreCase) `
        -or $extension -notin @('.h', '.hpp') `
        -or $normalized.IndexOf(
            '/Tests/',
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        continue
    }
    $path = Join-Path $projectRoot $relative
    $lines = @(Get-Content -LiteralPath $path -Encoding UTF8)
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -notmatch $unrealReflectionDeclarationPattern) {
            continue
        }
        $lookBehindStart = [Math]::Max(0, $index - 8)
        $lookBehind = if ($index -eq 0) {
            ''
        }
        else {
            $lines[$lookBehindStart..($index - 1)] -join "`n"
        }
        if ($lookBehind -notmatch '[\p{IsCJKUnifiedIdeographs}]') {
            Add-GovernanceFailure (
                "Unreal 反射类型缺少宏前职责说明：{0}:{1}" -f
                $relative,
                ($index + 1))
        }
    }
}

# UFUNCTION 会暴露给蓝图或网络层，其中 Server/Client RPC 尤其需要明确权威边界、隐私和失败行为。
# 与反射类型使用同一生产头文件范围；连续的同职责处理器可以共享八行内的分组说明。
$unrealFunctionDeclarationPattern =
    '^\s*UFUNCTION\s*\('
foreach ($relative in $trackedExistingFiles) {
    $normalized = $relative.Replace('\', '/')
    $extension = [IO.Path]::GetExtension($relative).ToLowerInvariant()
    if (!$normalized.StartsWith(
            'Source/',
            [StringComparison]::OrdinalIgnoreCase) `
        -or $extension -notin @('.h', '.hpp') `
        -or $normalized.IndexOf(
            '/Tests/',
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        continue
    }
    $path = Join-Path $projectRoot $relative
    $lines = @(Get-Content -LiteralPath $path -Encoding UTF8)
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -notmatch $unrealFunctionDeclarationPattern) {
            continue
        }
        $lookBehindStart = [Math]::Max(0, $index - 8)
        $lookBehind = if ($index -eq 0) {
            ''
        }
        else {
            $lines[$lookBehindStart..($index - 1)] -join "`n"
        }
        if ($lookBehind -notmatch '[\p{IsCJKUnifiedIdeographs}]') {
            Add-GovernanceFailure (
                "Unreal UFUNCTION 缺少就近职责说明：{0}:{1}" -f
                $relative,
                ($index + 1))
        }
    }
}

# Unreal 生命周期与接口覆写常承担委托注册、资源释放、复制和权威请求转发，必须说明其副作用。
# 五行窗口允许同一生命周期对共享分组说明；测试覆写不纳入生产维护门禁。
$unrealOverrideDeclarationPattern =
    '^\s*virtual\s+.+\boverride\s*;\s*$'
foreach ($relative in $trackedExistingFiles) {
    $normalized = $relative.Replace('\', '/')
    $extension = [IO.Path]::GetExtension($relative).ToLowerInvariant()
    if (!$normalized.StartsWith(
            'Source/',
            [StringComparison]::OrdinalIgnoreCase) `
        -or $extension -notin @('.h', '.hpp') `
        -or $normalized.IndexOf(
            '/Tests/',
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        continue
    }
    $path = Join-Path $projectRoot $relative
    $lines = @(Get-Content -LiteralPath $path -Encoding UTF8)
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -notmatch $unrealOverrideDeclarationPattern) {
            continue
        }
        $lookBehindStart = [Math]::Max(0, $index - 5)
        $lookBehind = if ($index -eq 0) {
            ''
        }
        else {
            $lines[$lookBehindStart..($index - 1)] -join "`n"
        }
        if ($lookBehind -notmatch '[\p{IsCJKUnifiedIdeographs}]') {
            Add-GovernanceFailure (
                "Unreal override 缺少就近副作用说明：{0}:{1}" -f
                $relative,
                ($index + 1))
        }
    }
}

# Angular 生产源码的导出类型、函数及显式公共成员必须在声明附近说明职责、生命周期或失败边界。
# 向上查看十行是为了跨过 @Component 元数据，同时排除 spec 测试，让测试名称继续承担场景说明职责。
$typeScriptDeclarationPattern =
    '^\s*(?:export\s+)?(?:abstract\s+)?(?:class|interface|type|enum)\s+[A-Za-z_]\w*' `
    + '|^\s*(?:export\s+)?(?:async\s+)?function\s+[A-Za-z_]\w*' `
    + '|^\s*(?:public|protected)\s+(?:async\s+)?[A-Za-z_]\w*\s*\('
foreach ($relative in $trackedExistingFiles) {
    $normalized = $relative.Replace('\', '/')
    if (!$normalized.StartsWith(
            'Services/GuiyangMahjong.Admin/ClientApp/src/',
            [StringComparison]::OrdinalIgnoreCase) `
        -or !$normalized.EndsWith(
            '.ts',
            [StringComparison]::OrdinalIgnoreCase) `
        -or $normalized.EndsWith(
            '.spec.ts',
            [StringComparison]::OrdinalIgnoreCase)) {
        continue
    }
    $path = Join-Path $projectRoot $relative
    $lines = @(Get-Content -LiteralPath $path -Encoding UTF8)
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -notmatch $typeScriptDeclarationPattern) {
            continue
        }
        $lookBehindStart = [Math]::Max(0, $index - 10)
        $lookBehind = if ($index -eq 0) {
            ''
        }
        else {
            $lines[$lookBehindStart..($index - 1)] -join "`n"
        }
        if ($lookBehind -notmatch '[\p{IsCJKUnifiedIdeographs}]') {
            Add-GovernanceFailure (
                "Angular TypeScript 声明缺少就近职责说明：{0}:{1}" -f
                $relative,
                ($index + 1))
        }
    }
}

# 支持原生注释的人工配置必须直接包含中文职责或约束说明；严格 JSON 不在此注入非法注释，
# 而由核心架构文档的数据字典覆盖并通过下方登记项检查。
$commentableConfigExtensions = @(
    '.ini', '.yaml', '.yml', '.props', '.targets', '.csproj', '.slnx')
foreach ($relative in $trackedExistingFiles) {
    $normalized = $relative.Replace('\', '/')
    $extension = [IO.Path]::GetExtension($relative).ToLowerInvariant()
    $isCommentableConfig =
        $extension -in $commentableConfigExtensions `
        -or $normalized.EndsWith(
            '/Dockerfile',
            [StringComparison]::OrdinalIgnoreCase) `
        -or $normalized.EndsWith(
            '.env.example',
            [StringComparison]::OrdinalIgnoreCase)
    if (!$isCommentableConfig) {
        continue
    }
    $path = Join-Path $projectRoot $relative
    if (!(Select-String `
            -LiteralPath $path `
            -Pattern '[\p{IsCJKUnifiedIdeographs}]' `
            -Quiet)) {
        Add-GovernanceFailure "可注释配置缺少中文说明：$relative"
    }
}

$architectureDocumentPath =
    Join-Path $projectRoot 'Docs/FULL_APPLICATION_ARCHITECTURE.md'
# 文档统一以 UTF-8 保存；显式指定编码可避免 Windows PowerShell 5.1 按本地代码页读取，
# 从而把中文治理标识误判为缺失。
$architectureDocument = Get-Content -LiteralPath $architectureDocumentPath -Raw -Encoding UTF8
$strictConfigurationDictionaryTokens = @(
    '严格 JSON 与工具配置数据字典',
    'GuiyangMahjong.uproject',
    'Plugins/Agones/Agones.uplugin',
    'appsettings*.json',
    'Services/global.json',
    'ClientApp/angular.json',
    'ClientApp/package.json',
    'ClientApp/package-lock.json',
    'ClientApp/tsconfig.json',
    'ClientApp/proxy.conf.json',
    'player-access-token-v1.contract.json',
    'runtime-telemetry-v1.schema.json',
    'grafana/dashboards/*.json',
    'MahjongTableMobileProduction/*Manifest.json',
    'MahjongTableProduction/*Manifest.json',
    'ui_asset_inventory.json')
foreach ($token in $strictConfigurationDictionaryTokens) {
    if ($architectureDocument.IndexOf(
            $token,
            [StringComparison]::Ordinal) -lt 0) {
        Add-GovernanceFailure "严格配置数据字典缺少登记项：$token"
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
