using System.Reflection;
using System.Security.Cryptography;

namespace GuiyangMahjong.Schema;

/// <summary>
/// 解析数据库服务的隔离 Schema 路径。
/// 该源码由中央 MSBuild 契约注入 Auth、Lobby、PlayerData 和 Admin，
/// 以程序集名称作为服务目录的唯一运行时事实来源。
/// </summary>
internal static class ServiceSchemaPath
{
    /// <summary>程序集元数据中的服务名键，用于交叉校验程序集身份和发布目录。</summary>
    private const string ServiceNameMetadataKey = "MahjongSchemaServiceName";

    /// <summary>程序集元数据中的相对路径键；只接受中央构建生成的固定布局。</summary>
    private const string RelativePathMetadataKey = "MahjongSchemaRelativePath";

    /// <summary>程序集元数据中的源 Schema SHA-256 键，用于部署后完整性校验。</summary>
    private const string Sha256MetadataKey = "MahjongSchemaSha256";

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

        var metadata = serviceAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => attribute.Key.StartsWith(
                "MahjongSchema",
                StringComparison.Ordinal))
            .ToDictionary(
                attribute => attribute.Key,
                attribute => attribute.Value,
                StringComparer.Ordinal);
        var declaredServiceName = RequireMetadata(
            metadata,
            ServiceNameMetadataKey);
        if (!declaredServiceName.Equals(serviceName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Schema 服务元数据与程序集不一致：{declaredServiceName} != {serviceName}");
        }

        var expectedRelativePath = $"Schemas/{serviceName}/schema.sql";
        var relativePath = RequireMetadata(
            metadata,
            RelativePathMetadataKey).Replace('\\', '/');
        if (!relativePath.Equals(
                expectedRelativePath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Schema 相对路径元数据无效：{relativePath}");
        }

        var schemaPath = Path.GetFullPath(Path.Combine(
            baseDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(schemaPath))
        {
            throw new FileNotFoundException(
                $"服务 {serviceName} 缺少隔离数据库 Schema。",
                schemaPath);
        }

        var expectedHashText = RequireMetadata(metadata, Sha256MetadataKey);
        byte[] expectedHash;
        try
        {
            expectedHash = Convert.FromHexString(expectedHashText);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                $"服务 {serviceName} 的 Schema 摘要元数据格式无效。",
                exception);
        }
        if (expectedHash.Length != SHA256.HashSizeInBytes)
        {
            CryptographicOperations.ZeroMemory(expectedHash);
            throw new InvalidDataException(
                $"服务 {serviceName} 的 Schema 摘要长度无效。");
        }

        using var schemaStream = File.OpenRead(schemaPath);
        var actualHash = SHA256.HashData(schemaStream);
        try
        {
            // 固定时间比较避免摘要差异通过启动错误时序泄露；校验失败绝不返回可读取路径。
            if (!CryptographicOperations.FixedTimeEquals(
                    actualHash,
                    expectedHash))
            {
                throw new InvalidDataException(
                    $"服务 {serviceName} 的数据库 Schema 完整性校验失败。");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualHash);
            CryptographicOperations.ZeroMemory(expectedHash);
        }
        return schemaPath;
    }

    /// <summary>
    /// 获取中央构建写入的非空程序集元数据；缺失键表示程序集与 Schema 不是同一次构建。
    /// </summary>
    private static string RequireMetadata(
        IReadOnlyDictionary<string, string?> metadata,
        string key)
    {
        if (!metadata.TryGetValue(key, out var value)
            || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"服务程序集缺少 Schema 元数据：{key}");
        }
        return value;
    }
}
