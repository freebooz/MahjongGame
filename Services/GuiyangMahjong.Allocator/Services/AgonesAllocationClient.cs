// Agones 分配客户端：调用 Kubernetes/Agones Allocation API 获取可用 Dedicated Server。
// 网络调用必须有超时、取消和错误分类；返回地址与实例标识需验证后才能写入 Allocator 状态。
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.Allocator.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Allocator.Services;

/// <summary>
/// 提交给 Agones 的游戏服分配规格。
/// 标签将房间、匹配和实例关联到编排资源；内部 Lobby 地址和构建版本通过受控注解传入。
/// </summary>
public sealed record AgonesAllocationSpec(
    string RoomId,
    string MatchId,
    string ServerInstanceId,
    string RegistrationCredential,
    string LobbyInternalUrl,
    string BuildVersion,
    long RoomEpoch = 1,
    string GameType = "guiyang-zhua-ji",
    string Region = "local",
    string RuleSetVersion = "guiyang-zhuoji-v1",
    string ProtocolVersion = "1",
    int RequestedCapacity = 4,
    long FencingToken = 1);

/// <summary>Agones 分配结果；名称用于后续状态/关闭，地址和端口是客户端可达入口。</summary>
public sealed record AgonesAllocationResult(string GameServerName, string Address, int Port);

/// <summary>隔离 Kubernetes Agones API 与 Allocator 领域逻辑的编排客户端边界。</summary>
public interface IAgonesAllocationClient
{
    /// <summary>分配一个 Ready GameServer；无容量或响应不完整时失败且不伪造地址。</summary>
    Task<AgonesAllocationResult> AllocateAsync(AgonesAllocationSpec spec, CancellationToken cancellationToken);

    /// <summary>读取指定资源的 Agones 状态；资源不存在返回空。</summary>
    Task<string?> GetGameServerStateAsync(string gameServerName, CancellationToken cancellationToken);

    /// <summary>请求关闭指定资源；重复关闭已终止资源必须安全。</summary>
    Task ShutdownAsync(string gameServerName, CancellationToken cancellationToken);

    /// <summary>验证 Agones API、命名空间和服务身份是否具备最小必要访问能力。</summary>
    Task<bool> CheckReadyAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 通过 Kubernetes ServiceAccount 调用 Agones REST API 的生产实现。
/// 客户端限制在配置命名空间，响应必须校验名称、地址和端口后才能返回。
/// </summary>
public sealed class KubernetesAgonesAllocationClient : IAgonesAllocationClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AllocatorOptions options;
    private readonly HttpClient client;

