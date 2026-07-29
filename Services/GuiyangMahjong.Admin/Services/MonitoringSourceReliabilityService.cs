using System.Diagnostics;
using System.Diagnostics.Metrics;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Services;

/// <summary>
/// 为 Admin 的只读监控下游提供独立硬超时、最后成功快照、来源级熔断和安全错误摘要。
/// 该服务仅缓存调用方明确允许的监控投影，不得用于聊天正文、支付证据或未脱敏身份数据。
/// </summary>
public sealed class MonitoringSourceReliabilityService
{
    private static readonly Meter Meter = new("GuiyangMahjong.Admin.Monitoring", "1.0.0");
    private static readonly Counter<long> TimeoutCounter =
        Meter.CreateCounter<long>("admin_monitoring_source_timeouts");
    private static readonly Counter<long> FailureCounter =
        Meter.CreateCounter<long>("admin_monitoring_source_failures");
    private static readonly Counter<long> CircuitRejectionCounter =
        Meter.CreateCounter<long>("admin_monitoring_circuit_rejections");
    private static readonly Histogram<double> DurationHistogram =
        Meter.CreateHistogram<double>("admin_monitoring_source_duration_ms", "ms");

    private readonly object syncRoot = new();
    private readonly Dictionary<string, SourceState> sources =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, SnapshotEntry> snapshots =
        new(StringComparer.Ordinal);
    private readonly MonitoringReliabilityOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<MonitoringSourceReliabilityService> logger;

