using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Storage;
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
            1);
        var requestedAudit = CreateAudit(
            action, now, "external-operator", "ActionRequested", null,
            JsonSerializer.SerializeToElement(action), null);

        await using var storeA = CreateStore();
        await using var storeB = CreateStore();
        await storeA.InitializeAsync(CancellationToken.None);
        await storeA.CreateAsync(action, requestedAudit, CancellationToken.None);

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

        var claimed = Assert.Single(await storeA.ClaimOutboxAsync(
            "external-worker-a",
            1,
            now.AddSeconds(2),
            now.AddSeconds(32),
            CancellationToken.None));
        Assert.Equal(command.OutboxId, claimed.OutboxId);
        Assert.Empty(await storeB.ClaimOutboxAsync(
            "external-worker-b",
            1,
            now.AddSeconds(3),
            now.AddSeconds(33),
            CancellationToken.None));
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
        Assert.True(await storeB.CheckHealthAsync(CancellationToken.None));
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
