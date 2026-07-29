using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GuiyangMahjong.Admin.Domain;

/// <summary>
/// 通用键集分页响应。Items 已完成权限校验与脱敏；NextCursor 只能用于相同过滤条件。
/// </summary>
public sealed record CursorPage<T>(
    T[] Items,
    string? NextCursor,
    bool HasMore,
    int PageSize);

/// <summary>
/// Admin 列表游标工具。使用不可变时间和唯一 ID 排序，并用过滤器摘要阻止跨查询复用。
/// </summary>
public static class MonitoringCursorPagination
{
    private const int Version = 1;

    /// <summary>
    /// 从已经完成授权、脱敏和稳定排序的集合生成一页；无效游标会失败而不是回退全量扫描。
    /// </summary>
    public static CursorPage<T> CreatePage<T>(
        IEnumerable<T> source,
        int requestedPageSize,
        int maximumPageSize,
        string filterIdentity,
        Func<T, DateTimeOffset> timestampSelector,
        Func<T, string> idSelector,
        string? cursor)
    {
        var pageSize = Math.Clamp(requestedPageSize, 1, maximumPageSize);
        var filterHash = HashFilter(filterIdentity);
        var boundary = Decode(cursor, filterHash);
        var ordered = source
            .Where(item => boundary is null
                || timestampSelector(item) < boundary.TimestampUtc
                || (timestampSelector(item) == boundary.TimestampUtc
                    && string.CompareOrdinal(
                        idSelector(item),
                        boundary.Id) > 0))
            .OrderByDescending(timestampSelector)
            .ThenBy(idSelector, StringComparer.Ordinal)
            .Take(pageSize + 1)
            .ToArray();
        var items = ordered.Take(pageSize).ToArray();
        var hasMore = ordered.Length > pageSize;
        var nextCursor = hasMore && items.Length > 0
            ? Encode(new CursorPayload(
                Version,
                timestampSelector(items[^1]),
                idSelector(items[^1]),
                filterHash))
            : null;
        return new CursorPage<T>(items, nextCursor, hasMore, pageSize);
    }

    /// <summary>计算规范化过滤器摘要，避免游标从低敏查询被复用到其他查询范围。</summary>
    public static string HashFilter(string filterIdentity) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(filterIdentity))).ToLowerInvariant();

    /// <summary>
    /// 将下游服务的不透明游标绑定到 Admin 当前筛选条件，浏览器不能跨筛选复用。
    /// </summary>
    public static string WrapOpaqueCursor(
        string upstreamCursor,
        string filterIdentity) =>
        EncodeOpaque(new OpaqueCursorPayload(
            Version,
            upstreamCursor,
            HashFilter(filterIdentity)));

    /// <summary>
    /// 校验并解开下游游标；损坏、版本不兼容或筛选不匹配时返回明确的 400 错误。
    /// </summary>
    public static string? UnwrapOpaqueCursor(
        string? cursor,
        string filterIdentity)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<OpaqueCursorPayload>(
                Convert.FromBase64String(cursor));
            var expectedFilterHash = HashFilter(filterIdentity);
            if (payload is null
                || payload.Version != Version
                || string.IsNullOrWhiteSpace(payload.UpstreamCursor)
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(payload.FilterHash),
                    Encoding.UTF8.GetBytes(expectedFilterHash)))
            {
                throw new InvalidMonitoringCursorException();
            }
            return payload.UpstreamCursor;
        }
        catch (Exception exception)
            when (exception is FormatException or JsonException)
        {
            throw new InvalidMonitoringCursorException();
        }
    }

    private static CursorPayload? Decode(
        string? cursor,
        string expectedFilterHash)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<CursorPayload>(
                Convert.FromBase64String(cursor));
            if (payload is null
                || payload.Version != Version
                || string.IsNullOrWhiteSpace(payload.Id)
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(payload.FilterHash),
                    Encoding.UTF8.GetBytes(expectedFilterHash)))
            {
                throw new InvalidMonitoringCursorException();
            }
            return payload;
        }
        catch (Exception exception)
            when (exception is FormatException or JsonException)
        {
            throw new InvalidMonitoringCursorException();
        }
    }

    private static string Encode(CursorPayload payload) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload));

    private static string EncodeOpaque(OpaqueCursorPayload payload) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload));

    private sealed record CursorPayload(
        int Version,
        DateTimeOffset TimestampUtc,
        string Id,
        string FilterHash);

    /// <summary>保存下游游标和筛选摘要，不暴露下游游标结构。</summary>
    private sealed record OpaqueCursorPayload(
        int Version,
        string UpstreamCursor,
        string FilterHash);
}

/// <summary>表示客户端游标损坏、版本不兼容或与当前过滤条件不匹配。</summary>
public sealed class InvalidMonitoringCursorException : Exception
{
    public InvalidMonitoringCursorException()
        : base("Cursor is invalid, expired, or belongs to another filter.")
    {
    }
}
