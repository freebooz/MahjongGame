using System.Security.Cryptography;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.Allocator.Domain;
using GuiyangMahjong.Allocator.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Allocator.Services;

/// <summary>配置中心发布信封的 Allocator 最小读模型；只读取 Fleet 路由，不依赖配置应用实现。</summary>
public sealed record AgonesFleetConfigurationEnvelope(
    long Version,
    int SchemaVersion,
    JsonElement Payload,
    string PayloadHash,
    string Signature,
    DateTimeOffset PublishedAtUtc,
    long? RollbackOfVersion,
    string ConfigKey);

/// <summary>Allocator 所需的平台配置片段；其余动态配置字段由 Json 序列化器安全忽略。</summary>
public sealed record AgonesFleetPayload(AgonesFleetRoute[] FleetRoutes);

/// <summary>
/// 不可变 Fleet 路由。StopNewAllocations 只拒绝新分配，不回收已运行房间；镜像与规则摘要用于审计，
/// 实际选择必须同时匹配 Build、RuleSet、Protocol 和 Region，防止错误 Fleet 接收流量。
/// </summary>
public sealed record AgonesFleetRoute(
    string RouteId,
    string Fleet,
    string ServerBuild,
    string ServerImageDigest,
    string RuleSetVersion,
    string RuleSetPackageHash,
    string ProtocolVersion,
    string Region,
    string Cell,
    string CanaryGroup,
    string? ExperimentId,
    bool StopNewAllocations);

/// <summary>将分配规格解析为唯一 Fleet；接口隔离便于测试灰度、暂停和 LKG 行为。</summary>
public interface IAgonesFleetRouteResolver
{
    /// <summary>解析新房间路由；无兼容路由、路由歧义或已暂停时抛出 503，不产生 Agones 副作用。</summary>
    string Resolve(AgonesAllocationSpec spec);
}

/// <summary>
/// 线程安全的 Fleet 路由 LKG。新版本必须通过配置键、Schema、正文哈希和 HMAC 签名校验后才能原子切换；
/// 配置中心中断或候选损坏时保留最后有效版本，确保故障不会清空当前发布策略。
/// </summary>
public sealed class AgonesFleetConfigurationState : IAgonesFleetRouteResolver
{
    private static readonly Meter Meter = new("GuiyangMahjong.Allocator.Configuration");
    private readonly object gate = new();
    private readonly AllocatorOptions startup;
    private AgonesFleetRoute[] routes = [];
    private long version;
    private readonly ObservableGauge<long> versionGauge;

    /// <summary>使用启动安全基线创建状态，并注册无高基数标签的当前配置版本指标。</summary>
    public AgonesFleetConfigurationState(IOptions<AllocatorOptions> options)
    {
        startup = options.Value;
        versionGauge = Meter.CreateObservableGauge(
            "mahjong_allocator_config_version",
            () => Version,
            description: "Allocator 当前生效的 Fleet 路由配置版本；不包含玩家或房间高基数标签。");
    }

    /// <summary>返回当前配置版本，供低基数指标和应用回执使用。</summary>
    public long Version { get { lock (gate) return version; } }

    /// <inheritdoc />
    public string Resolve(AgonesAllocationSpec spec)
    {
        AgonesFleetRoute[] snapshot;
        lock (gate) snapshot = routes;
        if (snapshot.Length == 0) return startup.Agones.FleetName;

        var compatible = snapshot.Where(route =>
            string.Equals(route.ServerBuild, spec.BuildVersion, StringComparison.Ordinal)
            && string.Equals(route.RuleSetVersion, spec.RuleSetVersion, StringComparison.Ordinal)
            && string.Equals(route.ProtocolVersion, spec.ProtocolVersion, StringComparison.Ordinal)
            && string.Equals(route.Region, spec.Region, StringComparison.Ordinal)).ToArray();
        if (compatible.Length == 0)
            throw new AllocatorOperationException("No published Fleet route supports this server contract.", 503);
        if (compatible.Length > 1)
            throw new AllocatorOperationException("Published Fleet routing is ambiguous.", 503);
        if (compatible[0].StopNewAllocations)
            throw new AllocatorOperationException("New allocations are paused for this Fleet route.", 503);
        return compatible[0].Fleet;
    }

