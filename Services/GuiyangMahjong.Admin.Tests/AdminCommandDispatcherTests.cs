// 验证 Admin 命令派发的审批前置、幂等状态转换、失败回退和审计记录完整性。
using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Services;
using GuiyangMahjong.Admin.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Tests;

public sealed class AdminCommandDispatcherTests
{
    [Fact]
    public async Task SuccessfulCommandCompletesOutboxActionAndAuditAtomically()
    {
        var now = new DateTimeOffset(2026, 7, 27, 8, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        var store = new InMemoryAdminActionStore();
        var action = await SeedApprovedActionAsync(store, now);
        var executor = new SequenceCommandExecutor(
            new AdminCommandExecutionResult(
                true,
                false,
                JsonSerializer.SerializeToElement(new { sessionState = "Disconnected" }),
                null));
        var dispatcher = CreateDispatcher(store, executor, time);

        Assert.Equal(1, await dispatcher.DispatchOnceAsync(
            "worker-success", CancellationToken.None));

        var completed = await store.GetAsync(action.ActionRequestId, CancellationToken.None);
        Assert.NotNull(completed);
        Assert.Equal(AdminActionStatus.Succeeded, completed.Status);
        Assert.Equal(3, completed.Version);
        var command = Assert.Single(await store.ListOutboxAsync(10, CancellationToken.None));
        Assert.Equal("Completed", command.Status);
        Assert.Equal(1, command.AttemptCount);
        Assert.Equal(now, command.CompletedAtUtc);
        Assert.Null(command.LockOwner);
        Assert.Contains(
            await store.ListAuditAsync(10, CancellationToken.None),
            record => record.Operation == "CommandSucceeded");
    }

    [Fact]
    public async Task RetryableFailureIsDelayedAndThenCanSucceed()
    {
        var now = new DateTimeOffset(2026, 7, 27, 9, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        var store = new InMemoryAdminActionStore();
        var action = await SeedApprovedActionAsync(store, now);
        var executor = new SequenceCommandExecutor(
            new AdminCommandExecutionResult(
                false,
                true,
                JsonSerializer.SerializeToElement(new { status = "TimedOut" }),
                "upstream timeout"),
            new AdminCommandExecutionResult(
                true,
                false,
                JsonSerializer.SerializeToElement(new { sessionState = "Disconnected" }),
                null));
        var dispatcher = CreateDispatcher(store, executor, time);

        Assert.Equal(1, await dispatcher.DispatchOnceAsync(
            "worker-retry", CancellationToken.None));
        var pending = Assert.Single(
            await store.ListOutboxAsync(10, CancellationToken.None));
        Assert.Equal("Pending", pending.Status);
        Assert.Equal(1, pending.AttemptCount);
        Assert.Equal("upstream timeout", pending.LastError);
        Assert.Equal(0, await dispatcher.DispatchOnceAsync(
            "worker-retry", CancellationToken.None));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await dispatcher.DispatchOnceAsync(
            "worker-retry", CancellationToken.None));

        var completed = await store.GetAsync(action.ActionRequestId, CancellationToken.None);
        Assert.NotNull(completed);
        Assert.Equal(AdminActionStatus.Succeeded, completed.Status);
        var command = Assert.Single(await store.ListOutboxAsync(10, CancellationToken.None));
        Assert.Equal("Completed", command.Status);
        Assert.Equal(2, command.AttemptCount);
        Assert.Contains(
            await store.ListAuditAsync(10, CancellationToken.None),
            record => record.Operation == "CommandRetryScheduled");
    }

    [Fact]
    public async Task NonRetryableFailureEndsActionWithoutRedispatch()
    {
        var now = new DateTimeOffset(2026, 7, 27, 9, 30, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        var store = new InMemoryAdminActionStore();
        var action = await SeedApprovedActionAsync(store, now);
        var dispatcher = CreateDispatcher(
            store,
            new SequenceCommandExecutor(
                new AdminCommandExecutionResult(
                    false,
                    false,
                    JsonSerializer.SerializeToElement(
                        new { status = "AdapterRejected" }),
                    "adapter rejected command")),
            time);

        Assert.Equal(1, await dispatcher.DispatchOnceAsync(
            "worker-terminal", CancellationToken.None));

        var failed = await store.GetAsync(action.ActionRequestId, CancellationToken.None);
        Assert.NotNull(failed);
        Assert.Equal(AdminActionStatus.Failed, failed.Status);
        var command = Assert.Single(await store.ListOutboxAsync(10, CancellationToken.None));
        Assert.Equal("Failed", command.Status);
        Assert.Equal("adapter rejected command", command.LastError);
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await dispatcher.DispatchOnceAsync(
            "worker-terminal", CancellationToken.None));
        Assert.Contains(
            await store.ListAuditAsync(10, CancellationToken.None),
            record => record.Operation == "CommandFailed");
    }

    [Fact]
    public async Task ExpiredLeaseCanBeReclaimedByAnotherWorker()
    {
        var now = new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);
        var store = new InMemoryAdminActionStore();
        await SeedApprovedActionAsync(store, now);

        var first = Assert.Single(await store.ClaimOutboxAsync(
            "worker-a",
            1,
            now,
            now.AddSeconds(5),
            CancellationToken.None));
        Assert.Equal("worker-a", first.LockOwner);
        Assert.Empty(await store.ClaimOutboxAsync(
            "worker-b",
            1,
            now.AddSeconds(4),
            now.AddSeconds(9),
            CancellationToken.None));

        var reclaimed = Assert.Single(await store.ClaimOutboxAsync(
            "worker-b",
            1,
            now.AddSeconds(6),
            now.AddSeconds(11),
            CancellationToken.None));
        Assert.Equal("worker-b", reclaimed.LockOwner);
        Assert.Equal(2, reclaimed.AttemptCount);
    }

