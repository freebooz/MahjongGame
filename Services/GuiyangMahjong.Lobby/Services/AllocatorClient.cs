// Lobby 到 Allocator 的客户端边界：负责申请游戏服、确认注册和报告实例释放或故障。
// 调用必须携带服务身份、请求超时和 TraceId；网络不确定结果不能直接视为分配失败并重复创建实例。
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Services;

/// <summary>Lobby 需要的游戏服分配投影；端口是客户端连接端口，State 来自 Allocator 状态机。</summary>
public sealed record AllocatorAllocation(
    string RequestId,
    string RoomId,
    string ServerInstanceId,
    int Port,
    string State,
    long RoomEpoch = 1,
    string? AllocationId = null,
    long FencingToken = 1);

/// <summary>Allocator 接受实例注册后的回执；心跳凭据只能转交目标 Dedicated Server。</summary>
public sealed record AllocatorRegistrationAck(
    string RequestId,
    string ServerInstanceId,
    bool Accepted,
    int HeartbeatIntervalSeconds,
    string HeartbeatCredential,
    long RoomEpoch = 1,
    long FencingToken = 1);

/// <summary>
/// Lobby 调用 Allocator 的最小客户端契约。
/// requestId 在跨服务重试中保持不变；所有方法必须传播取消令牌和请求追踪信息。
/// </summary>
public interface IAllocatorClient
{
    /// <summary>指示当前环境是否启用外部 Allocator。</summary>
    bool Enabled { get; }

    /// <summary>检查 Allocator 就绪性；只用于探针，不创建或改变实例。</summary>
    Task<bool> CheckReadinessAsync(CancellationToken cancellationToken);

    /// <summary>为房间/匹配申请实例；相同 requestId 的重试必须返回同一业务分配。</summary>
    Task<AllocatorAllocation> AllocateAsync(
        string requestId,
        string roomId,
        string matchId,
        CancellationToken cancellationToken);

    /// <summary>
    /// 为指定 RoomEpoch 申请实例；默认实现兼容旧测试替身，但生产 HTTP 实现必须向 Allocator 传播 Epoch。
    /// </summary>
    Task<AllocatorAllocation> AllocateForEpochAsync(
        string requestId,
        string roomId,
        string matchId,
        long roomEpoch,
        CancellationToken cancellationToken) =>
        AllocateAsync(requestId, roomId, matchId, cancellationToken);

    /// <summary>转发 Dedicated Server 注册并返回心跳配置；凭据不得记录。</summary>
    Task<AllocatorRegistrationAck> ConfirmRegistrationAsync(
        string requestId,
        GameServerRegistration request,
        CancellationToken cancellationToken);

    /// <summary>转发实例心跳；失败由 Lobby 生命周期策略决定重试或标记异常。</summary>
    Task RecordHeartbeatAsync(
        string requestId,
        string serverInstanceId,
        GameServerHeartbeat request,
        CancellationToken cancellationToken);

    /// <summary>请求实例进入排空/停止阶段，避免继续接收新玩家。</summary>
    Task DrainAsync(string requestId, string serverInstanceId, CancellationToken cancellationToken);
}

/// <summary>本地关闭 Allocator 时的实现；分配/注册显式失败，心跳和排空保持无副作用。</summary>
public sealed class DisabledAllocatorClient : IAllocatorClient
{
    /// <inheritdoc/>
    public bool Enabled => false;

    /// <inheritdoc/>
    public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    /// <inheritdoc/>
    public Task<AllocatorAllocation> AllocateAsync(
        string requestId, string roomId, string matchId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Allocator is disabled.");

    /// <inheritdoc/>
    public Task<AllocatorRegistrationAck> ConfirmRegistrationAsync(
        string requestId, GameServerRegistration request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Allocator is disabled.");

    /// <inheritdoc/>
    public Task RecordHeartbeatAsync(
        string requestId,
        string serverInstanceId,
        GameServerHeartbeat request,
        CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task DrainAsync(
        string requestId, string serverInstanceId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// 使用服务身份调用 Allocator 的 HTTP 实现。
/// 基地址和令牌来自已验证配置；每个命令携带稳定 RequestId，响应错误不会降级为本地分配。
/// </summary>
public sealed class HttpAllocatorClient(
    IHttpClientFactory httpClientFactory,
    IOptions<LobbyOptions> options) : IAllocatorClient
{
    private readonly AllocatorClientOptions options = options.Value.Allocator;

    /// <inheritdoc/>
    public bool Enabled => options.Enabled;

    /// <inheritdoc/>
    public async Task<bool> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client().GetAsync("/health/ready", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<AllocatorAllocation> AllocateAsync(
        string requestId,
        string roomId,
        string matchId,
        CancellationToken cancellationToken) =>
        await AllocateForEpochAsync(
            requestId,
            roomId,
            matchId,
            1,
            cancellationToken);

    /// <inheritdoc/>
    public async Task<AllocatorAllocation> AllocateForEpochAsync(
        string requestId,
        string roomId,
        string matchId,
        long roomEpoch,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, "/internal/allocations", requestId);
        request.Content = JsonContent.Create(new
        {
            allocationId = requestId,
            roomId,
            matchId,
            buildVersion = options.GameServerBuildVersion,
            roomEpoch,
            options.GameType,
            options.Region,
            options.RuleSetVersion,
            options.ProtocolVersion,
            options.RequestedCapacity,
            idempotencyKey = requestId
        });
        request.Headers.Add("Idempotency-Key", requestId);
        return await SendAsync<AllocatorAllocation>(request, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<AllocatorRegistrationAck> ConfirmRegistrationAsync(
        string requestId,
        GameServerRegistration registration,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"/internal/instances/{registration.ServerInstanceId}/register",
            requestId);
        request.Content = JsonContent.Create(new
        {
            registration.RoomId,
            registration.ListenIp,
            registration.ListenPort,
            registration.BuildVersion,
            registration.RegistrationCredential,
            registration.RoomEpoch,
            registration.FencingToken
        });
        return await SendAsync<AllocatorRegistrationAck>(request, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task RecordHeartbeatAsync(
        string requestId,
        string serverInstanceId,
        GameServerHeartbeat heartbeat,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"/internal/instances/{serverInstanceId}/heartbeat",
            requestId);
        request.Content = JsonContent.Create(heartbeat);
        using var response = await Client().SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    public async Task DrainAsync(
        string requestId, string serverInstanceId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"/internal/instances/{serverInstanceId}/drain",
            requestId);
        using var response = await Client().SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string requestId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceToken);
        request.Headers.Add("X-Request-Id", requestId);
        request.Headers.Add(
            "X-Trace-Id",
            MahjongTelemetry.CurrentBusinessTraceId);
        return request;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await Client().SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new HttpRequestException("Allocator returned an empty response.");
    }

    private HttpClient Client()
    {
        var client = httpClientFactory.CreateClient(nameof(HttpAllocatorClient));
        client.BaseAddress = new Uri(options.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        return client;
    }
}
