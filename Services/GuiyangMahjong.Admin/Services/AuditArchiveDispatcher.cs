using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Storage;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Services;

public sealed class AuditArchiveDispatcher(
    IAuditArchiveOutboxStore store,
    IHttpClientFactory httpClientFactory,
    IOptions<AdminOptions> options,
    TimeProvider timeProvider,
    ILogger<AuditArchiveDispatcher> logger)
{
    private readonly AuditArchiveOptions archive = options.Value.AuditArchive;
    private readonly string workerId =
        $"audit-archive:{Environment.MachineName}:{Guid.NewGuid():N}";

    public async Task DispatchOnceAsync(CancellationToken cancellationToken)
    {
        if (!archive.Enabled) return;
        var now = timeProvider.GetUtcNow();
        var records = await store.ClaimAsync(
            workerId,
            100,
            now,
            now.AddSeconds(30),
            cancellationToken);
        MahjongTelemetry.RecordAuditArchiveBatch(records.Count);
        foreach (var record in records)
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    archive.AppendUrl)
                {
                    Content = JsonContent.Create(record.Payload)
                };
                request.Headers.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer",
                        archive.AppendToken);
                request.Headers.TryAddWithoutValidation(
                    "Idempotency-Key",
                    record.AuditId);
                if (record.Payload.TryGetProperty(
                        "traceId",
                        out var traceId))
                {
                    request.Headers.TryAddWithoutValidation(
                        "X-Trace-Id",
                        traceId.GetString());
                }
                var client = httpClientFactory.CreateClient(
                    nameof(AuditArchiveDispatcher));
                using var response = await client.SendAsync(
                    request,
                    cancellationToken);
                if (response.IsSuccessStatusCode
                    || response.StatusCode == HttpStatusCode.Conflict)
                {
                    MahjongTelemetry.RecordAuditArchiveOutcome("succeeded");
                    var archivedAtUtc = timeProvider.GetUtcNow();
                    if (record.Payload.TryGetProperty(
                            "occurredAtUtc",
                            out var occurredAt)
                        && occurredAt.TryGetDateTimeOffset(
                            out var occurredAtUtc))
                    {
                        MahjongTelemetry.RecordAuditArchiveLatency(
                            occurredAtUtc,
                            archivedAtUtc);
                    }
                    await store.CompleteAsync(
                        record.AuditId,
                        workerId,
                        archivedAtUtc,
                        cancellationToken);
                    continue;
                }
                var retryable = response.StatusCode is
                    HttpStatusCode.RequestTimeout
                    or HttpStatusCode.TooManyRequests
                    or >= HttpStatusCode.InternalServerError;
                await FailAsync(
                    record,
                    $"Archive endpoint returned {(int)response.StatusCode}.",
                    !retryable,
                    cancellationToken);
            }
            catch (Exception exception)
                when (exception is HttpRequestException
                    or TaskCanceledException)
            {
                await FailAsync(
                    record,
                    exception.Message,
                    false,
                    cancellationToken);
            }
        }
    }

    private async Task FailAsync(
        AuditArchiveOutboxRecord record,
        string error,
        bool terminalResponse,
        CancellationToken cancellationToken)
    {
        var terminal =
            terminalResponse || record.AttemptCount >= archive.MaxAttempts;
        MahjongTelemetry.RecordAuditArchiveOutcome(
            terminal ? "failed" : "retry_scheduled");
        var delaySeconds = Math.Min(
            300,
            Math.Pow(2, Math.Min(record.AttemptCount, 8)));
        await store.FailAsync(
            record.AuditId,
            workerId,
            error,
            timeProvider.GetUtcNow().AddSeconds(delaySeconds),
            terminal,
            cancellationToken);
        logger.LogWarning(
            "Immutable audit archive delivery {AuditId} failed on attempt {AttemptCount}; terminal={Terminal}.",
            record.AuditId,
            record.AttemptCount,
            terminal);
    }
}

public sealed class AuditArchiveDispatcherService(
    AuditArchiveDispatcher dispatcher,
    IOptions<AdminOptions> options,
    ILogger<AuditArchiveDispatcherService> logger)
    : BackgroundService
{
    private readonly AuditArchiveOptions archive = options.Value.AuditArchive;

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        if (!archive.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await dispatcher.DispatchOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Immutable audit archive dispatch cycle failed.");
            }
            await Task.Delay(
                archive.PollIntervalMilliseconds,
                stoppingToken);
        }
    }
}
