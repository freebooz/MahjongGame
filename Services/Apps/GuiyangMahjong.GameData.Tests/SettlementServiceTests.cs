using System.Security.Cryptography;
using System.Text;
using GuiyangMahjong.Contracts.Events;
using GuiyangMahjong.GameData.Domain;
using GuiyangMahjong.GameData.Infrastructure;
using GuiyangMahjong.GameData.Options;
using GuiyangMahjong.GameData.ReplayEvidence;
using GuiyangMahjong.GameData.Settlement;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;

namespace GuiyangMahjong.GameData.Tests;

/// <summary>
/// 可信结算核心用例测试。测试替身只替代 Lobby 和对象存储边界，结算格式、签名、幂等和投影均执行生产代码。
/// </summary>
public sealed class SettlementServiceTests
{
    private const string SigningKey = "unit-test-settlement-signing-key-000000000000";
    private const string Credential = "unit-test-workload-credential-000000000000";
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);

    /// <summary>验证正常结算写入不可变战绩、证据和基础排行榜。</summary>
    [Fact]
    public async Task Commit_WritesSettlementAndReadModels()
    {
        var store = new InMemoryGameDataStore();
        var envelope = CreateEnvelope();
        var service = CreateService(store, new AuthorityStub(envelope));

        var result = await CommitAsync(service, envelope);

        Assert.False(result.Duplicate);
        Assert.NotNull(await store.GetMatchAsync(envelope.MatchId, default));
        Assert.NotNull(await store.GetEvidenceAsync(envelope.EvidenceId, default));
        Assert.Equal(2, (await store.GetLeaderboardAsync(10, default)).Count);
    }

    /// <summary>验证成功响应丢失后的重复提交返回首次 SettlementId，且不重复累计排行榜。</summary>
    [Fact]
    public async Task Commit_DuplicateReturnsFirstResponseWithoutProjectionSideEffects()
    {
        var store = new InMemoryGameDataStore();
        var envelope = CreateEnvelope();
        var service = CreateService(store, new AuthorityStub(envelope));

        var first = await CommitAsync(service, envelope);
        var duplicate = await CommitAsync(service, envelope);

        Assert.True(duplicate.Duplicate);
        Assert.Equal(first.SettlementId, duplicate.SettlementId);
        Assert.All(await store.GetLeaderboardAsync(10, default), item => Assert.Equal(1, item.MatchCount));
    }

    /// <summary>验证并发相同提交只产生一次首次写入，其余调用均走数据库等价幂等路径。</summary>
    [Fact]
    public async Task Commit_ConcurrentDuplicateHasSingleFirstWrite()
    {
        var store = new InMemoryGameDataStore();
        var envelope = CreateEnvelope();
        var service = CreateService(store, new AuthorityStub(envelope));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => CommitAsync(service, envelope)));

        Assert.Single(results, result => !result.Duplicate);
        Assert.Equal(7, results.Count(result => result.Duplicate));
        Assert.Single(results.Select(result => result.SettlementId).Distinct());
    }

    /// <summary>同一业务幂等键携带不同结果必须冲突，历史结果不可被第二份载荷覆盖。</summary>
    [Fact]
    public async Task Commit_SameBusinessKeyWithDifferentPayloadConflicts()
    {
        var store = new InMemoryGameDataStore();
        var first = CreateEnvelope();
        var service = CreateService(store, new AuthorityStub(first));
        await CommitAsync(service, first);
        var changed = Sign(first with
        {
            PlayerResults = [first.PlayerResults[0] with { TotalScore = 99 }, first.PlayerResults[1]]
        });

        var exception = await Assert.ThrowsAsync<GameDataException>(() => CommitAsync(service, changed));

        Assert.Equal("SETTLEMENT_IDEMPOTENCY_CONFLICT", exception.Code);
    }

    /// <summary>错误实例、旧 Epoch、错误规则或构建版本均由 Room 权威边界拒绝。</summary>
    [Theory]
    [InlineData("server")]
    [InlineData("epoch")]
    [InlineData("rules")]
    [InlineData("build")]
    [InlineData("players")]
    [InlineData("round")]
    public async Task Commit_AuthorityMismatchIsRejected(string mismatch)
    {
        var envelope = CreateEnvelope();
        var authority = AuthorityStub.CreateAuthority(envelope) with
        {
            ServerInstanceId = mismatch == "server" ? Guid.NewGuid().ToString() : envelope.ServerInstanceId,
            RoomEpoch = mismatch == "epoch" ? envelope.RoomEpoch + 1 : envelope.RoomEpoch,
            RuleSetVersion = mismatch == "rules" ? "other-rules" : envelope.RuleSetVersion,
            ServerBuild = mismatch == "build" ? "other-build" : envelope.ServerBuild,
            ExpectedRoundNo = mismatch == "round" ? envelope.RoundNo + 1 : envelope.RoundNo,
            PlayerIds = mismatch == "players" ? ["unexpected-player"] : envelope.PlayerResults.Select(x => x.PlayerId).ToArray()
        };
        var service = CreateService(new InMemoryGameDataStore(), new AuthorityStub(envelope, authority));

        var exception = await Assert.ThrowsAsync<GameDataException>(() => CommitAsync(service, envelope));

        Assert.Equal(StatusCodes.Status401Unauthorized, exception.StatusCode);
    }

    /// <summary>状态、动作或随机承诺摘要格式损坏时必须在任何外部调用前失败关闭。</summary>
    [Theory]
    [InlineData("state")]
    [InlineData("actions")]
    [InlineData("random")]
    public async Task Commit_InvalidDigestIsRejected(string digest)
    {
        var envelope = CreateEnvelope();
        envelope = envelope with
        {
            FinalStateHash = digest == "state" ? "broken" : envelope.FinalStateHash,
            ActionLogHash = digest == "actions" ? "broken" : envelope.ActionLogHash,
            RandomCommitment = digest == "random" ? "broken" : envelope.RandomCommitment
        };
        var service = CreateService(new InMemoryGameDataStore(), new AuthorityStub(envelope));

        var exception = await Assert.ThrowsAsync<GameDataException>(() => CommitAsync(service, envelope));

        Assert.Equal("FINAL_RESULT_INVALID", exception.Code);
    }

    /// <summary>伪造签名即使拥有有效短期凭据也不能提交最终结果。</summary>
    [Fact]
    public async Task Commit_InvalidServerSignatureIsRejected()
    {
        var envelope = CreateEnvelope() with { ServerSignature = new string('0', 64) };
        var service = CreateService(new InMemoryGameDataStore(), new AuthorityStub(envelope));

        var exception = await Assert.ThrowsAsync<GameDataException>(() => CommitAsync(service, envelope));

        Assert.Equal("SERVER_SIGNATURE_INVALID", exception.Code);
    }

    /// <summary>证据缺失或对象存储不可用时，不允许留下半笔结算或战绩。</summary>
    [Fact]
    public async Task Commit_EvidenceUnavailableLeavesNoSettlement()
    {
        var store = new InMemoryGameDataStore();
        var envelope = CreateEnvelope();
        var service = CreateService(store, new AuthorityStub(envelope), new FailingEvidenceVerifier());

        var exception = await Assert.ThrowsAsync<GameDataException>(() => CommitAsync(service, envelope));

        Assert.Equal("EVIDENCE_STORE_UNAVAILABLE", exception.Code);
        Assert.Null(await store.GetMatchAsync(envelope.MatchId, default));
    }

    /// <summary>存储事务失败时不得泄漏任何内存投影，用来约束生产存储的原子事务语义。</summary>
    [Fact]
    public async Task Commit_StoreFailureDoesNotCreateReadModel()
    {
        var inner = new InMemoryGameDataStore();
        var envelope = CreateEnvelope();
        var service = CreateService(new FailingStore(inner), new AuthorityStub(envelope));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CommitAsync(service, envelope));

        Assert.Null(await inner.GetMatchAsync(envelope.MatchId, default));
    }

    /// <summary>影子验证执行完整安全核对，但绝不能写入战绩、排行榜或结算 Outbox。</summary>
    [Fact]
    public async Task ShadowValidation_PerformsChecksWithoutWriting()
    {
        var store = new InMemoryGameDataStore();
        var envelope = CreateEnvelope();
        var service = CreateService(store, new AuthorityStub(envelope));

        var result = await service.ValidateOnlyAsync(envelope, Credential, default);

        Assert.True(result.Validated);
        Assert.False(result.Committed);
        Assert.Null(await store.GetMatchAsync(envelope.MatchId, default));
        Assert.Empty(await store.GetLeaderboardAsync(10, default));
    }

    /// <summary>创建生产用服务对象，并显式固定时间和密钥，避免测试依赖墙钟或外部机密。</summary>
    private static SettlementService CreateService(
        IGameDataStore store,
        ISettlementAuthorityClient authority,
        IEvidenceVerifier? evidence = null) =>
        new(authority, evidence ?? new MetadataEvidenceVerifier(), store,
            new FixedTimeProvider(Now), Microsoft.Extensions.Options.Options.Create(new GameDataOptions
            {
                SettlementSigningKey = SigningKey,
                LobbyAuthorityToken = new string('l', 32),
                MonitoringToken = new string('m', 32),
                AllocatorRecoveryToken = new string('r', 32)
            }), NullLogger<SettlementService>.Instance);

    /// <summary>按正式业务幂等键提交，测试调用不会隐藏重试或修改信封。</summary>
    private static Task<SettlementCommitResult> CommitAsync(
        SettlementService service,
        FinalResultEnvelope envelope) => service.CommitAsync(
        envelope, Credential, false,
        $"{envelope.MatchId}:{envelope.RoundNo}:{envelope.SettlementVersion}",
        Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), default);

    /// <summary>构造包含两名玩家和两类内容寻址证据的最小合法强结算信封。</summary>
    private static FinalResultEnvelope CreateEnvelope()
    {
        var matchId = Guid.NewGuid().ToString();
        var roomId = Guid.NewGuid().ToString();
        var instanceId = Guid.NewGuid().ToString();
        var evidenceId = Guid.NewGuid().ToString();
        var snapshotHash = Hash("snapshot");
        var actionsHash = Hash("actions");
        return Sign(new FinalResultEnvelope(
            matchId, roomId, 1, 1, instanceId, 3, "guiyang-v1", "server-1.0.0",
            SettlementSecurity.CredentialHash(Credential), Hash("state"), actionsHash, Hash("random"),
            [new("player-a", 0, 1, 10), new("player-b", 1, 2, -10)], evidenceId,
            [
                new("snapshot", $"matches/{matchId}/epochs/3/{snapshotHash}/snapshot.json", snapshotHash, 128),
                new("actions", $"matches/{matchId}/epochs/3/{actionsHash}/actions.jsonl", actionsHash, 256)
            ], Now, new string('0', 64)));
    }

    /// <summary>使用与 DS 一致的规范串计算测试签名。</summary>
    private static FinalResultEnvelope Sign(FinalResultEnvelope envelope)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SigningKey));
        var signature = Convert.ToHexString(hmac.ComputeHash(
            Encoding.UTF8.GetBytes(SettlementSecurity.BuildCanonical(envelope)))).ToLowerInvariant();
        return envelope with { ServerSignature = signature };
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>固定 UTC 时间，确保生成时间窗口测试可重复。</summary>
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    /// <summary>模拟 Lobby 只读权威核对及幂等关闭回调。</summary>
    private sealed class AuthorityStub(
        FinalResultEnvelope envelope,
        SettlementAuthority? authority = null) : ISettlementAuthorityClient
    {
        private readonly SettlementAuthority authority = authority ?? CreateAuthority(envelope);
        public Task<SettlementAuthority> ValidateAsync(FinalResultEnvelope _, string __, CancellationToken ___) =>
            Task.FromResult(authority);
        public Task NotifyCommittedAsync(SettlementCommitResult _, string __, CancellationToken ___) =>
            Task.CompletedTask;
        public static SettlementAuthority CreateAuthority(FinalResultEnvelope envelope) => new(
            true, envelope.MatchId, envelope.RoomId, envelope.ServerInstanceId, envelope.RoomEpoch,
            envelope.RuleSetVersion, envelope.ServerBuild, envelope.RoundNo,
            envelope.PlayerResults.Select(player => player.PlayerId).ToArray());
    }

    /// <summary>模拟对象存储临时不可用；失败发生在事务写入之前。</summary>
    private sealed class FailingEvidenceVerifier : IEvidenceVerifier
    {
        public Task VerifyAsync(IReadOnlyList<EvidenceManifestItem> _, CancellationToken __) =>
            throw GameDataException.Unavailable("EVIDENCE_STORE_UNAVAILABLE", "test");
    }

    /// <summary>模拟数据库事务整体失败，不委托内部存储，因此不可能留下部分投影。</summary>
    private sealed class FailingStore(InMemoryGameDataStore inner) : IGameDataStore
    {
        public Task<SettlementWriteResult> CommitAsync(FinalResultEnvelope _, string __,
            SettlementCommitResult ___, EventEnvelope ____, CancellationToken _____) =>
            throw new InvalidOperationException("transaction failed");
        public Task<GameRecord?> GetMatchAsync(string id, CancellationToken token) => inner.GetMatchAsync(id, token);
        public Task<IReadOnlyList<GameRecord>> GetPlayerRecordsAsync(string id, int limit, CancellationToken token) =>
            inner.GetPlayerRecordsAsync(id, limit, token);
        public Task<ReplayEvidenceRecord?> GetEvidenceAsync(string id, CancellationToken token) =>
            inner.GetEvidenceAsync(id, token);
        public Task<LegacyReplayEvidenceResult> RecordLegacyReplayAsync(
            LegacyReplayEvidenceRequest request, CancellationToken token) =>
            inner.RecordLegacyReplayAsync(request, token);
        public Task<IReadOnlyList<LeaderboardEntry>> GetLeaderboardAsync(int limit, CancellationToken token) =>
            inner.GetLeaderboardAsync(limit, token);
        public Task<bool> CheckHealthAsync(CancellationToken token) => inner.CheckHealthAsync(token);
    }
}
