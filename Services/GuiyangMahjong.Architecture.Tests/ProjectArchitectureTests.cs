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

        foreach (var (serviceName, marker) in expectedMarkers)
        {
            var path = Path.Combine(
                AppContext.BaseDirectory,
                "Schemas",
                serviceName,
                "schema.sql");
            Assert.True(File.Exists(path), $"缺少 {serviceName} Schema：{path}");
            Assert.True(resolvedPaths.Add(Path.GetFullPath(path)));
            Assert.Contains(marker, File.ReadAllText(path), StringComparison.Ordinal);
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
