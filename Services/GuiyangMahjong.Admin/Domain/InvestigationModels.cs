using System.Text.Json;

namespace GuiyangMahjong.Admin.Domain;

/// <summary>
/// Admin 只读的玩家房间历史投影；来源为 Lobby 权威库，不包含设备、IP 等身份信息。
/// </summary>
public sealed record PlayerRoomHistoryRecord(
    string PlayerId,
    string RoomId,
    string MatchId,
    DateTimeOffset JoinedAtUtc,
    DateTimeOffset? LeftAtUtc,
    string? LeaveReason);

/// <summary>
/// Admin 只读的玩家连接事件；EventId、TraceId 可关联房间事件与审计账本。
/// </summary>
public sealed record PlayerConnectionHistoryRecord(
    string EventId,
    string PlayerId,
    string RoomId,
    string? MatchId,
    string? FromState,
    string ToState,
    bool? Trustee,
    DateTimeOffset OccurredAtUtc,
    string TraceId);

/// <summary>跨服务历史页，下一页边界保持为 Lobby 生成的不可变键集。</summary>
public sealed record PlayerHistoryPage<T>(
    T[] Items,
    DateTimeOffset? NextBeforeAtUtc,
    string? NextBeforeId);

/// <summary>
/// 案件证据包清单；CanonicalPayloadHash 使用 SHA-256，可在离线导出后验证内容未被替换。
/// </summary>
public sealed record InvestigationEvidencePackage(
    string PackageId,
    string CaseId,
    string TargetType,
    string TargetId,
    DateTimeOffset GeneratedAtUtc,
    string GeneratedBy,
    DateTimeOffset RangeStartsAtUtc,
    DateTimeOffset RangeEndsAtUtc,
    string TraceId,
    string TicketId,
    string CanonicalPayloadHash,
    JsonElement Manifest,
    JsonElement Evidence);
