using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GuiyangMahjong.PlayerData.Domain;
using GuiyangMahjong.PlayerData.Options;
using GuiyangMahjong.PlayerData.Storage;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.PlayerData.Services;

public interface IChatPolicyClient
{
    Task<ChatPolicyResult> GetPolicyAsync(
        string playerId,
        CancellationToken cancellationToken);
}

public sealed class HttpChatPolicyClient(
    IHttpClientFactory httpClientFactory,
    IOptions<PlayerDataOptions> options,
    TimeProvider timeProvider) : IChatPolicyClient
{
    private readonly PlayerDataOptions settings = options.Value;

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

public sealed class ProjectionDispatcher(
    IPlayerDataStore store,
    IHttpClientFactory httpClientFactory,
    IOptions<PlayerDataOptions> options,
    TimeProvider timeProvider,
    ILogger<ProjectionDispatcher> logger)
{
    private readonly PlayerDataOptions settings = options.Value;

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

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
