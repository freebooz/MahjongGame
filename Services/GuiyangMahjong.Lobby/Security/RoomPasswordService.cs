using System.Collections.Concurrent;
using System.Security.Cryptography;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Security;

/// <summary>房间密码验证判定；RateLimited 携带下一次允许尝试前的等待时间。</summary>
public enum PasswordVerificationStatus
{
    Success,
    Required,
    Wrong,
    RateLimited
}

/// <summary>密码验证结果；RetryAfterMilliseconds 单位为毫秒，仅限 RateLimited 有意义。</summary>
public sealed record PasswordVerificationResult(
    PasswordVerificationStatus Status,
    int RetryAfterMilliseconds = 0);

/// <summary>房间密码派生与限速验证边界；任何实现都不得持久化或记录明文密码。</summary>
public interface IRoomPasswordService
{
    /// <summary>校验密码策略并生成随机盐 PBKDF2 摘要；输入仅在当前调用内使用。</summary>
    ProtectedPassword Protect(string password);

    /// <summary>
    /// 按玩家+房间维度验证候选密码并限制失败频率。
    /// 无密码房间直接成功，比较必须固定时间，成功后清除失败窗口。
    /// </summary>
    PasswordVerificationResult Verify(
        string playerId, string roomId, ProtectedPassword? protectedPassword, string? candidate);
}

/// <summary>
/// 密码只保留 PBKDF2-SHA256 盐化摘要；审计日志不得传入候选密码。
/// 失败窗口驻留当前实例内，生产多副本需要由入口粘性或共享限速层提供集群级保护。
/// </summary>
public sealed class RoomPasswordService : IRoomPasswordService
{
    private const int Iterations = 120_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    private readonly ConcurrentDictionary<string, FailureWindow> failures = new(StringComparer.Ordinal);
    private readonly LobbyOptions options;
    private readonly TimeProvider timeProvider;

    /// <summary>取得失败次数/窗口策略和可测试 UTC 时间源。</summary>
    public RoomPasswordService(IOptions<LobbyOptions> options, TimeProvider timeProvider)
    {
        this.options = options.Value;
        this.timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public ProtectedPassword Protect(string password)
    {
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return new ProtectedPassword(Convert.ToBase64String(salt), Convert.ToBase64String(hash), Iterations);
    }

    /// <inheritdoc/>
    public PasswordVerificationResult Verify(
        string playerId, string roomId, ProtectedPassword? protectedPassword, string? candidate)
    {
        if (protectedPassword is null)
        {
            return new PasswordVerificationResult(PasswordVerificationStatus.Success);
        }

        if (string.IsNullOrEmpty(candidate))
        {
            return new PasswordVerificationResult(PasswordVerificationStatus.Required);
        }

        var key = $"{playerId}:{roomId}";
        var now = timeProvider.GetUtcNow();
        var window = failures.GetOrAdd(key, _ => new FailureWindow());
        lock (window)
        {
            if (window.WindowStartedUtc == default ||
                now - window.WindowStartedUtc >= TimeSpan.FromSeconds(options.PasswordFailureWindowSeconds))
            {
                window.WindowStartedUtc = now;
                window.Count = 0;
            }

            if (window.Count >= options.PasswordFailureLimit)
            {
                var retry = TimeSpan.FromSeconds(options.PasswordFailureWindowSeconds) - (now - window.WindowStartedUtc);
                return new PasswordVerificationResult(
                    PasswordVerificationStatus.RateLimited,
                    Math.Max(1, (int)retry.TotalMilliseconds));
            }

            byte[] actual;
            byte[] expected;
            byte[] salt;
            try
            {
                salt = Convert.FromBase64String(protectedPassword.SaltBase64);
                expected = Convert.FromBase64String(protectedPassword.HashBase64);
                actual = Rfc2898DeriveBytes.Pbkdf2(
                    candidate, salt, protectedPassword.Iterations, HashAlgorithmName.SHA256, expected.Length);
            }
            catch (FormatException)
            {
                window.Count++;
                return new PasswordVerificationResult(PasswordVerificationStatus.Wrong);
            }

            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                window.Count++;
                return new PasswordVerificationResult(PasswordVerificationStatus.Wrong);
            }

            failures.TryRemove(key, out _);
            return new PasswordVerificationResult(PasswordVerificationStatus.Success);
        }
    }

    private static void ValidatePassword(string password)
    {
        if (password.Length is < 6 or > 12)
        {
            throw new ArgumentException("房间密码必须为 6 到 12 个字符", nameof(password));
        }
    }

    private sealed class FailureWindow
    {
        /// <summary>当前失败计数窗口的 UTC 起点。</summary>
        public DateTimeOffset WindowStartedUtc { get; set; }

        /// <summary>窗口内连续失败次数，成功验证或新窗口开始时清零。</summary>
        public int Count { get; set; }
    }
}
