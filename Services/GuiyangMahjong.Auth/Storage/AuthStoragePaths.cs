using GuiyangMahjong.Schema;

namespace GuiyangMahjong.Auth.Storage;

/// <summary>
/// 集中解析 Auth 发布目录中的数据库 Schema。
/// 路径包含服务名，确保组合测试输出不会被其他服务的同名文件覆盖。
/// </summary>
internal static class AuthStoragePaths
{
    /// <summary>
    /// 获取随当前进程发布的 Auth Schema 绝对路径。
    /// 服务目录由程序集名称推导；文件缺失时解析器立即失败并阻止数据库初始化。
    /// </summary>
    internal static string SchemaPath { get; } =
        ServiceSchemaPath.Resolve(typeof(AuthStoragePaths).Assembly);
}
