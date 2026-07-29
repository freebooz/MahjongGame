[CmdletBinding()]
param(
    [string]$PrometheusUrl = "http://127.0.0.1:19090",
    [switch]$ContractOnly
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$rules = Get-Content -Raw -Encoding UTF8 -LiteralPath (
    Join-Path $root "Deploy/observability/rules/mahjong-slo.yaml")
foreach ($pattern in @(
    "admin_api_error_budget_remaining",
    "admin_api_burn_rate_1h",
    "telemetry_freshness_p95_seconds",
    "admin_command_start_p95_seconds",
    "audit_worm_latency_p95_seconds",
    "governance: release-block")) {
    if ($rules -notmatch [regex]::Escape($pattern)) {
        throw "SLO release gate contract failed: missing $pattern"
    }
}
if ($ContractOnly) {
    Write-Host "SLO recording, burn-rate, and release policy contracts passed."
    exit 0
}

# 发布时直接读取记录规则；查询缺失也视为失败，避免“没有指标等于健康”。 Fail closed.
function Query-Scalar([string]$expression) {
    $encoded = [uri]::EscapeDataString($expression)
    $response = Invoke-RestMethod -Uri (
        "$($PrometheusUrl.TrimEnd('/'))/api/v1/query?query=$encoded") -TimeoutSec 10
    if ($response.status -ne "success" -or $response.data.result.Count -ne 1) {
        throw "SLO release gate has no unique value for: $expression"
    }
    return [double]$response.data.result[0].value[1]
}

$budget = Query-Scalar "mahjong:slo:admin_api_error_budget_remaining"
$fastBurn = Query-Scalar "max(mahjong:slo:admin_api_burn_rate_1h)"
if ($budget -le 0 -or $fastBurn -gt 14.4) {
    throw "Release blocked by SLO policy: budget=$budget fastBurn=$fastBurn"
}
Write-Host "SLO release gate passed: budget=$budget fastBurn=$fastBurn"
