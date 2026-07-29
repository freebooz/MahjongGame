using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Services;

/// <summary>
/// 集中日志只读查询接口；实现必须通过 Admin 服务端代理，禁止把 Loki 管理接口或凭据交给浏览器。
/// </summary>
public interface ICentralLogQueryClient
{
    /// <summary>
    /// 按批准房间范围查询日志；调用前必须完成案件、角色和目标一致性校验。
    /// </summary>
    Task<IReadOnlyList<CentralLogRecord>> QueryRoomAsync(
        string roomId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);
}

/// <summary>
/// Loki query_range 适配器；仅解析统一日志契约字段，忽略未知标签和任意扩展正文。
/// </summary>
public sealed class LokiCentralLogQueryClient(
    IHttpClientFactory httpClientFactory,
    IOptions<AdminOptions> options) : ICentralLogQueryClient
{
    /// <summary>
    /// 查询单个房间的受控时间窗口；失败时抛出安全异常且不泄露 Loki URL、查询凭据或原始响应。
    /// </summary>
    public async Task<IReadOnlyList<CentralLogRecord>> QueryRoomAsync(
        string roomId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        var settings = options.Value.CentralLogs;
        if (!settings.Enabled) return [];
        var safeRoomId = roomId.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        var logQuery =
            "{service_name=~\"GuiyangMahjong.+\"} | RoomId=\""
            + safeRoomId
            + "\"";
        var query = new Dictionary<string, string>
        {
            ["query"] = logQuery,
            ["start"] = ToNanoseconds(fromUtc).ToString(CultureInfo.InvariantCulture),
            ["end"] = ToNanoseconds(toUtc).ToString(CultureInfo.InvariantCulture),
            ["limit"] = settings.MaxEntries.ToString(CultureInfo.InvariantCulture),
            ["direction"] = "forward"
        };
        var uri = $"{settings.BaseUrl.TrimEnd('/')}/loki/api/v1/query_range?{string.Join(
            '&',
            query.Select(item =>
                $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"))}";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", settings.QueryToken);
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            using var response = await httpClientFactory
                .CreateClient(nameof(LokiCentralLogQueryClient))
                .SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content
                .ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: timeout.Token);
            return Parse(document.RootElement, settings.MaxEntries);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new CentralLogQueryUnavailableException(
                "集中日志查询超时，请稍后重试。");
        }
        catch (HttpRequestException)
        {
            throw new CentralLogQueryUnavailableException(
                "集中日志查询服务当前不可用，请稍后重试。");
        }
        catch (JsonException)
        {
            throw new CentralLogQueryUnavailableException(
                "集中日志响应格式无效，已拒绝生成不完整导出。");
        }
    }

    private static CentralLogRecord[] Parse(
        JsonElement root,
        int maximumEntries)
    {
        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Missing Loki result.");
        }
        var records = new List<CentralLogRecord>();
        foreach (var stream in result.EnumerateArray())
        {
            var streamLabels = stream.TryGetProperty(
                    "stream",
                    out var streamValue)
                && streamValue.ValueKind == JsonValueKind.Object
                    ? streamValue
                    : default;
            if (!stream.TryGetProperty("values", out var values)
                || values.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var pair in values.EnumerateArray())
            {
                if (records.Count >= maximumEntries) break;
                if (pair.ValueKind != JsonValueKind.Array
                    || pair.GetArrayLength() < 2)
                    continue;
                var elements = pair.EnumerateArray().ToArray();
                if (!long.TryParse(
                        elements[0].GetString(),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var nanoseconds))
                    continue;
                var body = elements[1].GetString() ?? string.Empty;
                JsonDocument? line = null;
                try
                {
                    if (body.TrimStart().StartsWith(
                        "{",
                            StringComparison.Ordinal))
                        line = JsonDocument.Parse(body);
                }
                catch (JsonException)
                {
                    // 非契约 JSON 正文仍按普通消息处理；字段只从 Loki 结构化元数据中读取。
                }
                var metadata = elements.Length >= 3
                    && elements[2].ValueKind == JsonValueKind.Object
                        ? elements[2]
                        : default;
                records.Add(MapLine(
                    nanoseconds,
                    body,
                    line?.RootElement ?? default,
                    metadata,
                    streamLabels));
                line?.Dispose();
            }
        }
        return records.OrderBy(item => item.Timestamp).ToArray();
    }

    private static CentralLogRecord MapLine(
        long nanoseconds,
        string body,
        JsonElement line,
        JsonElement metadata,
        JsonElement streamLabels) =>
        new(
            DateTimeOffset.UnixEpoch.AddTicks(nanoseconds / 100),
            GetString("Level", line, metadata, streamLabels)
                ?? GetString("severity_text", metadata, streamLabels)
                ?? "Unknown",
            GetString("Service", line, metadata, streamLabels)
                ?? GetString("service_name", streamLabels)
                ?? "Unknown",
            GetString("TraceId", line, metadata, streamLabels)
                ?? GetString("trace_id", metadata),
            GetString("RoomId", line, metadata, streamLabels),
            GetString("PlayerId", line, metadata, streamLabels),
            GetString("MatchId", line, metadata, streamLabels),
            GetString("ServerInstanceId", line, metadata, streamLabels),
            GetString("EventId", line, metadata, streamLabels),
            GetString("Message", line)
                ?? SensitiveDataSanitizer.SanitizeValue(body)?.ToString()
                ?? string.Empty);

    private static string? GetString(
        string name,
        params JsonElement[] values)
    {
        foreach (var value in values)
        {
            if (value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.String)
                return property.GetString();
        }
        return null;
    }

    private static long ToNanoseconds(DateTimeOffset value) =>
        checked((value - DateTimeOffset.UnixEpoch).Ticks * 100L);
}

/// <summary>
/// 集中日志查询失败的安全异常；原始网络错误、URL 和响应正文不会进入管理 API。
/// </summary>
public sealed class CentralLogQueryUnavailableException(string message)
    : Exception(message);
