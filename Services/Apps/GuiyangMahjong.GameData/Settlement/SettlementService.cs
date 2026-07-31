using System.Diagnostics;
using System.Diagnostics.Metrics;
using GuiyangMahjong.Contracts.Common;
using GuiyangMahjong.Contracts.Events;
using GuiyangMahjong.GameData.Domain;
using GuiyangMahjong.GameData.Infrastructure;
using GuiyangMahjong.GameData.ReplayEvidence;
using GuiyangMahjong.GameData.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.GameData.Settlement;

/// <summary>
/// 可信结算用例协调器。它按“本地格式/签名 → Lobby 权威 → 证据 → 数据库事务 → 房间回调”执行，
/// 任一步失败均不会伪造成功；数据库成功但回调失败时通过同幂等键重试补齐回调。
/// </summary>
public sealed class SettlementService(
    ISettlementAuthorityClient authorityClient,
    IEvidenceVerifier evidenceVerifier,
    IGameDataStore store,
    TimeProvider timeProvider,
    IOptions<GameDataOptions> options,
    ILogger<SettlementService> logger)
{
    private static readonly ActivitySource ActivitySource = new("GuiyangMahjong.GameData.Settlement");
    private static readonly Meter Meter = new("GuiyangMahjong.GameData.Settlement");
    private static readonly Counter<long> AcceptedCounter = Meter.CreateCounter<long>("mahjong_settlement_accepted_total");
    private static readonly Counter<long> DuplicateCounter = Meter.CreateCounter<long>("mahjong_settlement_duplicate_total");

    /// <summary>
    /// 验证并提交最终结果。Credential 只在本次调用内用于签名与摘要，绝不持久化或写日志；
    /// IdempotencyKey 必须精确绑定 match/round/version，取消会传播到所有外部调用。
    /// </summary>
    public async Task<SettlementCommitResult> CommitAsync(
        FinalResultEnvelope envelope,
        string? workloadCredential,
        bool trustedRecovery,
        string idempotencyKey,
        string traceId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("settlement.commit", ActivityKind.Server);
        activity?.SetTag("mahjong.match_id", envelope.MatchId);
        activity?.SetTag("mahjong.room_id", envelope.RoomId);
        activity?.SetTag("mahjong.room_epoch", envelope.RoomEpoch);
        var expectedIdempotencyKey = $"{envelope.MatchId}:{envelope.RoundNo}:{envelope.SettlementVersion}";
        if (!string.Equals(idempotencyKey, expectedIdempotencyKey, StringComparison.Ordinal))
            throw GameDataException.Invalid("IDEMPOTENCY_KEY_MISMATCH", "Idempotency-Key 与结算作用域不匹配");
        await ValidateSubmissionAsync(envelope, workloadCredential, trustedRecovery, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var settlementId = Guid.NewGuid().ToString();
        var firstResult = new SettlementCommitResult(
            settlementId, envelope.MatchId, envelope.RoundNo, envelope.SettlementVersion, now, false);
        var committedEvent = EventEnvelope.Create(
            new SettlementCommitted(
                MatchId.Parse(envelope.MatchId),
                RoomId.Parse(envelope.RoomId),
                settlementId,
                now),
            "settlement",
            envelope.MatchId,
            envelope.SettlementVersion,
            "gamedata",
            NormalizeOperationId(traceId),
            CorrelationId.Parse(NormalizeOperationId(correlationId)),
            now,
            idempotencyKey: IdempotencyKey.Parse(idempotencyKey));
        var write = await store.CommitAsync(
            envelope,
            SettlementSecurity.Fingerprint(envelope),
            firstResult,
            committedEvent,
            cancellationToken);
        if (write.Status == SettlementWriteStatus.Conflict)
            throw GameDataException.Conflict(
                "SETTLEMENT_IDEMPOTENCY_CONFLICT", "同一结算幂等键已绑定不同结果");
        var result = write.Result
            ?? throw new InvalidOperationException("结算存储未返回首次结果。");
        await authorityClient.NotifyCommittedAsync(result, envelope.RoomId, cancellationToken);

        if (write.Status == SettlementWriteStatus.Duplicate) DuplicateCounter.Add(1);
        else AcceptedCounter.Add(1);
        logger.LogInformation(
            "可信结算已确认 MatchId={MatchId} RoundNo={RoundNo} SettlementVersion={SettlementVersion} Duplicate={Duplicate} TraceId={TraceId}",
            envelope.MatchId, envelope.RoundNo, envelope.SettlementVersion, result.Duplicate, traceId);
        return result;
    }

    /// <summary>
    /// 执行影子验证但不写数据库、不写 Outbox、不关闭房间；用于旧 Lobby 仍正式写入期间对比新信封。
    /// 调用方必须明确识别 Committed=false，禁止把该回执当作最终结算成功。
    /// </summary>
    public async Task<SettlementShadowValidationResult> ValidateOnlyAsync(
        FinalResultEnvelope envelope,
        string workloadCredential,
        CancellationToken cancellationToken)
    {
        await ValidateSubmissionAsync(envelope, workloadCredential, false, cancellationToken);
        logger.LogInformation(
            "结算影子验证通过 MatchId={MatchId} RoundNo={RoundNo} SettlementVersion={SettlementVersion}",
            envelope.MatchId, envelope.RoundNo, envelope.SettlementVersion);
        return new SettlementShadowValidationResult(
            envelope.MatchId, envelope.RoundNo, envelope.SettlementVersion,
            timeProvider.GetUtcNow(), true);
    }

    /// <summary>集中执行格式、DS 签名、工作负载身份、Room 权威和证据验证，确保正式与影子路径规则一致。</summary>
    private async Task ValidateSubmissionAsync(
        FinalResultEnvelope envelope,
        string? workloadCredential,
        bool trustedRecovery,
        CancellationToken cancellationToken)
    {
        SettlementSecurity.ValidateEnvelope(envelope, timeProvider.GetUtcNow());
        if (!SettlementSecurity.VerifySignature(envelope, options.Value.SettlementSigningKey))
            throw GameDataException.Unauthorized("SERVER_SIGNATURE_INVALID", "Dedicated Server 签名无效");
        if (!trustedRecovery
            && (string.IsNullOrWhiteSpace(workloadCredential)
                || !string.Equals(SettlementSecurity.CredentialHash(workloadCredential),
                    envelope.WorkloadCredentialHash, StringComparison.OrdinalIgnoreCase)))
            throw GameDataException.Unauthorized("WORKLOAD_IDENTITY_MISMATCH", "Dedicated Server 工作负载身份无效");
        var authority = await authorityClient.ValidateAsync(
            envelope, envelope.WorkloadCredentialHash, cancellationToken);
        SettlementSecurity.ValidateAuthority(envelope, authority);
        await evidenceVerifier.VerifyAsync(envelope.EvidenceManifest, cancellationToken);
    }

    private static string NormalizeOperationId(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Length is >= 8 and <= 128 ? normalized : Guid.NewGuid().ToString("N");
    }
}
