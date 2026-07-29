[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot

# 检查旧巨页路径是否重新出现
$forbidden = @(
    @{
        Path = "Services/GuiyangMahjong.Admin/Services/MonitoringClients.cs"
        Pattern = "limit=5000"
    },
    @{
        Path = "Services/GuiyangMahjong.Admin/Services/PlayerMonitoringServices.cs"
        Pattern = "limit=2000"
    }
)

foreach ($rule in $forbidden) {
    $target = Join-Path $projectRoot $rule.Path
    if (Select-String -LiteralPath $target -Pattern $rule.Pattern -Quiet) {
        throw "Capacity contract failed: $($rule.Path) contains $($rule.Pattern)"
    }
}

# 检查分页上限和断线续传关键契约
$requiredPatterns = @(
    "MaximumPageSize",
    "EventBacklogLimit",
    "SubscriberQueueLimit",
    "PlayerPagesPerSnapshotCycle",
    "ListRoomsPageAsync",
    "WrapOpaqueCursor",
    "SseEnabled",
    "Last-Event-ID",
    "EVENT_WINDOW_EXCEEDED"
)
$contractFiles = @(
    (Join-Path $projectRoot "Services/GuiyangMahjong.Admin/Options/AdminOptions.cs"),
    (Join-Path $projectRoot "Services/GuiyangMahjong.Admin/Api/AdminEndpoints.cs"),
    (Join-Path $projectRoot "Services/GuiyangMahjong.Admin/Services/MonitoringClients.cs"),
    (Join-Path $projectRoot "Services/GuiyangMahjong.Admin/Domain/PaginationModels.cs"),
    (Join-Path $projectRoot "Services/GuiyangMahjong.Admin/ClientApp/src/app/admin-console.ts")
)
$combined = ($contractFiles | ForEach-Object {
    Get-Content -Raw -LiteralPath $_
}) -join "`n"

foreach ($pattern in $requiredPatterns) {
    if ($combined -notmatch [regex]::Escape($pattern)) {
        throw "Capacity contract failed: missing $pattern"
    }
}

Write-Host "Pagination, SSE resume, and capacity guard contracts passed."
