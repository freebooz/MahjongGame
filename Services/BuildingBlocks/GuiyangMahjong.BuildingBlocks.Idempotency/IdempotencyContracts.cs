using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using GuiyangMahjong.Contracts.Common;
using Microsoft.AspNetCore.Http;

namespace GuiyangMahjong.BuildingBlocks.Idempotency;

/// <summary>API 幂等记录状态；Completed 才允许重放首次响应。</summary>
public enum IdempotencyStatus
{
    Processing,
    Completed,
    Failed
}

/// <summary>首次响应快照；不得保存 Set-Cookie、Token 或其他凭据 Header。</summary>
public sealed record IdempotentResponse(
    int StatusCode,
    string ContentType,
    byte[] Body);

/// <summary>幂等记录，以服务端 Scope + Key 形成业务唯一约束。</summary>
public sealed record IdempotencyRecord(
    string Scope,
    IdempotencyKey Key,
    string RequestFingerprint,
    IdempotencyStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    IdempotentResponse? Response,
    string? ErrorSummary);

/// <summary>请求进入幂等边界后的判定结果。</summary>
public enum IdempotencyDecision
{
    Acquired,
    InProgress,
    Replay,
    Conflict
}

/// <summary>幂等判定及可选的首次响应。</summary>
public sealed record IdempotencyResult(
    IdempotencyDecision Decision,
    IdempotentResponse? Response);

/// <summary>请求指纹算法边界；实现必须稳定且不能把正文写入日志。</summary>
public interface IRequestFingerprint
{
    string Compute(
        string method,
        string canonicalPathAndQuery,
        ReadOnlySpan<byte> body);
}

/// <summary>使用 SHA-256 计算稳定请求指纹，区分方法、规范路径和原始正文。</summary>
public sealed class Sha256RequestFingerprint : IRequestFingerprint
{
    /// <inheritdoc/>
    public string Compute(
        string method,
        string canonicalPathAndQuery,
        ReadOnlySpan<byte> body)
    {
        if (string.IsNullOrWhiteSpace(method)
            || string.IsNullOrWhiteSpace(canonicalPathAndQuery))
        {
            throw new ArgumentException("请求方法和规范路径不能为空。");
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(method.ToUpperInvariant()));
        hash.AppendData([0]);
        hash.AppendData(Encoding.UTF8.GetBytes(canonicalPathAndQuery));
        hash.AppendData([0]);
        hash.AppendData(body);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

/// <summary>从 ASP.NET Core 请求读取并验证 Idempotency-Key，不接受多值或低熵键。</summary>
public static class IdempotencyHeaderReader
{
    public const string HeaderName = "Idempotency-Key";

    /// <summary>成功返回强类型键；缺失或格式错误由调用方映射为统一 400。</summary>
    public static bool TryRead(
        HttpRequest request,
        out IdempotencyKey key)
    {
        key = default;
        if (!request.Headers.TryGetValue(HeaderName, out var values)
            || values.Count != 1)
        {
            return false;
        }
        return IdempotencyKey.TryParse(values[0], out key);
    }
}

/// <summary>幂等存储抽象；业务服务负责定义不会跨命令碰撞的 Scope。</summary>
public interface IIdempotencyStore
{
    Task<IdempotencyResult> TryBeginAsync(
        string scope,
        IdempotencyKey key,
        string requestFingerprint,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        string scope,
        IdempotencyKey key,
        string requestFingerprint,
        IdempotentResponse response,
        CancellationToken cancellationToken);

    Task FailAsync(
        string scope,
        IdempotencyKey key,
        string errorSummary,
        CancellationToken cancellationToken);

    Task<int> DeleteExpiredAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>
/// 单进程测试实现，完整模拟重复、处理中、参数冲突、完成响应和过期清理语义。
/// 生产多副本必须使用具有数据库唯一约束的实现。
/// </summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, IdempotencyRecord> records =
        new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public Task<IdempotencyResult> TryBeginAsync(
        string scope,
        IdempotencyKey key,
        string requestFingerprint,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(scope, requestFingerprint, expiresAt, now);
        var storageKey = $"{scope}\n{key.Value}";
        while (true)
        {
            if (records.TryGetValue(storageKey, out var existing))
            {
                if (existing.ExpiresAt <= now)
                {
                    records.TryRemove(
                        new KeyValuePair<string, IdempotencyRecord>(
                            storageKey,
                            existing));
                    continue;
                }
                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(existing.RequestFingerprint),
                        Encoding.UTF8.GetBytes(requestFingerprint)))
                {
                    return Task.FromResult(
                        new IdempotencyResult(
                            IdempotencyDecision.Conflict,
                            null));
                }
                return Task.FromResult(
                    existing.Status == IdempotencyStatus.Completed
                        ? new IdempotencyResult(
                            IdempotencyDecision.Replay,
                            existing.Response)
                        : new IdempotencyResult(
                            IdempotencyDecision.InProgress,
                            null));
            }

            var created = new IdempotencyRecord(
                scope,
                key,
                requestFingerprint,
                IdempotencyStatus.Processing,
                now,
                expiresAt,
                null,
                null);
            if (records.TryAdd(storageKey, created))
                return Task.FromResult(
                    new IdempotencyResult(
                        IdempotencyDecision.Acquired,
                        null));
        }
    }

    /// <inheritdoc/>
    public Task CompleteAsync(
        string scope,
        IdempotencyKey key,
        string requestFingerprint,
        IdempotentResponse response,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var storageKey = $"{scope}\n{key.Value}";
        records.AddOrUpdate(
            storageKey,
            _ => throw new InvalidOperationException("幂等记录尚未开始。"),
            (_, current) =>
            {
                if (!string.Equals(
                        current.RequestFingerprint,
                        requestFingerprint,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException("幂等键参数冲突。");
                if (current.Status == IdempotencyStatus.Completed)
                    return current;
                return current with
                {
                    Status = IdempotencyStatus.Completed,
                    Response = response,
                    ErrorSummary = null
                };
            });
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task FailAsync(
        string scope,
        IdempotencyKey key,
        string errorSummary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var storageKey = $"{scope}\n{key.Value}";
        if (records.TryGetValue(storageKey, out var current))
        {
            records[storageKey] = current with
            {
                Status = IdempotencyStatus.Failed,
                ErrorSummary = Truncate(errorSummary)
            };
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<int> DeleteExpiredAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var removed = 0;
        foreach (var item in records
                     .Where(item => item.Value.ExpiresAt <= now)
                     .Take(Math.Max(0, limit)))
        {
            if (records.TryRemove(item)) removed++;
        }
        return Task.FromResult(removed);
    }

    private static void Validate(
        string scope,
        string fingerprint,
        DateTimeOffset expiresAt,
        DateTimeOffset now)
    {
        if (!StrongValueValidation.IsIdentifier(scope)
            || fingerprint.Length != 64
            || expiresAt <= now)
            throw new ArgumentException("幂等记录参数无效。");
    }

    private static string Truncate(string value) =>
        value.Length <= 512 ? value : value[..512];
}
