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
    /// <summary>阶段 8.3 保证 Economy 是资产唯一实现边界，且旧 PlayerData API 只依赖兼容客户端。</summary>
    [Fact]
    public void EconomyOwnsWalletWrites_AndPlayerDataUsesAdapterOnly()
    {
        var root = FindProjectRoot();
        var economyProject = File.ReadAllText(Path.Combine(root, "Services", "Apps",
            "GuiyangMahjong.Economy", "GuiyangMahjong.Economy.csproj"));
        Assert.DoesNotContain("GuiyangMahjong.PlayerData", economyProject, StringComparison.Ordinal);
        Assert.DoesNotContain("GuiyangMahjong.Admin", economyProject, StringComparison.Ordinal);
        var endpoints = File.ReadAllText(Path.Combine(root, "Services", "GuiyangMahjong.PlayerData",
            "Api", "PlayerDataEndpoints.cs"));
        Assert.Contains("ILegacyEconomyClient", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("store.RecordRewardClaimAsync", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("store.ApplyWalletOperationAsync", endpoints, StringComparison.Ordinal);
        var schema = File.ReadAllText(Path.Combine(root, "Services", "GuiyangMahjong.PlayerData", "Storage", "schema.sql"));
        Assert.Contains("reject_legacy_economy_write", schema, StringComparison.Ordinal);
    }
    /// <summary>阶段 7 GameData 必须保持模块目录与跨服务依赖边界，不得引用 Lobby、PlayerData 或 Admin 实现。</summary>
    [Fact]
    public void GameData_HasRequiredModules_AndNoBusinessImplementationReferences()
    {
        var root = Path.Combine(FindProjectRoot(), "Services", "Apps", "GuiyangMahjong.GameData");
        foreach (var module in new[]
                 { "Settlement", "GameRecords", "ReplayEvidence", "Leaderboards", "Administration", "Infrastructure" })
            Assert.True(Directory.Exists(Path.Combine(root, module)), $"GameData 缺少职责模块目录：{module}");
        var project = XDocument.Load(Path.Combine(root, "GuiyangMahjong.GameData.csproj"));
        var references = project.Descendants("ProjectReference")
            .Select(node => node.Attribute("Include")?.Value ?? string.Empty).ToArray();
        Assert.DoesNotContain(references, value => value.Contains("GuiyangMahjong.Lobby", StringComparison.Ordinal));
        Assert.DoesNotContain(references, value => value.Contains("GuiyangMahjong.PlayerData", StringComparison.Ordinal));
        Assert.DoesNotContain(references, value => value.Contains("GuiyangMahjong.Admin", StringComparison.Ordinal));
    }

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
    /// 验证阶段 4 LobbyControl 的职责目录真实存在，并阻止 Lobby 直接引入 Kubernetes/Agones 客户端。
    /// Allocator 仍是唯一允许执行编排操作的服务边界。
    /// </summary>
    [Fact]
    public void LobbyControl_HasRequiredModuleBoundaries_AndDoesNotCallKubernetes()
    {
        var lobbyRoot = Path.Combine(
            FindProjectRoot(),
            "Services",
            "GuiyangMahjong.Lobby");
        var requiredModules = new[]
        {
            "Lobby",
            "Rooms",
            "Matchmaking",
            "Reconnection",
            "GameRouting",
            "Administration",
            "Infrastructure"
        };
        foreach (var module in requiredModules)
        {
            Assert.True(
                Directory.Exists(Path.Combine(lobbyRoot, module)),
                $"LobbyControl 缺少职责模块目录：{module}");
        }

        var projectText = File.ReadAllText(Path.Combine(
            lobbyRoot,
            "GuiyangMahjong.Lobby.csproj"));
        Assert.DoesNotContain(
            "KubernetesClient",
            projectText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Agones",
            projectText,
            StringComparison.OrdinalIgnoreCase);

        var roomContracts = File.ReadAllText(Path.Combine(
            lobbyRoot,
            "Rooms",
            "RoomModuleContracts.cs"));
        Assert.Contains("IRoomReader", roomContracts, StringComparison.Ordinal);
        Assert.Contains("IRoomWriter", roomContracts, StringComparison.Ordinal);
        Assert.Contains("StateVersion", roomContracts, StringComparison.Ordinal);
        Assert.Contains("RoomEpoch", roomContracts, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证阶段 5 的统一 Provider 真实存在，并确保 Lobby、Admin、Auth 与 PlayerData
    /// 既不能调用 Agones API，也不能直接启动 Dedicated Server 子进程。
    /// </summary>
    [Fact]
    public void AllocationService_IsTheOnlyGameServerProviderBoundary()
    {
        var root = FindProjectRoot();
        var allocatorRoot = Path.Combine(root, "Services", "GuiyangMahjong.Allocator");
        var providerContract = File.ReadAllText(Path.Combine(
            allocatorRoot,
            "Providers",
            "GameServerProviderContracts.cs"));
        foreach (var method in new[]
                 {
                     "AllocateAsync", "GetStatusAsync", "DrainAsync", "TerminateAsync",
                     "RenewLeaseAsync", "ReportReadyAsync", "ReportUnhealthyAsync"
                 })
        {
            Assert.Contains(method, providerContract, StringComparison.Ordinal);
        }

        Assert.True(File.Exists(Path.Combine(
            allocatorRoot,
            "Providers",
            "LocalProcessGameServerProvider.cs")));
        Assert.True(File.Exists(Path.Combine(
            allocatorRoot,
            "Providers",
            "AgonesGameServerProvider.cs")));

        var forbiddenRoots = new[]
        {
            "GuiyangMahjong.Lobby",
            "GuiyangMahjong.Admin",
            "GuiyangMahjong.Auth",
            "GuiyangMahjong.PlayerData"
        };
        var forbiddenMarkers = new[]
        {
            "IAgonesAllocationClient",
            "allocation.agones.dev",
            "GameServerProcessLauncher",
            "Process.Start("
        };
        foreach (var project in forbiddenRoots)
        {
            var sources = Directory
                .EnumerateFiles(
                    Path.Combine(root, "Services", project),
                    "*.cs",
                    SearchOption.AllDirectories)
                .Where(path => !path.Contains(
                    $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase)
                    && !path.Contains(
                        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText)
                .ToArray();
            foreach (var marker in forbiddenMarkers)
                Assert.DoesNotContain(sources, source => source.Contains(marker, StringComparison.Ordinal));
        }

        var bridgeHeader = File.ReadAllText(Path.Combine(
            root,
            "Source",
            "GuiyangMahjongServer",
            "Public",
            "Server",
            "GuiyangGameServerBridge.h"));
        var bridgeSource = File.ReadAllText(Path.Combine(
            root,
            "Source",
            "GuiyangMahjongServer",
            "Private",
            "Server",
            "GuiyangGameServerBridge.cpp"));
        var agonesSource = File.ReadAllText(Path.Combine(
            root,
            "Source",
            "GuiyangMahjongServer",
            "Private",
            "Server",
            "GuiyangAgonesLifecycleSubsystem.cpp"));
        Assert.Contains("LeaseFencingToken", bridgeHeader, StringComparison.Ordinal);
        Assert.Contains("fencingToken", bridgeSource, StringComparison.Ordinal);
        Assert.Contains("mahjong.freebooz/fencing-token", agonesSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证阶段 2 Contracts/BuildingBlocks 的单向依赖图。
    /// Contracts 禁止数据库和业务实现依赖，BuildingBlocks 禁止反向依赖生产服务或 UE，
    /// Persistence 是唯一允许引用 Npgsql 的新基础项目。
    /// </summary>
    [Fact]
    public void ContractsAndBuildingBlocks_RespectDependencyDirection()
    {
        var servicesRoot = Path.Combine(FindProjectRoot(), "Services");
        var contractsRoot = Path.Combine(servicesRoot, "Contracts");
        var buildingBlocksRoot = Path.Combine(
            servicesRoot,
            "BuildingBlocks");
        var failures = new List<string>();
        var projects = Directory
            .EnumerateFiles(
                contractsRoot,
                "*.csproj",
                SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(
                buildingBlocksRoot,
                "*.csproj",
                SearchOption.AllDirectories))
            .Where(path =>
                !path.Contains(
                    "GuiyangMahjong.BuildingBlocks.Tests",
                    StringComparison.Ordinal))
            .ToArray();

        foreach (var projectPath in projects)
        {
            var projectName =
                Path.GetFileNameWithoutExtension(projectPath);
            var projectDirectory =
                Path.GetDirectoryName(projectPath)
                ?? throw new InvalidDataException(
                    $"项目目录无效：{projectPath}");
            var document = XDocument.Load(projectPath);
            var references = document
                .Descendants("ProjectReference")
                .Select(reference =>
                {
                    var include =
                        reference.Attribute("Include")?.Value
                        ?? string.Empty;
                    return Path.GetFileNameWithoutExtension(
                        Path.GetFullPath(
                            Path.Combine(projectDirectory, include)));
                })
                .ToArray();
            var packages = document
                .Descendants("PackageReference")
                .Select(reference =>
                    reference.Attribute("Include")?.Value
                    ?? string.Empty)
                .ToArray();

            if (references.Any(reference =>
                    BusinessServiceNames.Contains(
                        reference,
                        StringComparer.Ordinal)
                    || reference is "GuiyangMahjong.EdgeGateway"))
            {
                failures.Add(
                    $"{projectName} 反向依赖业务服务："
                    + string.Join(",", references));
            }

            if (projectName.StartsWith(
                    "GuiyangMahjong.Contracts.",
                    StringComparison.Ordinal))
            {
                if (references.Any(reference =>
                        !reference.Equals(
                            "GuiyangMahjong.Contracts.Common",
                            StringComparison.Ordinal)))
                    failures.Add(
                        $"{projectName} 包含非法契约引用："
                        + string.Join(",", references));
                if (packages.Any(package =>
                        package.Contains(
                            "EntityFramework",
                            StringComparison.OrdinalIgnoreCase)
                        || package.Equals(
                            "Npgsql",
                            StringComparison.OrdinalIgnoreCase)))
                    failures.Add(
                        $"{projectName} 引用了持久化包。");
            }

            if (projectName.Equals(
                    "GuiyangMahjong.BuildingBlocks.Domain",
                    StringComparison.Ordinal)
                && document.Descendants("FrameworkReference").Any())
                failures.Add("Domain BuildingBlock 引用了 ASP.NET Core。");

            if (!projectName.Equals(
                    "GuiyangMahjong.BuildingBlocks.Persistence",
                    StringComparison.Ordinal)
                && packages.Any(package =>
                    package.Equals(
                        "Npgsql",
                        StringComparison.OrdinalIgnoreCase)
                    || package.Contains(
                        "EntityFramework",
                        StringComparison.OrdinalIgnoreCase)))
                failures.Add(
                    $"{projectName} 越界引用持久化包。");
        }

        Assert.Equal(10, projects.Length);
        Assert.Empty(failures);
    }

    /// <summary>
    /// 验证阶段9的消息边界：Workers 只能依赖契约与 BuildingBlocks，不能引用任一业务服务实现；
    /// 同时禁止在 MVP 中并行引入 Kafka/RabbitMQ，避免形成多套投递和运维语义。
    /// </summary>
    [Fact]
    public void ReliableMessaging_UsesSingleTransportAndKeepsWorkersOutOfBusinessImplementations()
    {
        var servicesRoot = Path.Combine(FindProjectRoot(), "Services");
        var workersProject = Path.Combine(
            servicesRoot,
            "Apps",
            "GuiyangMahjong.Workers",
            "GuiyangMahjong.Workers.csproj");
        var document = XDocument.Load(workersProject);
        var projectDirectory = Path.GetDirectoryName(workersProject)!;
        var references = document.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(Path.GetFullPath(
                Path.Combine(projectDirectory, reference.Attribute("Include")!.Value))))
            .ToArray();
        Assert.DoesNotContain(references, reference =>
            BusinessServiceNames.Contains(reference, StringComparer.Ordinal)
            || reference == "GuiyangMahjong.GameData");

        var productionProjects = Directory.EnumerateFiles(
            servicesRoot,
            "*.csproj",
            SearchOption.AllDirectories)
            .Where(path => !path.Contains(".Tests", StringComparison.OrdinalIgnoreCase));
        var packages = productionProjects
            .SelectMany(path => XDocument.Load(path).Descendants("PackageReference"))
            .Select(reference => reference.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();
        Assert.Contains(packages, package => package.Equals("NATS.Net", StringComparison.Ordinal));
        Assert.DoesNotContain(packages, package =>
            package.Contains("Kafka", StringComparison.OrdinalIgnoreCase)
            || package.Contains("RabbitMQ", StringComparison.OrdinalIgnoreCase));
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
    /// 验证 IdentityApp 的六个模块目录和关键依赖边界已经真实建立。
    /// Auth 不得引用房间业务表，Players 不得读取签名配置或 Refresh Token 模型。
    /// </summary>
    [Fact]
    public void IdentityApp_ModulesRespectSecurityAndDataOwnershipBoundaries()
    {
        var identityRoot = Path.Combine(
            FindProjectRoot(),
            "Services",
            "GuiyangMahjong.Auth");
        var expectedModules = new[]
        {
            "Auth",
            "Sessions",
            "Players",
            "Devices",
            "Administration",
            "Infrastructure"
        };
        foreach (var module in expectedModules)
        {
            var modulePath = Path.Combine(identityRoot, module);
            Assert.True(
                Directory.Exists(modulePath),
                $"IdentityApp 缺少模块目录：{modulePath}");
            Assert.NotEmpty(Directory.EnumerateFiles(
                modulePath,
                "*.cs",
                SearchOption.AllDirectories));
        }

        var identitySources = Directory
            .EnumerateFiles(identityRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase)
                && !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText)
            .ToArray();
        var forbiddenRoomOwnershipMarkers = new[]
        {
            "lobby_rooms",
            "active_player_rooms",
            "match_results",
            "room_event_history",
            "player_room_history"
        };
        foreach (var marker in forbiddenRoomOwnershipMarkers)
            Assert.DoesNotContain(identitySources, text => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

        var playerSources = Directory
            .EnumerateFiles(
                Path.Combine(identityRoot, "Players"),
                "*.cs",
                SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();
        var forbiddenCredentialDependencies = new[]
        {
            "TokenSigningKey",
            "PlayerAccessTokenIssuer",
            "RefreshSession",
            "GuiyangMahjong.Auth.Security",
            "AuthOptions"
        };
        foreach (var marker in forbiddenCredentialDependencies)
            Assert.DoesNotContain(playerSources, text => text.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>
    /// 阶段 8.1 冻结玩家资料和在线会话的数据所有权：长期资料只能由 Identity/Players
    /// 持久化，会话只能由 Identity/Sessions 持久化。PlayerData 不得因兼容需求重新创建
    /// 同名表或写入口，否则会形成两个权威来源并使后续下线无法完成。
    /// </summary>
    [Fact]
    public void PlayerData_DoesNotOwnPlayerProfilesOrSessions()
    {
        var root = FindProjectRoot();
        var identitySchema = File.ReadAllText(Path.Combine(
            root,
            "Services",
            "GuiyangMahjong.Auth",
            "Storage",
            "schema.sql"));
        var playerDataRoot = Path.Combine(
            root,
            "Services",
            "GuiyangMahjong.PlayerData");
        var playerDataSchema = File.ReadAllText(Path.Combine(
            playerDataRoot,
            "Storage",
            "schema.sql"));
        var playerDataEndpoints = File.ReadAllText(Path.Combine(
            playerDataRoot,
            "Api",
            "PlayerDataEndpoints.cs"));

        // Identity 已存在真实权威表；先验证目标存在，避免仅检查“旧服务没有”造成伪通过。
        Assert.Contains(
            "CREATE TABLE IF NOT EXISTS player.player_profiles",
            identitySchema,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "CREATE TABLE IF NOT EXISTS session.auth_refresh_sessions",
            identitySchema,
            StringComparison.OrdinalIgnoreCase);

        // PlayerData 的数据库与 HTTP 边界都不得重新暴露资料或会话写能力。
        foreach (var forbiddenTable in new[]
                 {
                     "player_data.player_profiles",
                     "player_data.player_profile",
                     "player_data.sessions",
                     "player_data.player_sessions"
                 })
        {
            Assert.DoesNotContain(
                forbiddenTable,
                playerDataSchema,
                StringComparison.OrdinalIgnoreCase);
        }
        foreach (var forbiddenRoute in new[]
                 {
                     "/profiles",
                     "/profile",
                     "/sessions",
                     "/session"
                 })
        {
            Assert.DoesNotContain(
                forbiddenRoute,
                playerDataEndpoints,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 阶段8.2以后GameData必须拥有Replay索引表，PlayerData旧URL只能调用窄适配器，
    /// 并由数据库触发器拒绝任何遗留Replay写入，防止回归为长期双写。
    /// </summary>
    [Fact]
    public void ReplayEvidence_HasSingleGameDataWriterAndPlayerDataFailClosedGuard()
    {
        var root = FindProjectRoot();
        var gameDataSchema = File.ReadAllText(Path.Combine(
            root, "Services", "Apps", "GuiyangMahjong.GameData", "Storage", "schema.sql"));
        var playerDataSchema = File.ReadAllText(Path.Combine(
            root, "Services", "GuiyangMahjong.PlayerData", "Storage", "schema.sql"));
        var endpoints = File.ReadAllText(Path.Combine(
            root, "Services", "GuiyangMahjong.PlayerData", "Api", "PlayerDataEndpoints.cs"));

        Assert.Contains("replay.legacy_player_evidence", gameDataSchema, StringComparison.Ordinal);
        Assert.Contains("trg_reject_replay_evidence_write", playerDataSchema, StringComparison.Ordinal);
        Assert.Contains("ILegacyReplayEvidenceClient replayClient", endpoints, StringComparison.Ordinal);
        Assert.Contains("replayClient.RecordAsync", endpoints, StringComparison.Ordinal);
        var replayStart = endpoints.IndexOf("sources.MapPost(\"/replays\"", StringComparison.Ordinal);
        var replayEnd = endpoints.IndexOf("app.MapPost(\"/internal/admin", replayStart, StringComparison.Ordinal);
        Assert.True(replayStart >= 0 && replayEnd > replayStart);
        Assert.DoesNotContain(
            "store.RecordEvidenceAsync",
            endpoints[replayStart..replayEnd],
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 阶段10冻结 Admin 数据和基础设施边界：生产项目不得引用业务实现、Kubernetes/Agones SDK，
    /// 持久化写 SQL 只能指向 admin_monitor，防止后台演化为任意数据修改工具。
    /// </summary>
    [Fact]
    public void Admin_UsesOwnedSchemaAndControlledServiceCommandsOnly()
    {
        var root = FindProjectRoot();
        var adminRoot = Path.Combine(root, "Services", "GuiyangMahjong.Admin");
        var project = XDocument.Load(Path.Combine(adminRoot, "GuiyangMahjong.Admin.csproj"));
        var references = project.Descendants("ProjectReference")
            .Select(item => item.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();
        foreach (var forbidden in new[]
                 {
                     "GuiyangMahjong.Auth", "GuiyangMahjong.Lobby", "GuiyangMahjong.Allocator",
                     "GuiyangMahjong.PlayerData", "GuiyangMahjong.GameData"
                 })
        {
            Assert.DoesNotContain(references, value => value.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }

        var packages = project.Descendants("PackageReference")
            .Select(item => item.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain(packages, value => value.Contains("Kubernetes", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(packages, value => value.Contains("Agones", StringComparison.OrdinalIgnoreCase));

        var sqlTexts = Directory.EnumerateFiles(adminRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText)
            .ToArray();
        foreach (var forbiddenMutation in new[]
                 {
                     "INSERT INTO room.", "UPDATE room.", "DELETE FROM room.",
                     "INSERT INTO settlement.", "UPDATE settlement.", "DELETE FROM settlement.",
                     "INSERT INTO player_data.", "UPDATE player_data.", "DELETE FROM player_data.",
                     "INSERT INTO auth.", "UPDATE auth.", "DELETE FROM auth."
                 })
        {
            Assert.DoesNotContain(sqlTexts, text => text.Contains(forbiddenMutation, StringComparison.OrdinalIgnoreCase));
        }

        // 模块必须包含真实可调用代码，避免仅建立空目录通过结构验收。
        Assert.True(File.Exists(Path.Combine(adminRoot, "TrustSafety", "TrustSafetyReadModels.cs")));
        Assert.True(File.Exists(Path.Combine(adminRoot, "Api", "TrustSafetyEndpoints.cs")));
        Assert.True(File.Exists(Path.Combine(adminRoot, "Api", "AdminBffEndpoints.cs")));
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
