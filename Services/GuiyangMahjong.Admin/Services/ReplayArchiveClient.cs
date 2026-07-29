using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using GuiyangMahjong.Admin.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Services;

/// <summary>回放访问授权结果；URL 绑定案件、回放、操作者和短时过期点。</summary>
public sealed record ReplayAccessGrant(
    string AccessUrl,
    DateTimeOffset ExpiresAtUtc,
    string PlayerId,
    string EventId,
    string CaseId);

/// <summary>
/// 生成/验证短时访问签名并从独立对象网关读取回放；Admin 不向前端暴露真实 ObjectKey。
/// </summary>
public interface IReplayArchiveClient
{
    ReplayAccessGrant CreateAccess(
        string caseId,
        string playerId,
        string eventId,
        string operatorId,
        DateTimeOffset now);
    bool ValidateAccess(
        string caseId,
        string playerId,
        string eventId,
        string operatorId,
        long expiresUnixSeconds,
        string signature,
        DateTimeOffset now);
    Task<byte[]> DownloadAsync(
        string objectKey,
        string? expectedSha256,
        CancellationToken cancellationToken);
}

/// <summary>受控回放对象客户端；限制对象键、响应大小并校验目录提供的 SHA-256。</summary>
public sealed class HttpReplayArchiveClient(
    IHttpClientFactory httpClientFactory,
    IOptions<AdminOptions> options) : IReplayArchiveClient
{
    public ReplayAccessGrant CreateAccess(
        string caseId,
        string playerId,
        string eventId,
        string operatorId,
        DateTimeOffset now)
    {
        EnsureEnabled();
        var expires = now.AddSeconds(options.Value.ReplayArchive.AccessTtlSeconds);
        var signature = Sign(
            caseId,
            playerId,
            eventId,
            operatorId,
            expires.ToUnixTimeSeconds());
        return new ReplayAccessGrant(
            $"/admin/v1/players/{Uri.EscapeDataString(playerId)}"
                + $"/replay-content/{Uri.EscapeDataString(eventId)}"
                + $"?caseId={Uri.EscapeDataString(caseId)}"
                + $"&expires={expires.ToUnixTimeSeconds()}"
                + $"&signature={Uri.EscapeDataString(signature)}",
            expires,
            playerId,
            eventId,
            caseId);
    }

    public bool ValidateAccess(
        string caseId,
        string playerId,
        string eventId,
        string operatorId,
        long expiresUnixSeconds,
        string signature,
        DateTimeOffset now)
    {
        EnsureEnabled();
        if (expiresUnixSeconds < now.ToUnixTimeSeconds()
            || expiresUnixSeconds > now.AddMinutes(10).ToUnixTimeSeconds())
        {
            return false;
        }
        var expected = Encoding.UTF8.GetBytes(
            Sign(caseId, playerId, eventId, operatorId, expiresUnixSeconds));
        var supplied = Encoding.UTF8.GetBytes(signature);
        var valid = expected.Length == supplied.Length
            && CryptographicOperations.FixedTimeEquals(expected, supplied);
        CryptographicOperations.ZeroMemory(expected);
        CryptographicOperations.ZeroMemory(supplied);
        return valid;
    }

    public async Task<byte[]> DownloadAsync(
        string objectKey,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(objectKey)
            || objectKey.Length > 512
            || objectKey.Contains("..", StringComparison.Ordinal)
            || Uri.IsWellFormedUriString(objectKey, UriKind.Absolute))
        {
            throw new ReplayArchiveUnavailableException(
                "Replay object key is invalid.");
        }
        var settings = options.Value.ReplayArchive;
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{settings.BaseUrl.TrimEnd('/')}/v1/objects/{Uri.EscapeDataString(objectKey)}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            settings.ReadToken);
        using var response = await httpClientFactory
            .CreateClient(nameof(HttpReplayArchiveClient))
            .SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ReplayArchiveUnavailableException(
                $"Replay archive returned {(int)response.StatusCode}.");
        var maximumBytes = settings.MaxObjectMegabytes * 1024L * 1024L;
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new ReplayArchiveUnavailableException(
                "Replay object exceeds the configured size limit.");
        await using var source = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        await using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maximumBytes)
                throw new ReplayArchiveUnavailableException(
                    "Replay object exceeds the configured size limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        var content = buffer.ToArray();
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actual = Convert.ToHexString(SHA256.HashData(content))
                .ToLowerInvariant();
            if (!string.Equals(
                    actual,
                    expectedSha256.Trim().ToLowerInvariant(),
                    StringComparison.Ordinal))
            {
                throw new ReplayArchiveUnavailableException(
                    "Replay integrity verification failed.");
            }
        }
        return content;
    }

    private string Sign(
        string caseId,
        string playerId,
        string eventId,
        string operatorId,
        long expiresUnixSeconds)
    {
        using var hmac = new HMACSHA256(
            Encoding.UTF8.GetBytes(options.Value.ReplayArchive.SigningKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(
                $"{caseId}|{playerId}|{eventId}|{operatorId}|{expiresUnixSeconds}")))
            .ToLowerInvariant();
    }

    private void EnsureEnabled()
    {
        if (!options.Value.ReplayArchive.Enabled)
            throw new ReplayArchiveUnavailableException(
                "Replay archive access is not configured.");
    }
}

/// <summary>回放对象不可用、过大或完整性校验失败时的受控异常。</summary>
public sealed class ReplayArchiveUnavailableException(string message)
    : Exception(message);
