// 实例凭据服务：签发并验证 Dedicated Server 注册、心跳和故障回报所需的短期凭据。
// 签名材料只驻留服务端，比较采用固定时间算法；过期、实例不匹配或签名损坏均必须拒绝。
using System.Security.Cryptography;

namespace GuiyangMahjong.Allocator.Security;

/// <summary>
/// 一次性生成的实例凭据。
/// Plaintext 仅用于受控启动/注册通道，Hash 是 Allocator 唯一可持久化形式；
/// 调用方应尽快清除明文引用，二者都不能写入日志。
/// </summary>
public sealed record GeneratedCredential(string Plaintext, byte[] Hash);

/// <summary>使用加密随机数签发实例凭据并以固定时间算法验证其 SHA-256 哈希。</summary>
public sealed class InstanceCredentialService
{
    /// <summary>生成 256 位随机凭据及其不可逆哈希；每次调用产生独立值。</summary>
    public GeneratedCredential Generate()
    {
        var plaintext = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return new GeneratedCredential(plaintext, SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plaintext)));
    }

    /// <summary>
    /// 验证明文与预期哈希；空值或异常超长输入直接拒绝，
    /// 比较采用固定时间算法以降低时序侧信道。
    /// </summary>
    public bool Verify(string plaintext, byte[] expectedHash)
    {
        if (string.IsNullOrWhiteSpace(plaintext) || plaintext.Length > 256) return false;
        var actual = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plaintext));
        return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }
}
