using System.Reflection;

namespace GuiyangMahjong.Schema;

/// <summary>
/// 解析数据库服务的隔离 Schema 路径。
/// 该源码由中央 MSBuild 契约注入 Auth、Lobby、PlayerData 和 Admin，
/// 以程序集名称作为服务目录的唯一运行时事实来源。
/// </summary>
internal static class ServiceSchemaPath
{
    /// <summary>
    /// 从当前服务程序集推导发布目录中的 Schema 绝对路径。
    /// 文件缺失或程序集未进入白名单时立即失败，禁止服务在不完整数据库契约下启动。
    /// </summary>
    internal static string Resolve(Assembly serviceAssembly) =>
        Resolve(serviceAssembly, AppContext.BaseDirectory);

    /// <summary>
    /// 使用指定基目录解析 Schema，供架构测试验证缺失文件失败语义。
    /// 输入程序集必须是中央契约登记的数据库服务，基目录不由本方法创建或修改。
    /// </summary>
    internal static string Resolve(
        Assembly serviceAssembly,
        string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(serviceAssembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        const string assemblyPrefix = "GuiyangMahjong.";
        var assemblyName = serviceAssembly.GetName().Name
            ?? throw new InvalidOperationException("服务程序集缺少名称。");
        if (!assemblyName.StartsWith(
                assemblyPrefix,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Schema 服务程序集名称无效：{assemblyName}");
        }

        var serviceName = assemblyName[assemblyPrefix.Length..];
        if (serviceName is not ("Auth" or "Lobby" or "PlayerData" or "Admin"))
        {
            throw new InvalidOperationException(
                $"Schema 服务未进入中央白名单：{serviceName}");
        }

        var schemaPath = Path.GetFullPath(Path.Combine(
            baseDirectory,
            "Schemas",
            serviceName,
            "schema.sql"));
        if (!File.Exists(schemaPath))
        {
            throw new FileNotFoundException(
                $"服务 {serviceName} 缺少隔离数据库 Schema。",
                schemaPath);
        }
        return schemaPath;
    }
}
