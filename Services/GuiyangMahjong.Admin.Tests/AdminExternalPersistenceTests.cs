// PostgreSQL 外部集成测试：验证 Admin 操作、案件和证据在多实例并发下的事务与幂等性。
// 仅在显式提供隔离测试数据库时运行，默认跳过以避免误操作开发或生产数据。
using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Storage;
using GuiyangMahjong.Admin.Security;
using Npgsql;

namespace GuiyangMahjong.Admin.Tests;

public sealed class AdminExternalPersistenceFactAttribute : FactAttribute
{
    public AdminExternalPersistenceFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("ADMIN_TEST_POSTGRES")))
        {
            Skip = "Set ADMIN_TEST_POSTGRES to run Admin external persistence tests.";
        }
    }
}

public sealed class AdminExternalPersistenceTests
{
    [AdminExternalPersistenceFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task PostgreSql_ApprovalAndDispatchPersistAtomically()
    {
        var now = DateTimeOffset.UtcNow;
        var actionId = Guid.NewGuid().ToString();
        var targetId = $"player-postgres-{Guid.NewGuid():N}";
        var before = JsonSerializer.SerializeToElement(new
        {
            playerId = targetId,
            accountStatus = "Active",
            activeSessionCount = 1
        });
        var action = new AdminActionRecord(
            actionId,
            AdminManagementActionType.ForceLogoutPlayer,
            "Player",
            targetId,
            "external-operator",
            now,
            now.AddMinutes(5),
            now.AddHours(1),
            null,
            "External PostgreSQL workflow verification",
            $"TEST-{Guid.NewGuid():N}",
            Guid.NewGuid().ToString(),
            null,
            new string('a', 64),
            before,
            AdminActionStatus.AwaitingConfirmation,
            null,
            1,
            null,
            "INCIDENT_RESPONSE",
            "验证 PostgreSQL 能完整保存阶段10结构化管理证据",
            null,
            "external-idempotency-key");
        var requestedAudit = CreateAudit(
            action, now, "external-operator", "ActionRequested", null,
            JsonSerializer.SerializeToElement(action), null);

        await using var storeA = CreateStore();
        await using var storeB = CreateStore();
        await storeA.InitializeAsync(CancellationToken.None);
        await storeA.CreateAsync(action, requestedAudit, CancellationToken.None);
        var persistedRequested = await storeB.GetAsync(actionId, CancellationToken.None);
        Assert.Equal("INCIDENT_RESPONSE", persistedRequested?.ReasonCode);
        Assert.Equal("external-idempotency-key", persistedRequested?.IdempotencyKey);

        var approval = new AdminActionApproval(
            Guid.NewGuid().ToString(),
            "external-approver",
            now.AddSeconds(1),
            ApprovalDecision.Approve,
            "External transaction approval");
        var approved = action with
        {
            ConfirmedAtUtc = now.AddMilliseconds(500),
            Approval = approval,
            Status = AdminActionStatus.ApprovedAwaitingExecution,
            Version = 2
        };
        var approvalAudit = CreateAudit(
            approved,
            now.AddSeconds(1),
            approval.ApprovedBy,
            "ActionApprovalRecorded",
            JsonSerializer.SerializeToElement(action),
            JsonSerializer.SerializeToElement(approved),
            JsonSerializer.SerializeToElement(approval));
        Assert.True(await storeA.TryTransitionAsync(
            1, approved, approvalAudit, CancellationToken.None));

        var reloaded = await storeB.GetAsync(actionId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(AdminActionStatus.ApprovedAwaitingExecution, reloaded.Status);
        Assert.Equal("external-approver", reloaded.Approval?.ApprovedBy);
        var outbox = await storeB.ListOutboxAsync(500, CancellationToken.None);
        var command = Assert.Single(outbox, item => item.ActionRequestId == actionId);
        Assert.Equal("Pending", command.Status);
        var audit = await storeB.ListAuditAsync(1000, CancellationToken.None);
        Assert.Equal(2, audit.Count(item => item.TargetId == targetId));

        var claimed = Assert.Single(
            await storeA.ClaimOutboxAsync(
            "external-worker-a",
            500,
            now.AddSeconds(2),
            now.AddSeconds(32),
            CancellationToken.None),
            item => item.ActionRequestId == actionId);
        Assert.Equal(command.OutboxId, claimed.OutboxId);
        Assert.DoesNotContain(await storeB.ClaimOutboxAsync(
            "external-worker-b",
            500,
            now.AddSeconds(3),
            now.AddSeconds(33),
            CancellationToken.None),
            item => item.ActionRequestId == actionId);
        var completed = approved with
        {
            Status = AdminActionStatus.Succeeded,
            Version = 3
        };
        var completionAudit = CreateAudit(
            completed,
            now.AddSeconds(3),
            "system:outbox-dispatcher",
            "CommandSucceeded",
            JsonSerializer.SerializeToElement(approved),
            JsonSerializer.SerializeToElement(completed),
            JsonSerializer.SerializeToElement(approval));
        Assert.True(await storeA.CompleteOutboxAsync(
            claimed,
            completed,
            completionAudit,
            now.AddSeconds(3),
            CancellationToken.None));

        var persistedCommand = Assert.Single(
            await storeB.ListOutboxAsync(500, CancellationToken.None),
            item => item.ActionRequestId == actionId);
        Assert.Equal("Completed", persistedCommand.Status);
        Assert.Equal(1, persistedCommand.AttemptCount);
        Assert.Equal(
            AdminActionStatus.Succeeded,
            (await storeB.GetAsync(actionId, CancellationToken.None))?.Status);
        audit = await storeB.ListAuditAsync(1000, CancellationToken.None);
        Assert.Equal(3, audit.Count(item => item.TargetId == targetId));
        var targetAudit = audit.First(item => item.TargetId == targetId);
        await using (var immutableDataSource =
            NpgsqlDataSource.Create(ConnectionString))
        await using (var forbiddenUpdate = immutableDataSource.CreateCommand(
            """
            UPDATE admin_monitor.audit_ledger
            SET reason='forbidden mutation'
            WHERE audit_id=$1
            """))
        {
            forbiddenUpdate.Parameters.AddWithValue(
                Guid.Parse(targetAudit.AuditId));
            var exception = await Assert.ThrowsAsync<PostgresException>(
                async () => await forbiddenUpdate.ExecuteNonQueryAsync());
            Assert.Equal("P0001", exception.SqlState);
        }
        Assert.True(await storeB.CheckHealthAsync(CancellationToken.None));
        await using var archive = new PostgresAuditArchiveOutboxStore(
            NpgsqlDataSource.Create(ConnectionString));
        var archiveWorker = $"external-archive-{Guid.NewGuid():N}";
        var archiveBatch = await archive.ClaimAsync(
            archiveWorker,
            1000,
            now.AddMinutes(1),
            now.AddMinutes(2),
            CancellationToken.None);
        Assert.Contains(
            archiveBatch,
            item => item.AuditId == targetAudit.AuditId
                && item.Payload.GetProperty("recordHash").GetString()
                    == targetAudit.RecordHash);
        foreach (var item in archiveBatch)
        {
            await archive.CompleteAsync(
                item.AuditId,
                archiveWorker,
                now.AddMinutes(1),
                CancellationToken.None);
        }
    }

    [AdminExternalPersistenceFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task PostgreSql_AdminBrowserSessionIsSharedRevocableAndAudited()
    {
        await using var storeA = new PostgresAdminBrowserSessionStore(
            NpgsqlDataSource.Create(ConnectionString));
        await using var storeB = new PostgresAdminBrowserSessionStore(
            NpgsqlDataSource.Create(ConnectionString));
        await storeA.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var session = new AdminBrowserSessionRecord(
            new string('a', 64),
            new string('b', 64),
            new AdminPrincipal(
                "external-admin",
                new HashSet<string>([AdminRoles.RoomViewer], StringComparer.Ordinal),
                new HashSet<string>(["local"], StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                "shift-a",
                true),
            new string('c', 64),
            new string('d', 64),
            now,
            now.AddMinutes(10),
            null);
        await storeA.CreateAsync(session, CancellationToken.None);
        var shared = await storeB.GetAsync(session.SessionHash, CancellationToken.None);
        Assert.Equal("external-admin", shared?.Principal.OperatorId);

        await storeB.RecordLoginEventAsync(new AdminLoginSecurityEvent(
            Guid.NewGuid().ToString(), "external-admin", "Succeeded", "SESSION_CREATED",
            session.DeviceHash, session.IpNetworkHash, now, "trace-external-session"), CancellationToken.None);
        await storeA.RevokeAsync(session.SessionHash, now.AddSeconds(1), CancellationToken.None);
        var revoked = await storeB.GetAsync(session.SessionHash, CancellationToken.None);
        Assert.NotNull(revoked?.RevokedAtUtc);
    }

    [AdminExternalPersistenceFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task PostgreSql_CaseCreationIsConcurrentAndIdempotent()
    {
        var now = DateTimeOffset.UtcNow;
        var action = new AdminActionRecord(
            Guid.NewGuid().ToString(),
            AdminManagementActionType.StartDisputeInvestigation,
            "Room",
            $"room-case-{Guid.NewGuid():N}",
            "external-room-operator",
            now,
            now.AddMinutes(5),
            now.AddHours(1),
            now,
            "Investigate a disputed room event timeline",
            $"DISPUTE-{Guid.NewGuid():N}",
            Guid.NewGuid().ToString(),
            12,
            new string('b', 64),
            JsonSerializer.SerializeToElement(new { stateSequence = 12 }),
            AdminActionStatus.AwaitingConfirmation,
            null,
            1);
        await using var actionStore = CreateStore();
        await actionStore.InitializeAsync(CancellationToken.None);
        await actionStore.CreateAsync(
            action,
            CreateAudit(
                action,
                now,
                action.RequestedBy,
                "ActionRequested",
                null,
                JsonSerializer.SerializeToElement(action),
                null),
            CancellationToken.None);
        var approved = action with
        {
            Approval = new AdminActionApproval(
                Guid.NewGuid().ToString(),
                "external-room-approver",
                now.AddSeconds(1),
                ApprovalDecision.Approve,
                "Approved dispute investigation"),
            Status = AdminActionStatus.ApprovedAwaitingExecution,
            Version = 2
        };
        Assert.True(await actionStore.TryTransitionAsync(
            1,
            approved,
            CreateAudit(
                approved,
                now.AddSeconds(1),
                "external-room-approver",
                "ActionApprovalRecorded",
                JsonSerializer.SerializeToElement(action),
                JsonSerializer.SerializeToElement(approved),
                JsonSerializer.SerializeToElement(approved.Approval)),
            CancellationToken.None));

        await using var casesA =
            new PostgresAdminCaseStore(NpgsqlDataSource.Create(ConnectionString));
        await using var casesB =
            new PostgresAdminCaseStore(NpgsqlDataSource.Create(ConnectionString));
        await casesA.InitializeAsync(CancellationToken.None);
        var commandId = Guid.NewGuid().ToString();
        var results = await Task.WhenAll(
            casesA.CreateAsync(
                commandId,
                AdminCaseType.DisputeInvestigation,
                approved,
                now.AddSeconds(1),
                CancellationToken.None),
            casesB.CreateAsync(
                commandId,
                AdminCaseType.DisputeInvestigation,
                approved,
                now.AddSeconds(1),
                CancellationToken.None));

        Assert.Single(results, item => !item.Duplicate);
        Assert.Single(results, item => item.Duplicate);
        Assert.Equal(results[0].Case.CaseId, results[1].Case.CaseId);
        var persisted = Assert.Single(
            await casesB.ListAsync(500, CancellationToken.None),
            item => item.SourceCommandId == commandId);
        Assert.Equal("Open", persisted.Status);
        Assert.Equal(action.TraceId, persisted.TraceId);
        Assert.Equal("external-room-approver", persisted.ApprovedBy);
    }

    [AdminExternalPersistenceFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task PostgreSql_PlayerEvidenceIsConcurrentAndIdempotent()
    {
        var eventId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        var request = new IngestPlayerEvidenceRequest(
            eventId,
            $"player-evidence-{Guid.NewGuid():N}",
            PlayerEvidenceType.PaymentOrder,
            now.AddMinutes(-1),
            $"payment-{Guid.NewGuid():N}",
            JsonSerializer.SerializeToElement(new
            {
                orderReference = $"masked-{Guid.NewGuid():N}",
                amountMinor = 8800,
                currency = "CNY",
                status = "Paid"
            }),
            PlayerEvidenceSensitivity.Financial);
        await using var storeA = new PostgresPlayerEvidenceStore(
            NpgsqlDataSource.Create(ConnectionString));
        await using var storeB = new PostgresPlayerEvidenceStore(
            NpgsqlDataSource.Create(ConnectionString));
        await storeA.InitializeAsync(CancellationToken.None);

        var results = await Task.WhenAll(
            storeA.IngestAsync(
                request,
                now,
                CancellationToken.None),
            storeB.IngestAsync(
                request,
                now,
                CancellationToken.None));

        Assert.Single(results, item => !item.Duplicate);
        Assert.Single(results, item => item.Duplicate);
        var persisted = Assert.Single(
            await storeB.ListAsync(
                request.PlayerId,
                PlayerEvidenceType.PaymentOrder,
                10,
                CancellationToken.None));
        Assert.Equal(eventId, persisted.EventId);
        Assert.Equal(request.SourceReference, persisted.SourceReference);

        var grantId = Guid.NewGuid().ToString();
        var grant = new IngestPlayerChatAccessGrantRequest(
            grantId,
            request.PlayerId,
            $"CHAT-{Guid.NewGuid():N}",
            "external-chat-reviewer",
            "external-player-approver",
            "External dispute requires scoped chat review.",
            $"trace-{Guid.NewGuid():N}",
            now.AddDays(-1),
            now,
            now.AddHours(1),
            ["metadata"]);
        var grantResults = await Task.WhenAll(
            storeA.IngestChatGrantAsync(
                grant,
                now,
                CancellationToken.None),
            storeB.IngestChatGrantAsync(
                grant,
                now,
                CancellationToken.None));
        Assert.Single(grantResults, item => !item.Duplicate);
        Assert.Single(grantResults, item => item.Duplicate);
        var activeGrant = await storeB.GetActiveChatGrantAsync(
            grant.PlayerId,
            grant.TicketId,
            grant.GrantedTo,
            now,
            CancellationToken.None);
        Assert.NotNull(activeGrant);
        Assert.Equal(grant.TraceId, activeGrant.TraceId);
    }

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("ADMIN_TEST_POSTGRES")!;

    private static PostgresAdminActionStore CreateStore() =>
        new(NpgsqlDataSource.Create(ConnectionString));

    private static AdminAuditDraft CreateAudit(
        AdminActionRecord action,
        DateTimeOffset occurredAtUtc,
        string operatorId,
        string operation,
        JsonElement? before,
        JsonElement? after,
        JsonElement? approval) =>
        new(
            occurredAtUtc,
            operatorId,
            operation,
            action.TargetType,
            action.TargetId,
            action.Reason,
            before,
            after,
            approval,
            action.TraceId,
            action.TicketId);
}
