[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Require-Pattern {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string[]]$Patterns
    )
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Governance contract file is missing: $Path"
    }
    # Windows PowerShell 5 默认按系统代码页读取无 BOM 文件，显式 UTF-8 可避免中文相邻的 ASCII 契约词被误解码。 UTF-8 only.
    $content = [System.IO.File]::ReadAllText(
        $Path,
        [System.Text.Encoding]::UTF8)
    foreach ($pattern in $Patterns) {
        $missing = [string]::IsNullOrEmpty($pattern) -or
            $content.IndexOf($pattern, [StringComparison]::Ordinal) -lt 0
        if ($missing) {
            throw "Governance contract failed: $Path does not contain $pattern"
        }
    }
}

Require-Pattern -Path (
    Join-Path $root "Contracts/Monitoring/slo-v1.yaml") -Patterns @(
        "objective: 0.999",
        "telemetry-freshness",
        "admin-command-start",
        "audit-worm-latency",
        "heartbeat-loss")
Require-Pattern -Path (
    Join-Path $root "Contracts/Monitoring/governance-drills-v1.yaml") -Patterns @(
        "region-registration-expiry",
        "postgres-primary-failover",
        "worm-endpoint-unavailable",
        "audit-chain-tamper",
        "no-duplicate-admin-command")
Require-Pattern -Path (
    Join-Path $root "Services/GuiyangMahjong.Admin/Security/AdminAbacPolicyService.cs") -Patterns @(
        "RequireRegion",
        "RequireCase",
        "RequireCompensationApproval",
        "X-Break-Glass-Reason")
Require-Pattern -Path (
    Join-Path $root "Services/GuiyangMahjong.Admin/Services/TopologyRegistry.cs") -Patterns @(
        "Generation",
        "Conflict",
        "Expired")
Require-Pattern -Path (
    Join-Path $root "Services/GuiyangMahjong.Admin/Services/AuditChainAnchorService.cs") -Patterns @(
        "PreviousHash",
        "RecordHash",
        "Idempotency-Key")
Require-Pattern -Path (
    Join-Path $root "Docs/RUNBOOKS/SLO_MULTI_CLUSTER_GOVERNANCE.md") -Patterns @(
        "RTO",
        "RPO",
        "Break-glass",
        "WORM/SIEM")

& (Join-Path $root "Scripts/Test-SloReleaseGate.ps1") -ContractOnly
if ($LASTEXITCODE -ne 0) {
    throw "Nested SLO release contract gate failed."
}
Write-Host "Workflow H multi-cluster, SLO, ABAC, WORM, and drill contracts passed."
