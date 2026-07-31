using System.ComponentModel.DataAnnotations;

namespace GuiyangMahjong.Community.Options;

/// <summary>Community 配置；聊天网关入站身份与 Auth 只读出站身份必须用途隔离。</summary>
public sealed class CommunityOptions
{
    public const string SectionName = "Community";
    public string ChatGatewayToken { get; init; } = string.Empty;
    public string LegacyPlayerDataToken { get; init; } = string.Empty;
    [Required, Url] public string AuthBaseUrl { get; init; } = "http://127.0.0.1:18082";
    public string AuthMonitoringToken { get; init; } = string.Empty;
    [Range(1, 30)] public int AuthTimeoutSeconds { get; init; } = 5;
}
