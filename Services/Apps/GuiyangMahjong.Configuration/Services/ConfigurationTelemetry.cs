using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace GuiyangMahjong.Configuration.Services;

/// <summary>
/// 配置治理低基数指标。标签仅包含配置键、结果与服务部署维度；禁止加入玩家 ID、房间 ID、审批人或工单号。
/// </summary>
public static class ConfigurationTelemetry
{
    public const string MeterName = "GuiyangMahjong.Configuration";
    private static readonly Meter Meter = new(MeterName);
    private static readonly ConcurrentDictionary<string, long> CurrentVersions = new(StringComparer.Ordinal);
    private static readonly Counter<long> PublishCounter = Meter.CreateCounter<long>("mahjong.configuration.publish.count");
    private static readonly Counter<long> ApplicationCounter = Meter.CreateCounter<long>("mahjong.configuration.application.count");
    private static readonly ObservableGauge<long> CurrentVersionGauge = Meter.CreateObservableGauge(
        "mahjong.configuration.current.version",
        () => CurrentVersions.Select(item => new Measurement<long>(item.Value, new KeyValuePair<string, object?>("config_key", item.Key))));

    /// <summary>发布或回滚提交成功后更新当前版本；失败事务不会污染指标状态。</summary>
    public static void RecordPublished(string configKey, long version, bool rollback)
    {
        CurrentVersions[configKey] = version;
        PublishCounter.Add(1, new("config_key", configKey), new("operation", rollback ? "rollback" : "publish"));
        _ = CurrentVersionGauge;
    }

    /// <summary>记录服务应用回执，不携带实例、玩家或房间等高基数身份。</summary>
    public static void RecordApplication(string result, string serviceName) =>
        ApplicationCounter.Add(1, new("result", result), new("service_name", serviceName));
}
