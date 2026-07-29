namespace GuiyangMahjong.Admin.Domain;

/// <summary>
/// 单个监控数据源的健康状态；状态只允许 Healthy、Degraded、Unavailable 和 Stale。
/// </summary>
/// <param name="Source">稳定来源标识，不包含地址、集群动态名称或凭据。</param>
/// <param name="Status">当前健康状态；Stale 表示响应使用了最后成功快照。</param>
/// <param name="Enabled">来源是否在当前环境启用，未启用来源不参与部分成功判定。</param>
/// <param name="ObservedAtUtc">Admin 最近一次更新该来源状态的时间。</param>
/// <param name="LastSuccessAtUtc">最近一次成功完成下游请求的时间。</param>
/// <param name="DataAgeSeconds">当前返回数据距成功观测的年龄，单位秒。</param>
/// <param name="StaleAfterSeconds">页面必须标记数据陈旧的阈值，单位秒。</param>
/// <param name="ErrorCode">不含地址与异常正文的受控错误代码。</param>
/// <param name="Message">面向中文运维人员的安全摘要，不包含内部端点或凭据。</param>
/// <param name="CircuitState">Closed、Open 或 HalfOpen。</param>
/// <param name="SnapshotVersion">来源级单调快照版本；零表示尚无成功快照。</param>
/// <param name="TimeoutCount">进程生命周期内该来源累计超时次数。</param>
/// <param name="FailureCount">进程生命周期内该来源累计失败次数。</param>
public sealed record MonitoringSourceHealth(
    string Source,
    string Status,
    bool Enabled,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    double? DataAgeSeconds,
    int StaleAfterSeconds,
    string? ErrorCode,
    string Message,
    string CircuitState,
    long SnapshotVersion,
    long TimeoutCount,
    long FailureCount);

/// <summary>
/// 聚合响应的可靠性元数据；业务数据与来源健康信息使用同一生成时刻。
/// </summary>
/// <param name="GeneratedAtUtc">本次 Admin 聚合响应生成时间。</param>
/// <param name="Partial">至少一个已启用且必需的来源未返回实时数据时为 true。</param>
/// <param name="SafeForHighRiskActions">仅当相关来源均为实时健康状态时为 true。</param>
/// <param name="Sources">参与本次聚合或由管理台展示的来源健康列表。</param>
public sealed record MonitoringReliabilityMetadata(
    DateTimeOffset GeneratedAtUtc,
    bool Partial,
    bool SafeForHighRiskActions,
    MonitoringSourceHealth[] Sources);

/// <summary>
/// 来源调用的内部结果，携带数据值及其是否来自实时请求，供聚合层执行部分成功策略。
/// </summary>
/// <typeparam name="T">不会跨越授权边界的只读监控数据类型。</typeparam>
/// <param name="Value">实时值、最后成功快照或调用方提供的安全空值。</param>
/// <param name="Health">完成当前操作后的来源健康状态。</param>
/// <param name="IsLive">只有本次下游请求成功时才为 true；缓存命中始终为 false。</param>
public sealed record MonitoringSourceResult<T>(
    T Value,
    MonitoringSourceHealth Health,
    bool IsLive);
