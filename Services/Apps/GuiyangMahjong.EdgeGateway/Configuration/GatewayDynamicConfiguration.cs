using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.EdgeGateway.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.EdgeGateway.Configuration;

/// <summary>配置中心发布信封的网关最小读模型；忽略 Fleet、房间模板等非网关职责字段。</summary>
public sealed record GatewayConfigurationEnvelope(
    long Version,
    int SchemaVersion,
    JsonElement Payload,
    string PayloadHash,
    string Signature,
    DateTimeOffset PublishedAtUtc,
    long? RollbackOfVersion,
    string ConfigKey);

/// <summary>配置正本中的客户端兼容字段；其他服务拥有的字段不会进入网关运行状态。</summary>
public sealed record GatewayClientPayload(GatewayClientPolicy Client, int ApiProtocolVersion);

/// <summary>可热切换的客户端版本和协议门禁；阻断版本优先于最低版本判断。</summary>
public sealed record GatewayClientPolicy(
    string MinimumVersion,
    string RecommendedVersion,
    string[] BlockedVersions,
    string[] SupportedProtocolVersions);

/// <summary>
/// EdgeGateway 的原子动态配置状态。启动时以静态配置作为安全基线，只有哈希、签名和 Schema 全部通过才切换；
/// 配置中心不可用或新版本损坏时继续提供最后有效策略。
/// </summary>
public sealed class GatewayConfigurationState(IOptions<EdgeGatewayOptions> options)
{
    private readonly object gate = new();
    private readonly EdgeGatewayOptions startup = options.Value;
    private ClientContractOptions current = options.Value.ClientContract;
    private long configVersion;

    /// <summary>返回同一锁保护下的兼容策略与版本快照，防止单个请求观察到半切换状态。</summary>
    public (ClientContractOptions Contract, long ConfigVersion) Snapshot()
    {
        lock (gate) return (current, configVersion);
    }

    /// <summary>验证并原子应用候选版本；旧版本、坏签名和坏 Schema 都不会覆盖 LKG。</summary>
    public async Task<bool> TryApplyAsync(GatewayConfigurationEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.ConfigKey != "platform.runtime" || envelope.SchemaVersion != 1 || !Verify(envelope)) return false;
        var payload = envelope.Payload.Deserialize<GatewayClientPayload>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (payload is null || !Version.TryParse(payload.Client.MinimumVersion, out _)
            || payload.Client.SupportedProtocolVersions.Length == 0) return false;
        var next = new ClientContractOptions
        {
            MinimumClientVersion = payload.Client.MinimumVersion,
            RecommendedClientVersion = payload.Client.RecommendedVersion,
            BlockedVersions = payload.Client.BlockedVersions,
            SupportedProtocolVersions = payload.Client.SupportedProtocolVersions,
            AllowedChannels = startup.ClientContract.AllowedChannels,
            AllowedPlatforms = startup.ClientContract.AllowedPlatforms
        };
        lock (gate)
        {
            if (envelope.Version <= configVersion) return envelope.Version == configVersion;
            current = next;
            configVersion = envelope.Version;
        }
        await PersistAtomicallyAsync(envelope, cancellationToken);
        return true;
    }

    /// <summary>启动时尝试恢复磁盘 LKG；文件缺失或损坏时保留静态安全基线。</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        var path = ResolvePath();
        if (!File.Exists(path)) return;
        try
        {
            GatewayConfigurationEnvelope? envelope;
            // Windows 不允许在读句柄仍打开时原子替换同一文件；先完整读取并关闭，再进入统一验签/应用路径。
            await using (var stream = File.OpenRead(path))
                envelope = await JsonSerializer.DeserializeAsync<GatewayConfigurationEnvelope>(stream, cancellationToken: cancellationToken);
            if (envelope is not null) _ = await TryApplyAsync(envelope, cancellationToken);
        }
        catch (JsonException) { /* 损坏 LKG 不得阻止安全基线启动，也不能覆盖现有状态。 */ }
        catch (IOException) { /* 短暂卷故障由后续轮询修复，当前进程继续使用内存基线。 */ }
    }

    private bool Verify(GatewayConfigurationEnvelope envelope)
    {
        var calculatedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Payload.GetRawText())));
        if (!FixedEquals(calculatedHash, envelope.PayloadHash)) return false;
        var material = $"{envelope.ConfigKey}\n{envelope.Version}\n{envelope.SchemaVersion}\n{envelope.PayloadHash}\n{envelope.PublishedAtUtc:O}\n{envelope.RollbackOfVersion}";
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(startup.DynamicConfiguration.SigningKey), Encoding.UTF8.GetBytes(material)));
        return FixedEquals(signature, envelope.Signature);
    }

    private async Task PersistAtomicallyAsync(GatewayConfigurationEnvelope envelope, CancellationToken cancellationToken)
    {
        var path = ResolvePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Environment.ProcessId}.tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(envelope), cancellationToken);
        File.Move(temporary, path, true);
    }

    private string ResolvePath() => Path.GetFullPath(startup.DynamicConfiguration.LastKnownGoodPath, AppContext.BaseDirectory);
    private static bool FixedEquals(string left, string right) => left.Length == right.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
}

/// <summary>周期拉取已发布版本；任何失败只记录错误码和现用版本，不记录凭据或配置正本。</summary>
public sealed class GatewayConfigurationPoller(
    IHttpClientFactory clients,
    GatewayConfigurationState state,
    IOptions<EdgeGatewayOptions> options,
    ILogger<GatewayConfigurationPoller> logger) : BackgroundService
{
    /// <summary>先恢复 LKG 再轮询；配置中心不可用时以固定间隔重试且不中断玩家流量。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await state.RestoreAsync(stoppingToken);
        if (!options.Value.DynamicConfiguration.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.DynamicConfiguration.PollSeconds));
        do
        {
            await PullOnceAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PullOnceAsync(CancellationToken token)
    {
        try
        {
            var client = clients.CreateClient("configuration");
            using var request = new HttpRequestMessage(HttpMethod.Get, "/internal/configurations/platform.runtime/current");
            request.Headers.Authorization = new("Bearer", options.Value.DynamicConfiguration.ReadToken);
            using var response = await client.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            var envelope = await response.Content.ReadFromJsonAsync<GatewayConfigurationEnvelope>(cancellationToken: token);
            if (envelope is null || !await state.TryApplyAsync(envelope, token))
                logger.LogWarning("动态配置被拒绝，继续使用 LKG。ErrorCode={ErrorCode} ConfigVersion={ConfigVersion}", "CONFIG_VERIFY_FAILED", state.Snapshot().ConfigVersion);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            logger.LogWarning("配置中心不可用，继续使用 LKG。ErrorCode={ErrorCode} ConfigVersion={ConfigVersion}", exception.GetType().Name, state.Snapshot().ConfigVersion);
        }
    }
}
