using GuiyangMahjong.Schema;

namespace GuiyangMahjong.Admin.Storage;

/// <summary>
/// 为 Admin 的多个 PostgreSQL Store 提供唯一 Schema 发布路径。
/// 集中定义可防止不同 Store 演化出不一致路径，也避免跨服务测试输出覆盖。
/// </summary>
internal static class AdminStoragePaths
{
    /// <summary>
    /// 获取随当前进程发布的 Admin Schema 绝对路径。
    /// 服务目录由程序集名称推导；所有 Store 共享该只读且失败关闭的解析结果。
    /// </summary>
    internal static string SchemaPath { get; } =
        ServiceSchemaPath.Resolve(typeof(AdminStoragePaths).Assembly);
}
