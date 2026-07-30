using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GuiyangMahjong.Allocator.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Allocator.Services;

/// <summary>
/// Dedicated Server 本地结算 Outbox 的受控路径解析器。
/// 路径以配置目录为根并规范化为绝对路径，实例文件名只接受服务端生成的安全实例标识。
/// </summary>
public static class MatchResultOutboxPaths
{
    /// <summary>解析 Outbox 根目录；相对路径以应用目录为基准，不创建目录。</summary>
    public static string GetDirectory(AllocatorOptions options)
    {
        var configured = options.MatchResultOutboxDirectory.Trim();
        var path = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
        return Path.GetFullPath(path);
    }

    /// <summary>返回指定实例唯一 JSON Outbox 路径；调用前实例标识必须已通过业务校验。</summary>
    public static string GetInstancePath(AllocatorOptions options, string serverInstanceId) =>
        Path.Combine(GetDirectory(options), $"{serverInstanceId}.json");
}

/// <summary>结算 Outbox 中的单个玩家结果；座位、名次和总分来自 Dedicated Server 权威状态。</summary>
public sealed record MatchResultOutboxPlayer(
    string PlayerId,
    int SeatIndex,
    int Rank,
    int TotalScore);

/// <summary>待恢复的结算报告；ResultSequence 对同一比赛单调，Players 必须覆盖结算参与者。</summary>
public sealed record MatchResultOutboxReport(
    string RoomId,
    string ServerInstanceId,
    long ResultSequence,
    int CompletedRounds,
    MatchResultOutboxPlayer[] Players);

/// <summary>结算文件信封；Version 控制文件兼容性，MatchId 必须与报告房间当前比赛一致。</summary>
public sealed record MatchResultOutboxEnvelope(
    int Version,
    string MatchId,
    MatchResultOutboxReport Report);

/// <summary>Lobby 对恢复提交的幂等回执；Duplicate 表示同序号同载荷已接受。</summary>
public sealed record MatchResultRecoveryAck(
    string RequestId,
    string MatchId,
    long ResultSequence,
    bool Accepted,
    bool Duplicate);

