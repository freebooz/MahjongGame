using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GuiyangMahjong.PlayerData.Domain;
using GuiyangMahjong.PlayerData.Options;
using GuiyangMahjong.PlayerData.Storage;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.PlayerData.Services;

/// <summary>PlayerData 查询 Auth 玩家禁言策略的最小只读客户端边界。</summary>
public interface IChatPolicyClient
{
    /// <summary>读取玩家当前聊天策略；失败不得默认允许发送。</summary>
    Task<ChatPolicyResult> GetPolicyAsync(
        string playerId,
        CancellationToken cancellationToken);
}

/// <summary>
/// 使用独立服务身份调用 Auth 聊天策略端点。
/// 请求不携带消息正文；网络、身份或协议失败采用关闭式拒绝语义。
/// </summary>
public sealed class HttpChatPolicyClient(
    IHttpClientFactory httpClientFactory,
    IOptions<PlayerDataOptions> options,
    TimeProvider timeProvider) : IChatPolicyClient
{
    private readonly PlayerDataOptions settings = options.Value;

    /// <inheritdoc/>
    public async Task<ChatPolicyResult> GetPolicyAsync(
        string playerId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(
                new Uri(settings.AuthBaseUrl),
                $"/internal/monitoring/players/{Uri.EscapeDataString(playerId)}"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            settings.AuthMonitoringToken);
        using var client = httpClientFactory.CreateClient(
            nameof(HttpChatPolicyClient));
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ChatPolicyResult(
                playerId,
                false,
                null,
                "Player account was not found.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Auth policy lookup returned {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: cancellationToken);
        var mutedUntilUtc = body
            .GetProperty("player")
            .TryGetProperty("mutedUntilUtc", out var muted)
            && muted.ValueKind == JsonValueKind.String
            ? muted.GetDateTimeOffset()
            : (DateTimeOffset?)null;
        var now = timeProvider.GetUtcNow();
        return mutedUntilUtc > now
            ? new ChatPolicyResult(
                playerId,
                false,
                mutedUntilUtc,
                "Player is muted by an approved sanction.")
            : new ChatPolicyResult(
                playerId,
                true,
                null,
                "Player may send chat messages.");
    }
}

/// <summary>
/// 玩家证据投影 Outbox 的单批次执行器。
/// 使用 workerId 租约领取记录，以 EventId 调用 Admin 幂等接入；
/// 成功/冲突确认完成，瞬态失败退避，永久失败保留供调查。
/// </summary>
public sealed class ProjectionDispatcher(
    IPlayerDataStore store,
    IHttpClientFactory httpClientFactory,
    IOptions<PlayerDataOptions> options,
    TimeProvider timeProvider,
    ILogger<ProjectionDispatcher> logger)
{
    private readonly PlayerDataOptions settings = options.Value;

    /// <summary>
    /// 领取并投递一批证据投影，返回本次领取数量。
    /// 单条失败不会阻断同批其他记录；取消会传播且不错误确认未完成项。
    /// </summary>
    public async Task<int> DispatchOnceAsync(
        string workerId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var items = await store.ClaimProjectionsAsync(
            workerId,
            20,
            now,
            now.AddSeconds(30),
            cancellationToken);
        foreach (var item in items)
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    new Uri(
                        new Uri(settings.AdminProjectionBaseUrl),
                        "/internal/projections/player-evidence"))
                {
                    Content = JsonContent.Create(item.Payload)
                };
                request.Headers.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer",
                        settings.AdminEvidenceIngestionToken);
                request.Headers.Add("Idempotency-Key", item.EventId);
                using var client = httpClientFactory.CreateClient(
                    nameof(ProjectionDispatcher));
                using var response = await client.SendAsync(
                    request,
                    cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    await store.CompleteProjectionAsync(
                        item.EventId,
                        workerId,
                        cancellationToken);
                    continue;
                }
                var error =
                    $"Admin projection returned {(int)response.StatusCode}.";
                var terminal = (int)response.StatusCode is >= 400 and < 500
                    && response.StatusCode is not (
                        HttpStatusCode.RequestTimeout
                        or HttpStatusCode.TooManyRequests);
                await store.FailProjectionAsync(
                    item.EventId,
                    workerId,
                    error,
                    now.AddSeconds(
                        Math.Min(
                            300,
                            1 << Math.Min(item.AttemptCount, 8))),
                    terminal
                        || item.AttemptCount >=
                            settings.ProjectionMaxAttempts,
                    cancellationToken);
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Player evidence projection failed EventId={EventId}",
                    item.EventId);
                await store.FailProjectionAsync(
                    item.EventId,
                    workerId,
                    exception.Message,
                    now.AddSeconds(
                        Math.Min(
                            300,
                            1 << Math.Min(item.AttemptCount, 8))),
                    item.AttemptCount >= settings.ProjectionMaxAttempts,
                    cancellationToken);
            }
        }
        return items.Count;
    }
}

/// <summary>
/// 周期驱动 ProjectionDispatcher 的后台服务。
/// 使用实例唯一 workerId，宿主取消时退出，循环异常记录后继续以防投影静默停摆。
/// </summary>
public sealed class ProjectionDispatcherService(
    ProjectionDispatcher dispatcher,
    IOptions<PlayerDataOptions> options,
    ILogger<ProjectionDispatcherService> logger) : BackgroundService
{
    private readonly PlayerDataOptions settings = options.Value;
    private readonly string workerId =
        $"player-data:{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        if (!settings.ProjectionEnabled)
        {
            logger.LogInformation(
                "Player evidence projection dispatcher is disabled.");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            var count = await dispatcher.DispatchOnceAsync(
                workerId,
                stoppingToken);
            if (count == 0)
            {
                await Task.Delay(
                    settings.ProjectionPollMilliseconds,
                    stoppingToken);
            }
        }
    }
}

/// <summary>
/// PlayerData 存储启动初始化器。
/// 开发环境可显式执行幂等建表；生产关闭迁移时运行身份不需要 DDL 权限。
/// </summary>
public sealed class PlayerDataStoreInitializer(
    IPlayerDataStore store,
    IOptions<PlayerDataOptions> options,
    ILogger<PlayerDataStoreInitializer> logger) : IHostedService
{
    /// <summary>本地开发可执行幂等建表；生产环境关闭后仅使用预先迁移好的结构。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.ApplyDatabaseMigrations)
        {
            await store.InitializeAsync(cancellationToken);
            logger.LogInformation("Player data store initialized.");
            return;
        }

        logger.LogInformation("PlayerData 数据库迁移已关闭，运行身份不会执行 DDL。");
    }

    /// <summary>停止阶段无额外副作用；连接池由注册的数据源/存储生命周期释放。</summary>
    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
