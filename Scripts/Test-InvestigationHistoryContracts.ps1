[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot

# 工作流 G 的关键契约必须同时存在于数据库、服务端和前端，避免只完成展示层。 Contract gate.
$contractFiles = @(
    "Services/GuiyangMahjong.Lobby/Storage/schema.sql"
    "Services/GuiyangMahjong.Lobby/Storage/RoomMonitoringStore.cs"
    "Services/GuiyangMahjong.Lobby/Storage/PlayerHistoryStore.cs"
    "Services/GuiyangMahjong.Lobby/Api/LobbyEndpoints.Monitoring.cs"
    "Services/GuiyangMahjong.Admin/Api/AdminEndpoints.cs"
    "Services/GuiyangMahjong.Admin/Api/AdminEndpoints.Investigations.cs"
    "Services/GuiyangMahjong.Admin/Api/AdminEndpoints.Players.cs"
    "Services/GuiyangMahjong.Admin/Api/PlayerEvidenceEndpoints.cs"
    "Services/GuiyangMahjong.Admin/Api/PlayerEvidenceEndpoints.Chat.cs"
    "Services/GuiyangMahjong.Admin/Api/PlayerEvidenceEndpoints.Replays.cs"
    "Services/GuiyangMahjong.Admin/Api/PlayerEvidenceEndpoints.GmOperations.cs"
    "Services/GuiyangMahjong.Admin/Services/ReplayArchiveClient.cs"
    "Services/GuiyangMahjong.Admin/Services/ChatArchiveQueryClient.cs"
    "Services/GuiyangMahjong.Admin/ClientApp/src/app/admin-console/admin-console.ts"
    "Services/GuiyangMahjong.Admin/ClientApp/src/app/admin-console/admin-console-dashboard.ts"
    "Services/GuiyangMahjong.Admin/ClientApp/src/app/admin-console/admin-console-management.ts"
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

# 运行时不得把对象存储或聊天归档凭据写入任一 Angular 控制台源码。
# 扫描整个功能目录，避免拆分文件后旧的单文件门禁出现覆盖盲区。

$clientRoot = Join-Path `
    $projectRoot `
    "Services/GuiyangMahjong.Admin/ClientApp/src/app/admin-console"
$client = (
    Get-ChildItem -LiteralPath $clientRoot -File -Filter '*.ts' |
        ForEach-Object {
            $clientFilePath = $_.FullName
            Get-Content -Raw -LiteralPath $clientFilePath
        }
) -join "`n"
foreach ($forbidden in @("ReplayArchive__ReadToken", "ChatArchive__QueryToken")) {
    if ($client -match [regex]::Escape($forbidden)) {
        throw "Investigation history contract failed: browser contains $forbidden"
    }
}

Write-Host "Persistent history, replay, evidence package, and chat gateway contracts passed."
