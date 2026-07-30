using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Storage;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Services;

/// <summary>执行单个已审批 Outbox 命令的下游适配器边界。</summary>
public interface IAdminCommandExecutor
{
    /// <summary>
    /// 执行命令并返回成功、可重试性和脱敏后状态。
    /// 实现不得自行更新 Admin 动作/Outbox，事务完成由派发器统一负责。
    /// </summary>
    Task<AdminCommandExecutionResult> ExecuteAsync(
        AdminCommandOutboxRecord command,
        CancellationToken cancellationToken);
}

/// <summary>未配置下游命令适配器时的关闭式实现；所有命令返回不可重试失败且无副作用。</summary>
public sealed class UnsupportedAdminCommandExecutor : IAdminCommandExecutor
{
    /// <inheritdoc/>
    public Task<AdminCommandExecutionResult> ExecuteAsync(
        AdminCommandOutboxRecord command,
        CancellationToken cancellationToken) =>
        Task.FromResult(new AdminCommandExecutionResult(
            false,
            false,
            JsonSerializer.SerializeToElement(new
            {
                command.ActionType,
                status = "AdapterNotConfigured"
            }),
            $"No command adapter is configured for {command.ActionType}."));
}

/// <summary>
/// Admin 事务 Outbox 的单批次派发器。
/// 以 workerId/租约领取命令，调用执行器后原子完成动作、Outbox 和审计；
/// 不确定异常按瞬态失败重试，超过上限或明确永久失败进入终态。
/// </summary>
public sealed class AdminCommandDispatcher(
    IAdminActionStore store,
    IAdminCommandExecutor executor,
    IOptions<AdminOptions> options,
    TimeProvider timeProvider,
    ILogger<AdminCommandDispatcher> logger)
{
    private readonly AdminManagementOptions management = options.Value.Management;

    /// <summary>
    /// 领取最多 10 条到期命令并顺序执行，返回本次领取数量。
    /// 取消会传播且不会错误确认未完成命令，单条结果通过租约所有者条件提交。
    /// </summary>
    public async Task<int> DispatchOnceAsync(
        string workerId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var commands = await store.ClaimOutboxAsync(
            workerId,
            10,
            now,
            now.AddSeconds(management.LeaseSeconds),
            cancellationToken);
        MahjongTelemetry.RecordAdminCommandBatch(commands.Count);
        foreach (var command in commands)
            await DispatchCommandAsync(command, cancellationToken);
        return commands.Count;
    }

    private async Task DispatchCommandAsync(
        AdminCommandOutboxRecord command,
        CancellationToken cancellationToken)
    {
        var action = await store.GetAsync(command.ActionRequestId, cancellationToken);
        if (action is null)
        {
            logger.LogError(
                "Outbox command references missing action OutboxId={OutboxId} ActionRequestId={ActionRequestId}",
                command.OutboxId,
                command.ActionRequestId);
            return;
        }
        if (action.Approval is not null && command.AttemptCount <= 1)
        {
            // 仅首次领取记录批准到开始，重试等待不污染“开始执行”SLI。
            MahjongTelemetry.RecordAdminApprovalToStart(
                action.Approval.ApprovedAtUtc,
                timeProvider.GetUtcNow(),
                action.ActionType.ToString());
        }

        AdminCommandExecutionResult result;
        try
        {
            result = await executor.ExecuteAsync(command, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result = new AdminCommandExecutionResult(
                false,
                true,
                JsonSerializer.SerializeToElement(new { status = "ExecutorException" }),
                exception.Message);
        }

        var now = timeProvider.GetUtcNow();
        if (result.Succeeded)
        {
            MahjongTelemetry.RecordAdminCommandOutcome(
                command.ActionType.ToString(),
                "succeeded");
            var completed = action with
            {
                Status = AdminActionStatus.Succeeded,
                Version = action.Version + 1
            };
            var audit = CreateAudit(
                action,
                completed,
                now,
                "CommandSucceeded",
                result.AfterState,
                null);
            if (!await store.CompleteOutboxAsync(
                    command, completed, audit, now, cancellationToken))
            {
                logger.LogWarning(
                    "Command completion lost lease or action version OutboxId={OutboxId}",
                    command.OutboxId);
            }
            return;
        }

        var terminal = !result.Retryable || command.AttemptCount >= management.MaxAttempts;
        MahjongTelemetry.RecordAdminCommandOutcome(
            command.ActionType.ToString(),
            terminal ? "failed" : "retry_scheduled");
        var failed = terminal
            ? action with
            {
                Status = AdminActionStatus.Failed,
                Version = action.Version + 1
            }
            : null;
        var delaySeconds = Math.Min(
            300,
            management.RetryBaseSeconds * (1 << Math.Min(command.AttemptCount - 1, 6)));
        var error = NormalizeError(result.Error);
        var failureAudit = CreateAudit(
            action,
            failed ?? action,
            now,
            terminal ? "CommandFailed" : "CommandRetryScheduled",
            result.AfterState,
            error);
        if (!await store.FailOutboxAsync(
                command,
                failed,
                failureAudit,
                error,
                now.AddSeconds(delaySeconds),
                terminal,
                cancellationToken))
        {
            logger.LogWarning(
                "Command failure update lost lease or action version OutboxId={OutboxId}",
                command.OutboxId);
        }
    }

    private static AdminAuditDraft CreateAudit(
        AdminActionRecord beforeAction,
        AdminActionRecord afterAction,
        DateTimeOffset now,
        string operation,
        JsonElement domainState,
        string? error) =>
        new(
            now,
            "system:outbox-dispatcher",
            operation,
            beforeAction.TargetType,
            beforeAction.TargetId,
            beforeAction.Reason,
            JsonSerializer.SerializeToElement(beforeAction),
            JsonSerializer.SerializeToElement(new
            {
                action = afterAction,
                domainState,
                error
            }),
            beforeAction.Approval is null
                ? null
                : JsonSerializer.SerializeToElement(beforeAction.Approval),
            beforeAction.TraceId,
            beforeAction.TicketId);

    private static string NormalizeError(string? error)
    {
        var value = new string((error ?? "Unknown command failure")
            .Where(character => !char.IsControl(character))
            .Take(1000)
            .ToArray())
            .Trim();
        return value.Length == 0 ? "Unknown command failure" : value;
    }
}

/// <summary>
/// 周期驱动 AdminCommandDispatcher 的后台服务。
/// 使用实例唯一 workerId；执行开关关闭时不启动，宿主取消后退出，
/// 循环异常记录后继续，避免派发静默停摆。
/// </summary>
public sealed class AdminCommandDispatcherService(
    AdminCommandDispatcher dispatcher,
    IOptions<AdminOptions> options,
    ILogger<AdminCommandDispatcherService> logger) : BackgroundService
{
    private readonly AdminManagementOptions management = options.Value.Management;
    private readonly string workerId =
        $"{Environment.MachineName}-{Guid.NewGuid():N}"[..Math.Min(
            128, Environment.MachineName.Length + 33)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!management.ExecutionEnabled) return;
        logger.LogInformation("Admin command dispatcher started WorkerId={WorkerId}", workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await dispatcher.DispatchOnceAsync(workerId, stoppingToken);
                if (count > 0) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Admin command dispatcher iteration failed.");
            }
            await Task.Delay(
                TimeSpan.FromMilliseconds(management.PollIntervalMilliseconds),
                stoppingToken);
        }
    }
}
