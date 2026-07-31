using GuiyangMahjong.Auth.Domain;

namespace GuiyangMahjong.Auth.Auth;

/// <summary>
/// Auth 模块持久化端口，只负责身份解析和游客身份创建。
/// 接口不暴露会话撤销、玩家档案修改、管理命令或任何房间数据。
/// </summary>
public interface IIdentityRepository
{
    /// <summary>
    /// 按不可逆安装摘要原子取得或创建游客身份；相同摘要必须稳定返回同一玩家。
    /// </summary>
    Task<AuthIdentity> GetOrCreateGuestAsync(
        string installationHash,
        AuthIdentity proposedIdentity,
        CancellationToken cancellationToken);
}
