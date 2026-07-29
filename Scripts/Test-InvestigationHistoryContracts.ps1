[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot

# 工作流 G 的关键契约必须同时存在于数据库、服务端和前端，避免只完成展示层。 Contract gate.
$contractFiles = @(
    "Services/GuiyangMahjong.Lobby/Storage/schema.sql"
    "Services/GuiyangMahjong.Lobby/Storage/RoomMonitoringStore.cs"
    "Services/GuiyangMahjong.Lobby/Storage/PlayerHistoryStore.cs"
    "Services/GuiyangMahjong.Admin/Api/AdminEndpoints.cs"
    "Services/GuiyangMahjong.Admin/Api/PlayerEvidenceEndpoints.cs"
    "Services/GuiyangMahjong.Admin/Services/ReplayArchiveClient.cs"
    "Services/GuiyangMahjong.Admin/Services/ChatArchiveQueryClient.cs"
    "Services/GuiyangMahjong.Admin/ClientApp/src/app/admin-console.ts"
)
$combined = ($contractFiles | ForEach-Object {
    Get-Content -Raw -LiteralPath (Join-Path $projectRoot $_)
}) -join "`n"

$requiredPatterns = @(
    "room_event_history",
    "reject_room_event_mutation",
    "player_room_history",
    "player_connection_history",
    "ListPlayerRoomHistoryAsync",
    "InvestigationEvidencePackageGenerated",
    "CanonicalPayloadHash",
    "EvidencePackageHash",
    "ValidateAccess",
    "PlayerChatContentViewed",
    "data-case-close",
    "connection-history"
)
foreach ($pattern in $requiredPatterns) {
    if ($combined -notmatch [regex]::Escape($pattern)) {
        throw "Investigation history contract failed: missing $pattern"
    }
}

# 运行时不得把对象存储或聊天归档凭据写入 Angular 源码。 Browser secret guard.
$clientPath = Join-Path $projectRoot "Services/GuiyangMahjong.Admin/ClientApp/src/app/admin-console.ts"
$client = Get-Content -Raw -LiteralPath $clientPath
foreach ($forbidden in @("ReplayArchive__ReadToken", "ChatArchive__QueryToken")) {
    if ($client -match [regex]::Escape($forbidden)) {
        throw "Investigation history contract failed: browser contains $forbidden"
    }
}

Write-Host "Persistent history, replay, evidence package, and chat gateway contracts passed."
