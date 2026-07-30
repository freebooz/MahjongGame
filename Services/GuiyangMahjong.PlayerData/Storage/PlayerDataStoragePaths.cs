using GuiyangMahjong.Schema;

namespace GuiyangMahjong.PlayerData.Storage;

/// <summary>
/// 集中解析 PlayerData 发布目录中的数据库 Schema。
/// 唯一服务目录用于隔离测试、发布和容器输出中的同名内容文件。
/// </summary>
internal static class PlayerDataStoragePaths
{
    /// <summary>
    /// 获取随当前进程发布的 PlayerData Schema 绝对路径。
    /// 服务目录由程序集名称推导；缺失或不可读错误直接终止初始化，不使用空回退。
    /// </summary>
    internal static string SchemaPath { get; } =
        ServiceSchemaPath.Resolve(typeof(PlayerDataStoragePaths).Assembly);
}