/// <summary>
/// 扫描 Dedicated Server 遗留结算文件并可靠重投 Lobby。
/// 文件大小、版本、标识和时间先校验；只有 Lobby 明确接受或确认重复后才删除文件，
/// 网络失败和不确定响应保留文件等待下轮重试。
/// </summary>
public sealed class MatchResultOutboxRecovery(
    IHttpClientFactory httpClientFactory,
    IOptions<AllocatorOptions> options,
    TimeProvider timeProvider,
    ILogger<MatchResultOutboxRecovery> logger)
{
    private const long MaximumOutboxBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AllocatorOptions options = options.Value;
    private readonly string outboxDirectory = MatchResultOutboxPaths.GetDirectory(options.Value);

    /// <summary>
    /// 扫描根目录第一层达到恢复延迟的 JSON 文件并逐个尝试提交。
    /// 取消会停止扫描；单个损坏或失败文件记录后保留，不阻断其他文件。
    /// </summary>
    public async Task RecoverAvailableAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outboxDirectory);
        foreach (var path in Directory.EnumerateFiles(outboxDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timeProvider.GetUtcNow() - File.GetLastWriteTimeUtc(path)
                < TimeSpan.FromSeconds(options.MatchResultRecoveryDelaySeconds))
            {
                continue;
            }

            await TryRecoverAsync(path, cancellationToken);
        }
    }

    private async Task TryRecoverAsync(string path, CancellationToken cancellationToken)
    {
        MatchResultOutboxEnvelope envelope;
        try
        {
            var file = new FileInfo(path);
            if (file.Length is <= 0 or > MaximumOutboxBytes)
            {
                logger.LogError("结算 outbox 文件大小非法 Path={Path} Bytes={Bytes}", path, file.Length);
                return;
            }
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous);
            envelope = await JsonSerializer.DeserializeAsync<MatchResultOutboxEnvelope>(
                    stream, JsonOptions, cancellationToken)
                ?? throw new JsonException("结算 outbox 内容为空");
            Validate(path, envelope);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException)
        {
            logger.LogError(exception, "结算 outbox 无法读取或校验 Path={Path}", path);
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{options.LobbyInternalUrl.TrimEnd('/')}/internal/matches/{envelope.MatchId}/result/recovery")
            {
                Content = JsonContent.Create(envelope.Report, options: JsonOptions)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.LobbyCallbackToken);
            request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString());
            request.Headers.Add("Idempotency-Key", $"recovery:{envelope.MatchId}:{envelope.Report.ResultSequence}");
            using var response = await httpClientFactory.CreateClient(nameof(MatchResultOutboxRecovery))
                .SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "大厅暂未接受结算 outbox MatchId={MatchId} InstanceId={InstanceId} Status={Status}",
                    envelope.MatchId, envelope.Report.ServerInstanceId, (int)response.StatusCode);
                return;
            }

            var acknowledgement = await response.Content.ReadFromJsonAsync<MatchResultRecoveryAck>(
                JsonOptions, cancellationToken);
            if (acknowledgement is not { Accepted: true }
                || acknowledgement.MatchId != envelope.MatchId
                || acknowledgement.ResultSequence != envelope.Report.ResultSequence)
            {
                logger.LogWarning(
                    "大厅返回的结算恢复确认不匹配 MatchId={MatchId} InstanceId={InstanceId}",
                    envelope.MatchId, envelope.Report.ServerInstanceId);
                return;
            }

            File.Delete(path);
            logger.LogInformation(
                "结算 outbox 已恢复 MatchId={MatchId} InstanceId={InstanceId} Sequence={Sequence} Duplicate={Duplicate}",
                envelope.MatchId, envelope.Report.ServerInstanceId,
                envelope.Report.ResultSequence, acknowledgement.Duplicate);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or UnauthorizedAccessException or TaskCanceledException)
        {
            if (exception is TaskCanceledException && cancellationToken.IsCancellationRequested) throw;
            logger.LogWarning(exception,
                "结算 outbox 恢复暂时失败 MatchId={MatchId} InstanceId={InstanceId}",
                envelope.MatchId, envelope.Report.ServerInstanceId);
        }
    }

    private static void Validate(string path, MatchResultOutboxEnvelope envelope)
    {
        if (envelope.Version != 1
            || !Guid.TryParse(envelope.MatchId, out _)
            || !Guid.TryParse(envelope.Report.RoomId, out _)
            || !Guid.TryParse(envelope.Report.ServerInstanceId, out _)
            || !string.Equals(Path.GetFileNameWithoutExtension(path), envelope.Report.ServerInstanceId,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || envelope.Report.ResultSequence < 1
            || envelope.Report.CompletedRounds is < 1 or > 16
            || envelope.Report.Players is null
            || envelope.Report.Players.Length is < 1 or > 4
            || envelope.Report.Players.Any(player => string.IsNullOrWhiteSpace(player.PlayerId)
                || player.PlayerId.Length > 80
                || player.SeatIndex is < 0 or > 3
                || player.Rank is < 1 or > 4)
            || envelope.Report.Players.Select(player => player.PlayerId)
                .Distinct(StringComparer.Ordinal).Count() != envelope.Report.Players.Length)
        {
            throw new InvalidDataException("结算 outbox 的版本、作用域或结果数据非法");
        }
    }
}

/// <summary>
/// 周期驱动结算恢复器的后台服务。
/// 宿主取消时退出，循环异常记录后继续；扫描频率由配置限制，避免磁盘和 Lobby 压力。
/// </summary>
public sealed class MatchResultOutboxRecoveryService(
    MatchResultOutboxRecovery recovery,
    IOptions<AllocatorOptions> options,
    ILogger<MatchResultOutboxRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMilliseconds(options.Value.MonitorIntervalMilliseconds));
        do
        {
            try { await recovery.RecoverAvailableAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogError(exception, "结算 outbox 恢复轮询失败"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