    private static AdminCommandDispatcher CreateDispatcher(
        IAdminActionStore store,
        IAdminCommandExecutor executor,
        TimeProvider timeProvider) =>
        new(
            store,
            executor,
            Microsoft.Extensions.Options.Options.Create(new AdminOptions
            {
                Management = new AdminManagementOptions
                {
                    Enabled = true,
                    ExecutionEnabled = true,
                    LeaseSeconds = 30,
                    MaxAttempts = 3,
                    RetryBaseSeconds = 1
                }
            }),
            timeProvider,
            NullLogger<AdminCommandDispatcher>.Instance);

    private static async Task<AdminActionRecord> SeedApprovedActionAsync(
        IAdminActionStore store,
        DateTimeOffset now)
    {
        var action = new AdminActionRecord(
            Guid.NewGuid().ToString(),
            AdminManagementActionType.ForceLogoutPlayer,
            "Player",
            $"player-{Guid.NewGuid():N}",
            "operator",
            now,
            now.AddMinutes(5),
            now.AddHours(1),
            now,
            "Test command dispatch",
            $"TEST-{Guid.NewGuid():N}",
            Guid.NewGuid().ToString(),
            null,
            new string('a', 64),
            JsonSerializer.SerializeToElement(new { sessionState = "Connected" }),
            AdminActionStatus.AwaitingConfirmation,
            null,
            1);
        await store.CreateAsync(
            action,
            CreateAudit(action, now, "ActionRequested"),
            CancellationToken.None);
        var approved = action with
        {
            Status = AdminActionStatus.ApprovedAwaitingExecution,
            Approval = new AdminActionApproval(
                Guid.NewGuid().ToString(),
                "approver",
                now,
                ApprovalDecision.Approve,
                "Approved for test"),
            Version = 2
        };
        Assert.True(await store.TryTransitionAsync(
            1,
            approved,
            CreateAudit(approved, now, "ActionApprovalRecorded"),
            CancellationToken.None));
        return approved;
    }

    private static AdminAuditDraft CreateAudit(
        AdminActionRecord action,
        DateTimeOffset now,
        string operation) =>
        new(
            now,
            "test-operator",
            operation,
            action.TargetType,
            action.TargetId,
            action.Reason,
            null,
            JsonSerializer.SerializeToElement(action),
            action.Approval is null
                ? null
                : JsonSerializer.SerializeToElement(action.Approval),
            action.TraceId,
            action.TicketId);

    private sealed class SequenceCommandExecutor(
        params AdminCommandExecutionResult[] results) : IAdminCommandExecutor
    {
        private readonly Queue<AdminCommandExecutionResult> results = new(results);

        public Task<AdminCommandExecutionResult> ExecuteAsync(
            AdminCommandOutboxRecord command,
            CancellationToken cancellationToken) =>
            Task.FromResult(results.Dequeue());
    }

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset utcNow = initial;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