    /// <summary>验证并应用候选信封；相同版本可安全重放，旧版本和坏签名均不会覆盖 LKG。</summary>
    public async Task<bool> TryApplyAsync(AgonesFleetConfigurationEnvelope envelope, CancellationToken token)
    {
        if (envelope.ConfigKey != "platform.runtime" || envelope.SchemaVersion != 1 || !Verify(envelope)) return false;
        var payload = envelope.Payload.Deserialize<AgonesFleetPayload>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (payload is null || payload.FleetRoutes.Length == 0 || payload.FleetRoutes.Any(route =>
                string.IsNullOrWhiteSpace(route.Fleet) || string.IsNullOrWhiteSpace(route.ServerBuild)
                || string.IsNullOrWhiteSpace(route.RuleSetVersion) || string.IsNullOrWhiteSpace(route.ProtocolVersion)
                || string.IsNullOrWhiteSpace(route.Region))) return false;
        lock (gate)
        {
            if (envelope.Version < version) return false;
            if (envelope.Version == version) return true;
            routes = payload.FleetRoutes;
            version = envelope.Version;
        }
        await PersistAtomicallyAsync(envelope, token);
        return true;
    }

    /// <summary>启动时恢复签名 LKG；文件缺失或损坏时保留静态 FleetName 安全基线。</summary>
    public async Task RestoreAsync(CancellationToken token)
    {
        var path = ResolvePath();
        if (!File.Exists(path)) return;
        try
        {
            AgonesFleetConfigurationEnvelope? envelope;
            await using (var stream = File.OpenRead(path))
                envelope = await JsonSerializer.DeserializeAsync<AgonesFleetConfigurationEnvelope>(stream, cancellationToken: token);
            if (envelope is not null) _ = await TryApplyAsync(envelope, token);
        }
        catch (JsonException) { /* 损坏文件不能阻止静态安全基线启动，也不能成为路由权威。 */ }
        catch (IOException) { /* 持久卷短暂不可用时由后续轮询恢复，当前进程继续使用内存基线。 */ }
    }

    private bool Verify(AgonesFleetConfigurationEnvelope envelope)
    {
        var calculated = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Payload.GetRawText())));
        if (!FixedEquals(calculated, envelope.PayloadHash)) return false;
        var material = $"{envelope.ConfigKey}\n{envelope.Version}\n{envelope.SchemaVersion}\n{envelope.PayloadHash}\n{envelope.PublishedAtUtc:O}\n{envelope.RollbackOfVersion}";
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(startup.Agones.DynamicFleetConfiguration.SigningKey), Encoding.UTF8.GetBytes(material)));
        return FixedEquals(expected, envelope.Signature);
    }

    private async Task PersistAtomicallyAsync(AgonesFleetConfigurationEnvelope envelope, CancellationToken token)
    {
        var path = ResolvePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Environment.ProcessId}.tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(envelope), token);
        File.Move(temporary, path, true);
    }

    private string ResolvePath() => Path.GetFullPath(
        startup.Agones.DynamicFleetConfiguration.LastKnownGoodPath, AppContext.BaseDirectory);
    private static bool FixedEquals(string left, string right) => left.Length == right.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
}

/// <summary>周期拉取 Fleet 路由；失败只记录类型与当前版本，不记录令牌、签名或配置正文。</summary>
public sealed class AgonesFleetConfigurationPoller(
    IHttpClientFactory clients,
    AgonesFleetConfigurationState state,
    IOptions<AllocatorOptions> options,
    ILogger<AgonesFleetConfigurationPoller> logger) : BackgroundService
{
    /// <summary>先恢复 LKG，再按固定间隔拉取；关闭动态配置时只使用静态 FleetName。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await state.RestoreAsync(stoppingToken);
        var config = options.Value.Agones.DynamicFleetConfiguration;
        if (!config.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.PollSeconds));
        do { await PullOnceAsync(stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PullOnceAsync(CancellationToken token)
    {
        try
        {
            var config = options.Value.Agones.DynamicFleetConfiguration;
            var client = clients.CreateClient("configuration-fleet");
            using var request = new HttpRequestMessage(HttpMethod.Get, "/internal/configurations/platform.runtime/current");
            request.Headers.Authorization = new("Bearer", config.ReadToken);
            using var response = await client.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            var envelope = await response.Content.ReadFromJsonAsync<AgonesFleetConfigurationEnvelope>(cancellationToken: token);
            if (envelope is null || !await state.TryApplyAsync(envelope, token))
                logger.LogWarning("Fleet 动态配置被拒绝，继续使用 LKG。ErrorCode={ErrorCode} ConfigVersion={ConfigVersion}", "CONFIG_VERIFY_FAILED", state.Version);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            logger.LogWarning("配置中心不可用，Fleet 路由继续使用 LKG。ErrorCode={ErrorCode} ConfigVersion={ConfigVersion}", exception.GetType().Name, state.Version);
        }
    }
}
