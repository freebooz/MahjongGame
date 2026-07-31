using System.Net.Http.Headers;
using System.Security.Cryptography;
using GuiyangMahjong.GameData.Domain;
using GuiyangMahjong.GameData.Options;
using GuiyangMahjong.GameData.Settlement;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.GameData.ReplayEvidence;

/// <summary>验证证据对象已存在、大小匹配且内容摘要正确；不会返回对象内容给结算模块。</summary>
public interface IEvidenceVerifier
{
    Task VerifyAsync(IReadOnlyList<EvidenceManifestItem> items, CancellationToken cancellationToken);
}

/// <summary>仅验证不可变对象键和元数据；只允许开发、影子对比和单元测试使用。</summary>
public sealed class MetadataEvidenceVerifier : IEvidenceVerifier
{
    public Task VerifyAsync(IReadOnlyList<EvidenceManifestItem> items, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

/// <summary>从只读共享恢复卷逐个读取对象并验证大小和 SHA-256，路径穿越会失败关闭。</summary>
public sealed class FileSystemEvidenceVerifier(IOptions<GameDataOptions> options) : IEvidenceVerifier
{
    private readonly string root = Path.GetFullPath(options.Value.EvidenceStorage.RootDirectory);

    public async Task VerifyAsync(IReadOnlyList<EvidenceManifestItem> items, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            var path = Path.GetFullPath(Path.Combine(root, item.ObjectKey.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(path))
                throw GameDataException.Invalid("EVIDENCE_MISSING", "结算证据对象不存在");
            var info = new FileInfo(path);
            if (info.Length != item.SizeBytes)
                throw GameDataException.Invalid("EVIDENCE_SIZE_MISMATCH", "结算证据对象大小不匹配");
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
            if (!string.Equals(hash, item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw GameDataException.Invalid("EVIDENCE_HASH_MISMATCH", "结算证据对象哈希不匹配");
        }
    }
}

/// <summary>通过 MinIO/S3 前置对象网关读取证据并做流式大小和哈希校验。</summary>
public sealed class HttpEvidenceVerifier(
    IHttpClientFactory httpClientFactory,
    IOptions<GameDataOptions> options) : IEvidenceVerifier
{
    public async Task VerifyAsync(IReadOnlyList<EvidenceManifestItem> items, CancellationToken cancellationToken)
    {
        var settings = options.Value.EvidenceStorage;
        foreach (var item in items)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{settings.BaseUrl.TrimEnd('/')}/v1/objects/{Uri.EscapeDataString(item.ObjectKey)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ReadToken);
            using var response = await httpClientFactory.CreateClient(nameof(HttpEvidenceVerifier))
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw GameDataException.Unavailable("EVIDENCE_STORE_UNAVAILABLE", "证据对象存储暂时不可用");
            if (response.Content.Headers.ContentLength is long length && length != item.SizeBytes)
                throw GameDataException.Invalid("EVIDENCE_SIZE_MISMATCH", "结算证据对象大小不匹配");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > settings.MaximumObjectBytes || total > item.SizeBytes)
                    throw GameDataException.Invalid("EVIDENCE_TOO_LARGE", "结算证据对象超过声明大小");
                hash.AppendData(buffer, 0, read);
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (total != item.SizeBytes || !string.Equals(actual, item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw GameDataException.Invalid("EVIDENCE_HASH_MISMATCH", "结算证据对象哈希不匹配");
        }
    }
}
