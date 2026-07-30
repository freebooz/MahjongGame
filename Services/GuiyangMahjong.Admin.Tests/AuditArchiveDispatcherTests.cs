// 验证审计 Outbox 归档调度器的成功确认、重试退避、永久失败和敏感响应处理。
using System.Net;
using System.Text.Json;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Services;
using GuiyangMahjong.Admin.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuiyangMahjong.Admin.Tests;

public sealed class AuditArchiveDispatcherTests
{
    [Fact]
    public async Task SuccessfulDeliveryUsesAuditIdAsIdempotencyKey()
    {
        var auditId = Guid.NewGuid().ToString();
        var store = new RecordingArchiveStore(new AuditArchiveOutboxRecord(
            auditId,
            JsonSerializer.SerializeToElement(new
            {
                auditId,
                traceId = "trace-archive-test",
                recordHash = new string('a', 64)
            }),
            1));
        var handler = new ArchiveHandler();
        var dispatcher = new AuditArchiveDispatcher(
            store,
            new TestHttpClientFactory(handler),
            Microsoft.Extensions.Options.Options.Create(
                new AdminOptions
                {
                    AuditArchive = new AuditArchiveOptions
                    {
                        Enabled = true,
                        AppendUrl =
                            "https://archive.example.invalid/v1/audit",
                        AppendToken =
                            "archive-token-that-is-at-least-32-characters"
                    }
                }),
            TimeProvider.System,
            NullLogger<AuditArchiveDispatcher>.Instance);

        await dispatcher.DispatchOnceAsync(CancellationToken.None);

        Assert.Equal(auditId, store.CompletedAuditId);
        Assert.Equal(auditId, handler.IdempotencyKey);
        Assert.Equal("trace-archive-test", handler.TraceId);
        Assert.Equal(
            "archive-token-that-is-at-least-32-characters",
            handler.Token);
    }

    private sealed class RecordingArchiveStore(
        AuditArchiveOutboxRecord record) : IAuditArchiveOutboxStore
    {
        private bool claimed;
        public string? CompletedAuditId { get; private set; }

        public Task<bool> CheckHealthAsync(
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<IReadOnlyList<AuditArchiveOutboxRecord>> ClaimAsync(
            string workerId,
            int limit,
            DateTimeOffset now,
            DateTimeOffset leaseExpiresAtUtc,
            CancellationToken cancellationToken)
        {
            if (claimed)
                return Task.FromResult<IReadOnlyList<AuditArchiveOutboxRecord>>(
                    []);
            claimed = true;
            return Task.FromResult<IReadOnlyList<AuditArchiveOutboxRecord>>(
                [record]);
        }

        public Task CompleteAsync(
            string auditId,
            string workerId,
            DateTimeOffset archivedAtUtc,
            CancellationToken cancellationToken)
        {
            CompletedAuditId = auditId;
            return Task.CompletedTask;
        }

        public Task FailAsync(
            string auditId,
            string workerId,
            string error,
            DateTimeOffset availableAtUtc,
            bool terminal,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Delivery should succeed.");
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class ArchiveHandler : HttpMessageHandler
    {
        public string? IdempotencyKey { get; private set; }
        public string? TraceId { get; private set; }
        public string? Token { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            IdempotencyKey = request.Headers
                .GetValues("Idempotency-Key").Single();
            TraceId = request.Headers.GetValues("X-Trace-Id").Single();
            Token = request.Headers.Authorization?.Parameter;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Created));
        }
    }
}
