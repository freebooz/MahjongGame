namespace GuiyangMahjong.Auth.Players;

/// <summary>
/// IdentityApp 内的玩家长期档案。该模型不包含凭证、会话、房间、战绩或资产，
/// 后续可以在保持部署单元不变的前提下独立演进。
/// </summary>
public sealed record PlayerProfile(
    string PlayerId,
    string DisplayName,
    string? AvatarUrl,
    string? Region,
    int Level,
    string SettingsJson,
    string PrivacySettingsJson,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// 玩家档案模块的只读边界。认证模块只能读取生成登录响应所需的显示摘要，
/// 不得通过该接口访问 Token 私钥或会话凭证。
/// </summary>
public interface IPlayerProfileReader
{
    /// <summary>按玩家标识读取档案；玩家不存在时返回空，不自动创建任何业务数据。</summary>
    Task<PlayerProfile?> GetProfileAsync(
        string playerId,
        CancellationToken cancellationToken);
}
