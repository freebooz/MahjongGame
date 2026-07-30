using System.Security.Cryptography;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace GuiyangMahjong.Architecture.Tests;

/// <summary>
/// 验证服务项目依赖方向、测试程序集边界和数据库 Schema 输出契约。
/// 测试只读取仓库声明和构建输出，不启动服务或修改外部系统。
/// </summary>
public sealed class ProjectArchitectureTests
{
    /// <summary>
    /// 参与依赖方向约束的业务服务名称；Observability 等横切基础设施不在此集合。
    /// </summary>
    private static readonly string[] BusinessServiceNames =
    [
        "GuiyangMahjong.Admin",
        "GuiyangMahjong.Allocator",
        "GuiyangMahjong.Auth",
        "GuiyangMahjong.Lobby",
        "GuiyangMahjong.PlayerData"
    ];

    /// <summary>
    /// 同时引用四个数据库服务后检查唯一目标和代表性表名，
    /// 防止增量构建把一个服务的 Schema 静默覆盖到另一个服务。
    /// </summary>
    [Fact]
    public void AllServiceSchemas_ArePublishedToUniqueOutputPaths()
    {
        var expectedMarkers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Auth"] = "auth_identities",
            ["Lobby"] = "lobby_rooms",
            ["PlayerData"] = "player_data.wallet_balances",
            ["Admin"] = "admin_monitor"
        };
        var resolvedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var schemaOutputRoot = Path.Combine(
            AppContext.BaseDirectory,
            "Schemas");