    /// <summary>
    /// 创建进程内可靠性协调器；实例必须按 Singleton 注册，才能跨请求维持快照与熔断状态。
    /// </summary>
    public MonitoringSourceReliabilityService(
        IOptions<AdminOptions> options,
        TimeProvider timeProvider,
        ILogger<MonitoringSourceReliabilityService> logger)
    {
        this.options = options.Value.MonitoringReliability;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// 在独立硬超时内执行来源调用。调用失败时优先返回未过期的最后成功快照，否则返回安全空值。
    /// 调用方取消会直接传播且不计为来源故障；下游即使忽略取消，也不会继续占用当前请求线程。
    /// </summary>
    /// <typeparam name="T">只读监控投影类型。</typeparam>
    /// <param name="source">稳定、低基数的来源名称。</param>
    /// <param name="operation">稳定、低基数的操作名称，也是快照隔离键的一部分。</param>
    /// <param name="enabled">来源是否启用；禁用时不会调用下游。</param>
    /// <param name="timeout">本操作独立硬超时。</param>
    /// <param name="operationFactory">接收可取消令牌的下游调用。</param>
    /// <param name="emptyFactory">无可用快照时生成不含敏感数据的安全空值。</param>
    /// <param name="cacheSnapshot">是否允许保存该操作的最后成功快照。</param>
    /// <param name="cancellationToken">Admin 请求取消令牌。</param>
    public async Task<MonitoringSourceResult<T>> ExecuteAsync<T>(
        string source,
        string operation,
        bool enabled,
        TimeSpan timeout,
        Func<CancellationToken, Task<T>> operationFactory,
        Func<T> emptyFactory,
        bool cacheSnapshot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(operationFactory);
        ArgumentNullException.ThrowIfNull(emptyFactory);

        var snapshotKey = $"{source}:{operation}";
        var now = timeProvider.GetUtcNow();
        if (!enabled)
        {
            lock (syncRoot)
            {
                var disabled = GetOrCreateState(source);
                disabled.Enabled = false;
                disabled.Status = "Unavailable";
                disabled.ErrorCode = "SOURCE_DISABLED";
                disabled.Message = "该监控来源在当前环境未启用。";
                disabled.ObservedAtUtc = now;
                return new MonitoringSourceResult<T>(
                    emptyFactory(),
                    CreateHealth(source, disabled, now, null),
                    false);
            }
        }

        MonitoringSourceResult<T>? rejected;
        lock (syncRoot)
        {
            var state = GetOrCreateState(source);
            state.Enabled = true;
            rejected = TryRejectByCircuit<T>(
                source, snapshotKey, state, now, emptyFactory);
        }
        if (rejected is not null)
        {
            CircuitRejectionCounter.Add(1, new KeyValuePair<string, object?>("source", source));
            return rejected;
        }

        var started = Stopwatch.GetTimestamp();
        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        Task<T>? pendingTask = null;
        try
        {
            pendingTask = operationFactory(timeoutSource.Token);
            var value = await pendingTask.WaitAsync(timeout, cancellationToken);
            var completedAt = timeProvider.GetUtcNow();
            DurationHistogram.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("source", source),
                new KeyValuePair<string, object?>("operation", operation),
                new KeyValuePair<string, object?>("outcome", "success"));
            lock (syncRoot)
            {
                var state = GetOrCreateState(source);
                RecordSuccess(snapshotKey, state, value!, cacheSnapshot, completedAt);
                return new MonitoringSourceResult<T>(
                    value,
                    CreateHealth(source, state, completedAt, completedAt),
                    true);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timeoutSource.Cancel();
            ObserveLateCompletion(pendingTask);
            return RecordFailure(
                source,
                operation,
                snapshotKey,
                "SOURCE_TIMEOUT",
                "下游监控请求超时，已使用可用的最后成功快照。",
                true,
                emptyFactory,
                started);
        }
        catch (TimeoutException)
        {
            timeoutSource.Cancel();
            ObserveLateCompletion(pendingTask);
            return RecordFailure(
                source,
                operation,
                snapshotKey,
                "SOURCE_TIMEOUT",
                "下游监控请求超时，已使用可用的最后成功快照。",
                true,
                emptyFactory,
                started);
        }
        catch (OperationCanceledException)
        {
            // 调用方主动取消属于请求生命周期，不应污染来源失败计数或打开熔断器。
            throw;
        }
        catch (Exception)
        {
            return RecordFailure(
                source,
                operation,
                snapshotKey,
                "SOURCE_REQUEST_FAILED",
                "下游监控请求失败，已使用可用的最后成功快照。",
                false,
                emptyFactory,
                started);
        }
    }

    /// <summary>
    /// 返回指定来源最近一次安全健康快照；未访问来源会显示为尚未观测，不主动触发网络请求。
    /// </summary>
    public MonitoringSourceHealth GetHealth(string source, bool enabled)
    {
        var now = timeProvider.GetUtcNow();
        lock (syncRoot)
        {
            var state = GetOrCreateState(source);
            state.Enabled = enabled;
            if (!enabled)
            {
                state.Status = "Unavailable";
                state.ErrorCode = "SOURCE_DISABLED";
                state.Message = "该监控来源在当前环境未启用。";
            }
            return CreateHealth(source, state, now, state.LastSuccessAtUtc);
        }
    }

    /// <summary>
    /// 判断来源是否具有本次请求刚取得的实时值；高危管理操作必须使用此结果而不能依赖快照。
    /// </summary>
    public static void RequireLive(
        IEnumerable<MonitoringSourceResult<object?>> results)
    {
        if (results.Any(result => !result.IsLive))
            throw new MonitoringFreshDataRequiredException();
    }

    private MonitoringSourceResult<T>? TryRejectByCircuit<T>(
        string source,
        string snapshotKey,
        SourceState state,
        DateTimeOffset now,
        Func<T> emptyFactory)
    {
        if (state.CircuitState == "Open" && now < state.OpenUntilUtc)
        {
            return CreateFallback(
                source,
                snapshotKey,
                state,
                now,
                "CIRCUIT_OPEN",
                "下游监控熔断器已打开，当前未发起新的网络请求。",
                emptyFactory);
        }
        if (state.CircuitState == "Open")
        {
            // 只允许一个半开探测进入下游，其余并发请求继续走快照，避免恢复瞬间形成惊群。
            state.CircuitState = "HalfOpen";
            state.HalfOpenProbeInProgress = true;
            return null;
        }
        if (state.CircuitState == "HalfOpen" && state.HalfOpenProbeInProgress)
        {
            return CreateFallback(
                source,
                snapshotKey,
                state,
                now,
                "CIRCUIT_HALF_OPEN",
                "下游监控正在执行恢复探测，当前使用最后成功快照。",
                emptyFactory);
        }
        return null;
    }

    private void RecordSuccess<T>(
        string snapshotKey,
        SourceState state,
        T value,
        bool cacheSnapshot,
        DateTimeOffset completedAt)
    {
        state.Status = "Healthy";
        state.ErrorCode = null;
        state.Message = "来源实时数据正常。";
        state.ObservedAtUtc = completedAt;
        state.LastSuccessAtUtc = completedAt;
        state.ConsecutiveFailures = 0;
        state.CircuitState = "Closed";
        state.OpenUntilUtc = null;
        state.HalfOpenProbeInProgress = false;
        state.OpenCount = 0;
        state.SnapshotVersion++;
        if (!cacheSnapshot) return;

        snapshots[snapshotKey] =
            new SnapshotEntry(value!, completedAt, state.SnapshotVersion);
        TrimSnapshots();
    }

    private MonitoringSourceResult<T> RecordFailure<T>(
        string source,
        string operation,
        string snapshotKey,
        string errorCode,
        string message,
        bool timedOut,
        Func<T> emptyFactory,
        long started)
    {
        var failedAt = timeProvider.GetUtcNow();
        DurationHistogram.Record(
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            new KeyValuePair<string, object?>("source", source),
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("outcome", timedOut ? "timeout" : "failure"));
        FailureCounter.Add(1, new KeyValuePair<string, object?>("source", source));
        if (timedOut)
            TimeoutCounter.Add(1, new KeyValuePair<string, object?>("source", source));

        lock (syncRoot)
        {
            var state = GetOrCreateState(source);
            state.FailureCount++;
            if (timedOut) state.TimeoutCount++;
            state.ConsecutiveFailures++;
            state.ErrorCode = errorCode;
            state.Message = message;
            state.ObservedAtUtc = failedAt;
            state.HalfOpenProbeInProgress = false;
            if (state.ConsecutiveFailures >= options.CircuitFailureThreshold
                || state.CircuitState == "HalfOpen")
            {
                OpenCircuit(state, failedAt);
            }
            logger.LogWarning(
                "监控来源调用失败。Source={Source} Operation={Operation} ErrorCode={ErrorCode} CircuitState={CircuitState}",
                source,
                operation,
                errorCode,
                state.CircuitState);
            return CreateFallback(
                source,
                snapshotKey,
                state,
                failedAt,
                errorCode,
                message,
                emptyFactory);
        }
    }

    private MonitoringSourceResult<T> CreateFallback<T>(
        string source,
        string snapshotKey,
        SourceState state,
        DateTimeOffset now,
        string errorCode,
        string message,
        Func<T> emptyFactory)
    {
        state.ErrorCode = errorCode;
        state.Message = message;
        state.ObservedAtUtc = now;
        if (snapshots.TryGetValue(snapshotKey, out var snapshot))
        {
            var age = now - snapshot.ObservedAtUtc;
            if (age <= TimeSpan.FromSeconds(options.SnapshotTtlSeconds)
                && snapshot.Value is T typedValue)
            {
                state.Status = "Stale";
                return new MonitoringSourceResult<T>(
                    typedValue,
                    CreateHealth(source, state, now, snapshot.ObservedAtUtc),
                    false);
            }
            // TTL 到期后立即清除快照，保证过期的敏感监控投影不会继续驻留或被误用。
            snapshots.Remove(snapshotKey);
        }

        state.Status = "Unavailable";
        return new MonitoringSourceResult<T>(
            emptyFactory(),
            CreateHealth(source, state, now, null),
            false);
    }

    private void OpenCircuit(SourceState state, DateTimeOffset now)
    {
        state.OpenCount++;
        var exponent = Math.Min(state.OpenCount - 1, 10);
        var breakSeconds = Math.Min(
            options.CircuitMaxBreakSeconds,
            options.CircuitBreakSeconds * Math.Pow(2, exponent));
        state.CircuitState = "Open";
        state.OpenUntilUtc = now.AddSeconds(breakSeconds);
    }

    private MonitoringSourceHealth CreateHealth(
        string source,
        SourceState state,
        DateTimeOffset now,
        DateTimeOffset? dataObservedAtUtc)
    {
        double? dataAge = dataObservedAtUtc.HasValue
            ? Math.Max(0, (now - dataObservedAtUtc.Value).TotalSeconds)
            : null;
        return new MonitoringSourceHealth(
            source,
            state.Status,
            state.Enabled,
            state.ObservedAtUtc == default ? now : state.ObservedAtUtc,
            state.LastSuccessAtUtc,
            dataAge,
            options.StaleAfterSeconds,
            state.ErrorCode,
            state.Message,
            state.CircuitState,
            state.SnapshotVersion,
            state.TimeoutCount,
            state.FailureCount);
    }

    private SourceState GetOrCreateState(string source)
    {
        if (sources.TryGetValue(source, out var state)) return state;
        state = new SourceState
        {
            Status = "Degraded",
            Message = "尚未完成首次来源观测。",
            CircuitState = "Closed"
        };
        sources[source] = state;
        return state;
    }

    private void TrimSnapshots()
    {
        while (snapshots.Count > options.MaxSnapshotEntries)
        {
            var oldestKey = snapshots.MinBy(item => item.Value.ObservedAtUtc).Key;
            snapshots.Remove(oldestKey);
        }
    }

    private static void ObserveLateCompletion(Task? pendingTask)
    {
        if (pendingTask is null) return;
        // WaitAsync 超时不会替我们观察一个故意忽略取消的任务，延续任务负责吞掉其最终异常。
        _ = pendingTask.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class SourceState
    {
        public bool Enabled { get; set; } = true;
        public string Status { get; set; } = "Degraded";
        public DateTimeOffset ObservedAtUtc { get; set; }
        public DateTimeOffset? LastSuccessAtUtc { get; set; }
        public string? ErrorCode { get; set; }
        public string Message { get; set; } = string.Empty;
        public string CircuitState { get; set; } = "Closed";
        public DateTimeOffset? OpenUntilUtc { get; set; }
        public bool HalfOpenProbeInProgress { get; set; }
        public int ConsecutiveFailures { get; set; }
        public int OpenCount { get; set; }
        public long SnapshotVersion { get; set; }
        public long TimeoutCount { get; set; }
        public long FailureCount { get; set; }
    }

    private sealed record SnapshotEntry(
        object Value,
        DateTimeOffset ObservedAtUtc,
        long Version);
}

/// <summary>
/// 表示高危管理操作未取得实时权威状态；调用方必须拒绝继续，而不能把缓存快照当作当前状态。
/// </summary>
public sealed class MonitoringFreshDataRequiredException : Exception
{
    /// <summary>使用固定中文运维提示创建异常，避免暴露具体下游地址和原始异常。</summary>
    public MonitoringFreshDataRequiredException()
        : base("实时权威状态不可用，已拒绝基于陈旧监控数据执行高危操作。")
    {
    }
}

/// <summary>
/// 表示读取详情所必需的权威主数据来源当前不可用；与业务对象不存在的 404 语义严格区分。
/// </summary>
public sealed class MonitoringSourceUnavailableException : Exception
{
    /// <summary>发生故障的稳定来源名称，不包含内部地址、集群名或其他敏感配置。</summary>
    public string SourceName { get; }

    /// <summary>用安全中文消息创建来源不可用异常。</summary>
    public MonitoringSourceUnavailableException(string source)
        : base($"监控来源 {source} 当前不可用，请稍后重试。")
    {
        SourceName = source;
    }
}