    /// <summary>建立使用集群内身份的 HTTP 客户端；配置错误会在首次操作或就绪检查中显式失败。</summary>
    public KubernetesAgonesAllocationClient(IOptions<AllocatorOptions> options)
    {
        this.options = options.Value;
        var agones = this.options.Agones;
        var handler = new HttpClientHandler();
        if (File.Exists(agones.ServiceAccountCaPath))
        {
            // Kubernetes projects a CA certificate only; CreateFromPemFile also attempts to
            // locate a private key and rejects this valid service-account trust bundle.
            var root = X509Certificate2.CreateFromPem(
                File.ReadAllText(agones.ServiceAccountCaPath));
            handler.ServerCertificateCustomValidationCallback = (_, certificate, chain, errors) =>
            {
                if (certificate is null || chain is null) return false;
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(root);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(new X509Certificate2(certificate))
                       && errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors;
            };
        }
        client = new HttpClient(handler)
        {
            BaseAddress = new Uri(agones.ApiServer.TrimEnd('/') + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(agones.RequestTimeoutSeconds)
        };
    }

    /// <inheritdoc/>
    public async Task<AgonesAllocationResult> AllocateAsync(
        AgonesAllocationSpec spec, CancellationToken cancellationToken)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mahjong.freebooz/room-id"] = spec.RoomId,
            ["mahjong.freebooz/match-id"] = spec.MatchId,
            ["mahjong.freebooz/server-instance-id"] = spec.ServerInstanceId,
            ["mahjong.freebooz/registration-credential"] = spec.RegistrationCredential,
            ["mahjong.freebooz/lobby-internal-url"] = spec.LobbyInternalUrl,
            ["mahjong.freebooz/build-version"] = spec.BuildVersion,
            ["mahjong.freebooz/room-epoch"] =
                spec.RoomEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["mahjong.freebooz/fencing-token"] =
                spec.FencingToken.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["mahjong.freebooz/ruleset-version"] = spec.RuleSetVersion,
            ["mahjong.freebooz/protocol-version"] = spec.ProtocolVersion,
            ["mahjong.freebooz/requested-capacity"] =
                spec.RequestedCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var body = new
        {
            apiVersion = "allocation.agones.dev/v1",
            kind = "GameServerAllocation",
            metadata = new { generateName = "guiyang-mahjong-", @namespace = options.Agones.Namespace },
            spec = new
            {
                scheduling = "Packed",
                selectors = new[] { new { matchLabels = new Dictionary<string, string>
                {
                    ["agones.dev/fleet"] = options.Agones.FleetName,
                    ["mahjong.freebooz/game"] = spec.GameType,
                    ["mahjong.freebooz/region"] = spec.Region,
                    ["mahjong.freebooz/server-build"] = spec.BuildVersion,
                    ["mahjong.freebooz/ruleset-version"] = spec.RuleSetVersion,
                    ["mahjong.freebooz/protocol-version"] = spec.ProtocolVersion,
                    ["mahjong.freebooz/capacity"] =
                        spec.RequestedCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture)
                } } },
                metadata = new { labels = new Dictionary<string, string>
                {
                    ["mahjong.freebooz/allocation-source"] = "lobby"
                }, annotations }
            }
        };
        using var request = await CreateRequestAsync(
            HttpMethod.Post,
            $"apis/allocation.agones.dev/v1/namespaces/{Uri.EscapeDataString(options.Agones.Namespace)}/gameserverallocations",
            cancellationToken);
        request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Agones allocation failed with HTTP {(int)response.StatusCode}.");
        using var document = JsonDocument.Parse(payload);
        var status = document.RootElement.GetProperty("status");
        if (!string.Equals(status.GetProperty("state").GetString(), "Allocated", StringComparison.Ordinal))
            throw new GuiyangMahjong.Allocator.Domain.AllocatorOperationException(
                "Agones has no compatible Ready GameServer capacity.", 503);
        var name = status.GetProperty("gameServerName").GetString();
        var address = status.GetProperty("address").GetString();
        var port = status.GetProperty("ports").EnumerateArray()
            .OrderByDescending(item => string.Equals(item.GetProperty("name").GetString(), "game", StringComparison.Ordinal))
            .Select(item => item.GetProperty("port").GetInt32())
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(address) || port is < 1 or > 65535)
            throw new InvalidDataException("Agones returned an incomplete allocation response.");
        return new AgonesAllocationResult(name, address, port);
    }

    /// <inheritdoc/>
    public async Task ShutdownAsync(string gameServerName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gameServerName)) return;
        using var request = await CreateRequestAsync(
            HttpMethod.Delete,
            $"apis/agones.dev/v1/namespaces/{Uri.EscapeDataString(options.Agones.Namespace)}/gameservers/{Uri.EscapeDataString(gameServerName)}",
            cancellationToken);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            throw new HttpRequestException($"Agones shutdown failed with HTTP {(int)response.StatusCode}.");
    }

    /// <inheritdoc/>
    public async Task<string?> GetGameServerStateAsync(
        string gameServerName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gameServerName)) return null;
        using var request = await CreateRequestAsync(
            HttpMethod.Get,
            $"apis/agones.dev/v1/namespaces/{Uri.EscapeDataString(options.Agones.Namespace)}/gameservers/{Uri.EscapeDataString(gameServerName)}",
            cancellationToken);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Agones reconciliation failed with HTTP {(int)response.StatusCode}.");
        await using var payload = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(payload, cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("status").GetProperty("state").GetString();
    }

    /// <inheritdoc/>
    public async Task<bool> CheckReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = await CreateRequestAsync(
                HttpMethod.Get,
                $"apis/agones.dev/v1/namespaces/{Uri.EscapeDataString(options.Agones.Namespace)}/fleets/{Uri.EscapeDataString(options.Agones.FleetName)}",
                cancellationToken);
            using var response = await client.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or HttpRequestException
                                           or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method, string relativeUrl, CancellationToken cancellationToken)
    {
        var token = (await File.ReadAllTextAsync(
            options.Agones.ServiceAccountTokenPath, cancellationToken)).Trim();
        if (token.Length < 16) throw new InvalidDataException("Kubernetes service account token is unavailable.");
        var request = new HttpRequestMessage(method, relativeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    /// <summary>释放内部 HTTP 连接资源；释放后不得再次调用该客户端。</summary>
    public void Dispose() => client.Dispose();
}

/// <summary>Agones 未启用时的显式关闭实现；分配失败、状态为空、关闭幂等且就绪为 false。</summary>
public sealed class DisabledAgonesAllocationClient : IAgonesAllocationClient
{
    /// <inheritdoc/>
    public Task<AgonesAllocationResult> AllocateAsync(AgonesAllocationSpec spec, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Agones allocator backend is disabled.");

    /// <inheritdoc/>
    public Task<string?> GetGameServerStateAsync(string gameServerName, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    /// <inheritdoc/>
    public Task ShutdownAsync(string gameServerName, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<bool> CheckReadyAsync(CancellationToken cancellationToken) => Task.FromResult(false);
}
