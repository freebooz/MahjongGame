namespace GuiyangMahjong.Admin.Storage;

/// <summary>
/// 为 Admin 的多个 PostgreSQL Store 提供唯一 Schema 发布路径。
/// 集中定义可防止不同 Store 演化出不一致路径，也避免跨服务测试输出覆盖。
/// </summary>
internal static class AdminStoragePaths
{
    /// <summary>
    /// 获取随当前进程发布的 Admin Schema 绝对路径。
    /// 所有 Admin Store 共享该只读路径；文件生命周期由构建和发布系统管理。
    /// </summary>
    internal static string SchemaPath { get; } = Path.Combine(
        AppContext.BaseDirectory,
        "Schemas",
        "Admin",
        "schema.sql");
}