        foreach (var (serviceName, marker) in expectedMarkers)
        {
            var projectRoot = FindProjectRoot();
            var serviceDirectory = Path.Combine(
                projectRoot,
                "Services",
                $"GuiyangMahjong.{serviceName}");
            var sourcePath = Path.Combine(
                serviceDirectory,
                "Storage",
                "schema.sql");
            var path = Path.Combine(
                AppContext.BaseDirectory,
                "Schemas",
                serviceName,
                "schema.sql");
            Assert.True(
                File.Exists(sourcePath),
                $"缺少 {serviceName} Schema 源文件：{sourcePath}");
            Assert.True(File.Exists(path), $"缺少 {serviceName} Schema：{path}");
            Assert.True(resolvedPaths.Add(Path.GetFullPath(path)));
            Assert.Contains(marker, File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Equal(
                File.ReadAllBytes(sourcePath),
                File.ReadAllBytes(path));
        }

        var actualRelativePaths = Directory
            .EnumerateFiles(
                schemaOutputRoot,
                "schema.sql",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(
                AppContext.BaseDirectory,
                path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expectedRelativePaths = expectedMarkers.Keys
            .Select(serviceName => $"Schemas/{serviceName}/schema.sql")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedRelativePaths, actualRelativePaths);
        Assert.False(File.Exists(Path.Combine(
            AppContext.BaseDirectory,
            "schema.sql")));
        Assert.False(File.Exists(Path.Combine(
            AppContext.BaseDirectory,
            "Storage",
            "schema.sql")));
    }

    /// <summary>
    /// 确认数据库服务只声明安全且唯一的服务名，复制和哈希校验统一来自
    /// Directory.Build.targets，禁止项目重新引入手写 TargetPath。
    /// </summary>
    [Fact]
    public void SchemaProjects_UseCentralizedCollisionProofBuildContract()
    {
        var projectRoot = FindProjectRoot();
        var servicesRoot = Path.Combine(projectRoot, "Services");
        var expectedServiceNames = new[]
        {
            "Auth",
            "Lobby",
            "PlayerData",
            "Admin"
        };
        var declaredServiceNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        var targetsPath = Path.Combine(
            servicesRoot,
            "Directory.Build.targets");
        Assert.True(File.Exists(targetsPath), $"缺少 Schema 构建契约：{targetsPath}");
        var targetsText = File.ReadAllText(targetsPath);
        Assert.Contains("ValidateMahjongSchemaConfiguration", targetsText);
        Assert.Contains("ValidateMahjongSchemaBuildOutput", targetsText);
        Assert.Contains("ValidateMahjongSchemaPublishOutput", targetsText);
        // 共享运行时源码必须位于语义明确的 Schema 目录，防止再次混入仅承载构建配置的 Build 目录。
        Assert.Contains(
            @"Schema\ServiceSchemaPath.cs",
            targetsText);

        foreach (var serviceName in expectedServiceNames)
        {
            var projectPath = Path.Combine(
                servicesRoot,
                $"GuiyangMahjong.{serviceName}",
                $"GuiyangMahjong.{serviceName}.csproj");
            var project = XDocument.Load(projectPath);
            var declaration = project
                .Descendants("MahjongSchemaServiceName")
                .Select(element => element.Value.Trim())
                .Single();

            Assert.Equal(serviceName, declaration);
            Assert.True(
                declaredServiceNames.Add(declaration),
                $"重复 Schema 服务目录：{declaration}");
            Assert.DoesNotContain(
                project.Descendants("Content"),
                content => string.Equals(
                    content.Attribute("Include")?.Value,
                    @"Storage\schema.sql",
                    StringComparison.OrdinalIgnoreCase));

            var storagePathsSource = Path.Combine(
                servicesRoot,
                $"GuiyangMahjong.{serviceName}",
                "Storage",
                $"{serviceName}StoragePaths.cs");
            var storagePathsText = File.ReadAllText(storagePathsSource);
            Assert.Contains("ServiceSchemaPath.Resolve", storagePathsText);
            Assert.DoesNotContain("Path.Combine", storagePathsText);
        }
    }

    /// <summary>
    /// 通过各服务程序集的内部 StoragePaths 验证实际运行时读取路径，
    /// 并确认共享解析器在 Schema 缺失时抛出 FileNotFoundException。
    /// </summary>
    [Fact]
    public void RuntimeSchemaResolvers_MatchBuildLayoutAndFailClosed()
    {
        var serviceNames = new[]
        {
            "Auth",
            "Lobby",
            "PlayerData",
            "Admin"
        };

        foreach (var serviceName in serviceNames)
        {
            var assemblyPath = Path.Combine(
                AppContext.BaseDirectory,
                $"GuiyangMahjong.{serviceName}.dll");
            var assembly = Assembly.LoadFrom(assemblyPath);
            var sourcePath = Path.Combine(
                FindProjectRoot(),
                "Services",
                $"GuiyangMahjong.{serviceName}",
                "Storage",
                "schema.sql");
            var metadata = assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(attribute => attribute.Key.StartsWith(
                    "MahjongSchema",
                    StringComparison.Ordinal))
                .ToDictionary(
                    attribute => attribute.Key,
                    attribute => attribute.Value,
                    StringComparer.Ordinal);
            Assert.Equal(serviceName, metadata["MahjongSchemaServiceName"]);
            Assert.Equal(
                $"Schemas/{serviceName}/schema.sql",
                metadata["MahjongSchemaRelativePath"]?.Replace('\\', '/'));
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(
                    File.ReadAllBytes(sourcePath))),
                metadata["MahjongSchemaSha256"],
                ignoreCase: true);

            var storagePathsType = assembly.GetType(
                $"GuiyangMahjong.{serviceName}.Storage.{serviceName}StoragePaths",
                throwOnError: true)
                ?? throw new InvalidOperationException(
                    $"无法加载 {serviceName} StoragePaths。");
            var schemaProperty = storagePathsType.GetProperty(
                "SchemaPath",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    $"{serviceName} 缺少内部 SchemaPath 属性。");
            var actualPath = Assert.IsType<string>(
                schemaProperty.GetValue(null));
            var expectedPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "Schemas",
                serviceName,
                "schema.sql"));
            Assert.Equal(expectedPath, actualPath);

            var resolverType = assembly.GetType(
                "GuiyangMahjong.Schema.ServiceSchemaPath",
                throwOnError: true)
                ?? throw new InvalidOperationException(
                    $"{serviceName} 未注入共享 Schema 解析器。");
            var testableResolve = resolverType.GetMethod(
                "Resolve",
                BindingFlags.Static | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(Assembly), typeof(string)],
                modifiers: null)
                ?? throw new InvalidOperationException(
                    $"{serviceName} Schema 解析器缺少可测试重载。");
            var missingRoot = Path.Combine(
                Path.GetTempPath(),
                $"missing-mahjong-schema-{Guid.NewGuid():N}");
            var invocation = Assert.Throws<TargetInvocationException>(() =>
                testableResolve.Invoke(null, [assembly, missingRoot]));
            Assert.IsType<FileNotFoundException>(invocation.InnerException);

            var tamperedRoot = Path.Combine(
                Path.GetTempPath(),
                $"tampered-mahjong-schema-{Guid.NewGuid():N}");
            var tamperedSchemaPath = Path.Combine(
                tamperedRoot,
                "Schemas",
                serviceName,
                "schema.sql");
            Directory.CreateDirectory(
                Path.GetDirectoryName(tamperedSchemaPath)
                    ?? throw new InvalidOperationException(
                        "篡改测试 Schema 目录无效。"));
            try
            {
                File.Copy(sourcePath, tamperedSchemaPath);
                File.AppendAllText(
                    tamperedSchemaPath,
                    $"{Environment.NewLine}-- integrity-test-tamper");
                var tamperedInvocation =
                    Assert.Throws<TargetInvocationException>(() =>
                        testableResolve.Invoke(
                            null,
                            [assembly, tamperedRoot]));
                Assert.IsType<InvalidDataException>(
                    tamperedInvocation.InnerException);
            }
            finally
            {
                Directory.Delete(tamperedRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// 解析生产项目引用图，确保业务服务只能共享 Observability 等基础设施，
    /// 不能通过程序集直接依赖另一个业务服务的内部实现。
    /// </summary>
    [Fact]
    public void ProductionServices_DoNotReferenceOtherBusinessServices()
    {
        var servicesRoot = Path.Combine(FindProjectRoot(), "Services");
        var failures = new List<string>();

        foreach (var serviceName in BusinessServiceNames)
        {
            var projectPath = Path.Combine(
                servicesRoot,
                serviceName,
                $"{serviceName}.csproj");
            var projectDirectory = Path.GetDirectoryName(projectPath)
                ?? throw new InvalidDataException($"项目目录无效：{projectPath}");
            var document = XDocument.Load(projectPath);

            foreach (var reference in document.Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }
                var referencedPath = Path.GetFullPath(
                    Path.Combine(projectDirectory, include));
                var referencedName = Path.GetFileNameWithoutExtension(referencedPath);
                if (BusinessServiceNames.Contains(referencedName, StringComparer.Ordinal)
                    && referencedName != serviceName)
                {
                    failures.Add($"{serviceName} -> {referencedName}");
                }
            }
        }

        Assert.Empty(failures);
    }

    /// <summary>
    /// 检查测试工程和源码不再使用程序集别名；兼容性必须通过 Contracts
    /// 中的机器契约验证，而不是共享被测服务内部类型。
    /// </summary>
    [Fact]
    public void ServiceTests_DoNotUseAssemblyAliases()
    {
        var servicesRoot = Path.Combine(FindProjectRoot(), "Services");
        var failures = new List<string>();
        var testDirectories = Directory.GetDirectories(
            servicesRoot,
            "GuiyangMahjong.*.Tests",
            SearchOption.TopDirectoryOnly);

        foreach (var testDirectory in testDirectories)
        {
            foreach (var projectPath in Directory.GetFiles(
                         testDirectory,
                         "*.csproj",
                         SearchOption.TopDirectoryOnly))
            {
                if (File.ReadAllText(projectPath).Contains(
                        "<Aliases>",
                        StringComparison.Ordinal))
                {
                    failures.Add(projectPath);
                }
            }
            foreach (var sourcePath in EnumerateSourceFiles(testDirectory))
            {
                if (File.ReadLines(sourcePath).Any(line =>
                        line.TrimStart().StartsWith(
                            "extern alias ",
                            StringComparison.Ordinal)))
                {
                    failures.Add(sourcePath);
                }
            }
        }

        Assert.Empty(failures);
    }

    /// <summary>
    /// 从测试输出逐级查找同时含 `.git`、`.uproject` 和 Services 的项目根。
    /// 找不到时抛出异常，避免架构测试误读其他目录后给出伪通过。
    /// </summary>
    private static string FindProjectRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git"))
                && File.Exists(Path.Combine(
                    current.FullName,
                    "GuiyangMahjong.uproject"))
                && Directory.Exists(Path.Combine(current.FullName, "Services")))
            {
                return current.FullName;
            }
        }
        throw new DirectoryNotFoundException("无法从测试输出定位麻将项目根目录。");
    }

    /// <summary>
    /// 枚举测试源码并跳过 bin/obj 生成目录，防止扫描编译缓存造成重复或误报。
    /// </summary>
    private static IEnumerable<string> EnumerateSourceFiles(string testDirectory)
    {
        return Directory.EnumerateFiles(
                testDirectory,
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains(
                    $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase)
                && !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase));
    }
}
