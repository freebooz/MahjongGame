using System.Text.Json.Serialization;

namespace GuiyangMahjong.GameData.Domain;

/// <summary>最终结算中的单个玩家结果；只描述比赛得分，不代表资产账本变更。</summary>
public sealed record FinalPlayerResult(
    [property: JsonPropertyName("player_id")] string PlayerId,
    [property: JsonPropertyName("seat_id")] int SeatId,
    [property: JsonPropertyName("rank")] int Rank,
    [property: JsonPropertyName("total_score")] int TotalScore);

/// <summary>
/// 一个不可覆盖证据对象的清单项。ObjectKey 必须绑定 Match/Epoch/内容哈希，
/// Sha256 是对象内容摘要而不是路径摘要。
/// </summary>
public sealed record EvidenceManifestItem(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("object_key")] string ObjectKey,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size_bytes")] long SizeBytes);

/// <summary>
/// Dedicated Server 产生的版本化最终结果信封。
/// 客户端和 Admin 均无此写权限；ServerSignature 绑定所有结算字段和证据清单。
/// </summary>
public sealed record FinalResultEnvelope(
    [property: JsonPropertyName("match_id")] string MatchId,
    [property: JsonPropertyName("room_id")] string RoomId,
    [property: JsonPropertyName("round_no")] int RoundNo,
    [property: JsonPropertyName("settlement_version")] int SettlementVersion,
    [property: JsonPropertyName("server_instance_id")] string ServerInstanceId,
    [property: JsonPropertyName("room_epoch")] long RoomEpoch,
    [property: JsonPropertyName("ruleset_version")] string RuleSetVersion,
    [property: JsonPropertyName("server_build")] string ServerBuild,
    [property: JsonPropertyName("workload_credential_hash")] string WorkloadCredentialHash,
    [property: JsonPropertyName("final_state_hash")] string FinalStateHash,
    [property: JsonPropertyName("action_log_hash")] string ActionLogHash,
    [property: JsonPropertyName("random_commitment")] string RandomCommitment,
    [property: JsonPropertyName("player_results")] FinalPlayerResult[] PlayerResults,
    [property: JsonPropertyName("evidence_id")] string EvidenceId,
    [property: JsonPropertyName("evidence_manifest")] EvidenceManifestItem[] EvidenceManifest,
    [property: JsonPropertyName("generated_at")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("server_signature")] string ServerSignature);

/// <summary>幂等提交回执；重复请求始终返回首次提交生成的 SettlementId 和提交时间。</summary>
public sealed record SettlementCommitResult(
    string SettlementId,
    string MatchId,
    int RoundNo,
    int SettlementVersion,
    DateTimeOffset CommittedAtUtc,
    bool Duplicate);

/// <summary>影子验证回执；只证明身份、作用域、签名和证据通过，不代表数据库已经提交结算。</summary>
public sealed record SettlementShadowValidationResult(
    string MatchId,
    int RoundNo,
    int SettlementVersion,
    DateTimeOffset ValidatedAtUtc,
    bool Validated,
    bool Committed = false);

/// <summary>Lobby 对 GameData 返回的只读权威作用域，不包含结果凭据原文。</summary>
public sealed record SettlementAuthority(
    bool Authorized,
    string MatchId,
    string RoomId,
    string ServerInstanceId,
    long RoomEpoch,
    string RuleSetVersion,
    string ServerBuild,
    int ExpectedRoundNo,
    string[] PlayerIds,
    string? FailureCode = null);

/// <summary>GameData 向 Lobby 发送的凭据摘要和结算作用域校验请求。</summary>
public sealed record SettlementAuthorityRequest(
    string MatchId,
    string RoomId,
    string ServerInstanceId,
    long RoomEpoch,
    string RuleSetVersion,
    string ServerBuild,
    int RoundNo,
    string CredentialSha256);

/// <summary>不可变战绩读模型；来源只能是已提交 SettlementCommitted 事务。</summary>
public sealed record GameRecord(
    string SettlementId,
    string MatchId,
    string RoomId,
    int RoundNo,
    int SettlementVersion,
    string RuleSetVersion,
    DateTimeOffset CommittedAtUtc,
    IReadOnlyList<FinalPlayerResult> PlayerResults);

/// <summary>受控回放证据目录；只公开对象元数据，不返回私有牌或对象内容。</summary>
public sealed record ReplayEvidenceRecord(
    string EvidenceId,
    string MatchId,
    long RoomEpoch,
    int RoundNo,
    int SettlementVersion,
    string FinalStateHash,
    string ActionLogHash,
    string RandomCommitment,
    DateTimeOffset RetainUntilUtc,
    IReadOnlyList<EvidenceManifestItem> Objects);

/// <summary>
/// 阶段 8.2 从 PlayerData 迁入的旧回放索引。该记录只保存旧调用方已经提供的受控元数据，
/// 不冒充阶段 7 的权威结算证据清单，也不得包含完整手牌、访问令牌或对象存储凭据。
/// </summary>
public sealed record LegacyReplayEvidenceRequest(
    string EventId,
    string PlayerId,
    string EvidenceType,
    DateTimeOffset OccurredAtUtc,
    string SourceReference,
    System.Text.Json.JsonElement Data,
    string Sensitivity);

/// <summary>旧回放索引的幂等写入结果；重复事件返回首次 EventId，冲突请求被显式拒绝。</summary>
public sealed record LegacyReplayEvidenceResult(string EventId, bool Duplicate);

/// <summary>基础排行榜投影；它可重建且不是比赛结果权威来源。</summary>
public sealed record LeaderboardEntry(
    string PlayerId,
    long TotalScore,
    long MatchCount,
    DateTimeOffset UpdatedAtUtc);

/// <summary>持久化提交判定；Conflict 表示同幂等键出现不同规范请求。</summary>
public enum SettlementWriteStatus
{
    Inserted,
    Duplicate,
    Conflict
}

/// <summary>存储层原子提交返回值，首次记录用于响应丢失后的稳定重放。</summary>
public sealed record SettlementWriteResult(
    SettlementWriteStatus Status,
    SettlementCommitResult? Result);
