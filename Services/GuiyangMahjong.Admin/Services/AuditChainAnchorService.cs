using System.Net.Http.Headers;
using System.Net.Http.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Storage;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Services;

/// <summary>
/// 周期验证本地审计哈希链并把最新链头提交独立 WORM/SIEM。
/// 任何断链都会产生 Critical 日志和失败指标，绝不发送看似有效的新锚点。
/// </summary>
public sealed class AuditChainAnchorService(
    IAdminActionStore store,
    IHttpClientFactory httpClientFactory,
    IOptions<AdminOptions> options,
    TimeProvider timeProvider,
    ILogger<AuditChainAnchorService> logger) : BackgroundService
{
    private readonly AuditArchiveOptions settings = options.Value.AuditArchive;
    private string? lastAnchoredHash;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.AnchorEnabled) return;
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(settings.AnchorIntervalSeconds));
        do
        {
            try
            {
                await VerifyAndAnchorAsync(stoppingToken);
            }
            catch (InvalidDataException)
            {
                // 审计断链属于安全完整性事件，必须让宿主失败停止，避免管理命令在证据不可信时继续执行。
                throw;
            }
            catch (Exception exception) when (
                exception is HttpRequestException
                or TaskCanceledException)
            {
                // 外部 WORM 暂时不可用时保留本地链和 Outbox，下一周期使用相同幂等键重试。
                logger.LogError(
                    exception,
                    "AuditChainAnchorDeliveryFailed AnchorUrl={AnchorUrl}",
                    settings.AnchorUrl);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// 校验最近 1000 条链记录并提交链头；外部端以 headHash 作为幂等键。
    /// 数据库管理员篡改正文、前序哈希或记录哈希时，本方法均会拒绝锚定。
    /// </summary>
    public async Task VerifyAndAnchorAsync(
        CancellationToken cancellationToken)
    {
        var records = (await store.ListAuditAsync(1000, cancellationToken))
            .OrderBy(item => item.Sequence)
            .ToArray();
        if (records.Length == 0) return;
        for (var index = 0; index < records.Length; index++)
        {
            var current = records[index];
            if (index > 0
                && current.PreviousHash != records[index - 1].RecordHash)
            {
                FailIntegrity(current.Sequence);
            }
            var draft = new AdminAuditDraft(
                current.OccurredAtUtc,
                current.OperatorId,
                current.Operation,
                current.TargetType,
                current.TargetId,
                current.Reason,
                current.BeforeState,
                current.AfterState,
                current.ApprovalRecord,
                current.TraceId,
                current.TicketId);
            if (AdminAuditHash.Compute(
                    current.Sequence,
                    draft,
                    current.PreviousHash) != current.RecordHash)
            {
                FailIntegrity(current.Sequence);
            }
        }
        var head = records[^1];
        if (head.RecordHash == lastAnchoredHash) return;
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            settings.AnchorUrl)
        {
            Content = JsonContent.Create(new
            {
                schemaVersion = 1,
                anchoredAtUtc = timeProvider.GetUtcNow(),
                firstSequence = records[0].Sequence,
                headSequence = head.Sequence,
                headHash = head.RecordHash,
                previousHash = head.PreviousHash,
                recordCount = records.Length
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            settings.AppendToken);
        request.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            head.RecordHash);
        using var response = await httpClientFactory
            .CreateClient(nameof(AuditChainAnchorService))
            .SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode
            && response.StatusCode != System.Net.HttpStatusCode.Conflict)
        {
            MahjongTelemetry.RecordAuditChainAnchorOutcome("failed");
            throw new HttpRequestException(
                $"Audit anchor endpoint returned {(int)response.StatusCode}.");
        }
        lastAnchoredHash = head.RecordHash;
        MahjongTelemetry.RecordAuditChainAnchorOutcome("succeeded");
    }

    private void FailIntegrity(long sequence)
    {
        MahjongTelemetry.RecordAuditChainAnchorOutcome("integrity_failed");
        logger.LogCritical(
            "Audit chain integrity verification failed at Sequence={Sequence}",
            sequence);
        throw new InvalidDataException(
            $"Audit chain integrity verification failed at sequence {sequence}.");
    }
}
