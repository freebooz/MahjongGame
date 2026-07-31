$ErrorActionPreference = "Stop"
$required = @(
    "Services/Apps/GuiyangMahjong.Configuration/Storage/schema.sql",
    "Deploy/Agones/guiyang-mahjong-fleet.yaml",
    "Deploy/Agones/guiyang-mahjong-fleet-canary.yaml",
    "Deploy/helm/guiyang-mahjong/Chart.yaml",
    "Deploy/helm/guiyang-mahjong/values.yaml"
)
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing release governance artifact: $path" }
}
$eventContract = Get-Content -LiteralPath "Services/BuildingBlocks/GuiyangMahjong.BuildingBlocks.Messaging/PlatformEventSubjects.cs" -Raw
if ($eventContract -notmatch 'configuration\.published\.v1') { throw "Missing versioned configuration publication subject" }
$fleetContent = Get-Content -LiteralPath "Deploy/Agones/guiyang-mahjong-fleet.yaml","Deploy/Agones/guiyang-mahjong-fleet-canary.yaml" -Raw
foreach ($label in @("server-build", "ruleset-version", "protocol-version", "release-track", "config-version")) {
    if ($fleetContent -notmatch [regex]::Escape($label)) { throw "Missing fleet version label: $label" }
}
$metrics = Get-ChildItem -LiteralPath "Services" -Recurse -Filter *.cs | Get-Content -Raw
if ($metrics -match 'new\("(player_id|room_id)"') { throw "High-cardinality Prometheus label detected" }
Write-Host "Release governance contracts passed."
