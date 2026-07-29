[CmdletBinding()]
param(
    # CI 的独立部署校验步骤会执行 docker compose config，此开关避免重复拉长测试时间。
    [switch]$SkipDockerValidation
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-ContainsText {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string[]]$Values
    )

    # 使用 UTF-8 一次性读取契约文件，确保中文注释和字段名在 Windows/Linux CI 上解释一致。
    $content = Get-Content -LiteralPath $Path -Raw -Encoding utf8
    foreach ($value in $Values) {
        if ($content.IndexOf($value, [StringComparison]::Ordinal) -lt 0) {
            $failures.Add("$Path 缺少契约值：$value")
        }
    }
}

$requiredLogFields = @(
    "Timestamp", "Level", "Service", "Environment", "TraceId",
    "RoomId", "PlayerId", "MatchId", "ServerInstanceId", "EventId"
)
$contractPath = Join-Path $repoRoot "Services/GuiyangMahjong.Observability/StructuredLogContract.cs"
$unrealLogPath = Join-Path $repoRoot "Source/GuiyangMahjongServer/Private/Server/GuiyangGameServerBridge.cpp"
Assert-ContainsText -Path $contractPath -Values $requiredLogFields
Assert-ContainsText -Path $unrealLogPath -Values $requiredLogFields

$alertPath = Join-Path $repoRoot "Deploy/observability/rules/mahjong-alerts.yaml"
Assert-ContainsText -Path $alertPath -Values @(
    "MahjongHeartbeatMissing",
    "MahjongDedicatedServerCpuHigh",
    "MahjongDedicatedServerMemoryHigh",
    "MahjongDedicatedServerTickSlow",
    "MahjongDisconnectRateHigh",
    "MahjongRpcStorm",
    "MahjongServiceErrorRateHigh",
    "MahjongAdminCommandBacklogSuspected",
    "MahjongAuditArchiveBacklogSuspected"
)

$collectorPath = Join-Path $repoRoot "Deploy/observability/otel-collector.yaml"
Assert-ContainsText -Path $collectorPath -Values @(
    "attributes/sensitive",
    "transform/redaction",
    "otlphttp/loki",
    "otlp/tempo",
    "prometheus"
)

$dashboardRoot = Join-Path $repoRoot "Deploy/observability/grafana/dashboards"
$expectedDashboards = @(
    "admin.json",
    "room-cluster.json",
    "dedicated-server.json",
    "approvals.json"
)
foreach ($dashboard in $expectedDashboards) {
    $dashboardPath = Join-Path $dashboardRoot $dashboard
    try {
        $document = Get-Content -LiteralPath $dashboardPath -Raw -Encoding utf8 | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace($document.uid) -or
            [string]::IsNullOrWhiteSpace($document.title) -or
            $document.panels.Count -lt 1) {
            $failures.Add("$dashboardPath 缺少 uid、title 或面板。")
        }
    }
    catch {
        $failures.Add("$dashboardPath 不是有效 JSON：$($_.Exception.Message)")
    }
}

if (-not $SkipDockerValidation) {
    $previousGrafanaPassword = $env:GRAFANA_ADMIN_PASSWORD
    $previousLokiToken = $env:LOKI_QUERY_TOKEN
    try {
        # 仅解析 Compose，不连接或修改 Docker daemon；真实启动由部署流程显式执行。
        $env:GRAFANA_ADMIN_PASSWORD = "contract-grafana-password-00000001"
        $env:LOKI_QUERY_TOKEN = "contract-loki-query-token-0000000001"
        & docker compose -f (Join-Path $repoRoot "Deploy/observability/compose.yaml") config --quiet
        if ($LASTEXITCODE -ne 0) {
            $failures.Add("Docker Compose 可观测性配置解析失败。")
        }
    }
    catch {
        $failures.Add("无法执行 Docker Compose 配置校验：$($_.Exception.Message)")
    }
    finally {
        $env:GRAFANA_ADMIN_PASSWORD = $previousGrafanaPassword
        $env:LOKI_QUERY_TOKEN = $previousLokiToken
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "工作流 D 可观测性契约门禁通过。"
