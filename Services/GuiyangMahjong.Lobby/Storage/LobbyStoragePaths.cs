using GuiyangMahjong.Schema;

namespace GuiyangMahjong.Lobby.Storage;

/// <summary>
/// 集中解析 Lobby 发布目录中的数据库 Schema。
/// 路径包含服务名，避免与 Auth、PlayerData 或 Admin 的同名文件发生覆盖。
/// </summary>
internal static class LobbyStoragePaths
{
    /// <summary>
    /// 获取随当前进程发布的 Lobby Schema 绝对路径。
    /// 服务目录由程序集名称推导；缺失文件必须阻止服务在不完整表结构上运行。
    /// </summary>
    internal static string SchemaPath { get; } =
        ServiceSchemaPath.Resolve(typeof(LobbyStoragePaths).Assembly);
}
