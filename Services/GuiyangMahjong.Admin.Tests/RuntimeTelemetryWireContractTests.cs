using System.Text.Json;
using GuiyangMahjong.Admin.Domain;

namespace GuiyangMahjong.Admin.Tests;

/// <summary>
/// 验证 Admin 消费模型遵守独立的运行遥测 v1 JSON Schema。
/// 测试不引用 Lobby 生产程序集，使服务可以独立编译，同时由同一机器契约阻止字段漂移。
/// </summary>
public sealed class RuntimeTelemetryWireContractTests
{
    /// <summary>
    /// 使用 ASP.NET Core 默认 Web JSON 规则验证真实线格式，包括 camelCase 与可选字段默认值。
    /// </summary>
    private static readonly JsonSerializerOptions WireJsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 校验房间及嵌套模型的公开属性与 Schema 完全一致。
    /// 多余或缺失字段都会失败，避免 Admin 静默丢弃 Lobby 新增指标。
    /// </summary>
    [Fact]
    public async Task AdminRuntimeModels_MatchCanonicalSchemaPropertySets()
    {
        using var schema = await LoadSchemaAsync();

        Assert.Equal(
            GetSchemaPropertyNames(schema.RootElement),
            GetWirePropertyNames<RoomRuntimeTelemetry>());
        Assert.Equal(
            GetDefinitionPropertyNames(schema.RootElement, "playerRuntimeTelemetry"),
            GetWirePropertyNames<PlayerRuntimeTelemetry>());
        Assert.Equal(
            GetDefinitionPropertyNames(schema.RootElement, "rpcMethodTelemetry"),
            GetWirePropertyNames<RpcMethodTelemetry>());
        Assert.Equal(
            GetDefinitionPropertyNames(schema.RootElement, "settlementRuntimeTelemetry"),
            GetWirePropertyNames<SettlementRuntimeTelemetry>());
    }

    /// <summary>
    /// 验证兼容的 v1 样例可被 Admin 读取，且缺少新增可选字段时保留约定默认值。
    /// </summary>
    [Fact]
    public void CanonicalV1Snapshot_DeserializesWithCompatibleDefaults()
    {
        const string json = """
        {
          "roomId": "room-contract",
          "serverInstanceId": "instance-contract",
          "observedAtUtc": "2026-07-29T01:00:00Z",
          "gameStartedAtUtc": null,
          "lifecycle": "Waiting",
          "currentRound": 0,
          "connectedPlayers": 1,
          "serverTickMilliseconds": 16.67,
          "serverFramesPerSecond": 59.98,
          "rpcReceivedCount": 1234,
          "processMemoryBytes": 268435456,
          "processCpuPercent": 37.5,
          "networkIngressBytes": 9876,
          "networkEgressBytes": 5432,
          "buildVersion": "contract-build-1",
          "players": [
            {
              "playerId": "player-contract",
              "seatIndex": 0,
              "connectionState": "Connected",
              "latencyMilliseconds": 42.5,
              "disconnectedAtUtc": null,
              "trustee": false
            }
          ]
        }
        """;

        var snapshot = JsonSerializer.Deserialize<RoomRuntimeTelemetry>(
            json,
            WireJsonOptions);

        Assert.NotNull(snapshot);
        Assert.Equal("room-contract", snapshot.RoomId);
        Assert.Equal(1, snapshot.TelemetrySchemaVersion);
        Assert.Null(snapshot.ProcessCpuSampleWindowMilliseconds);
        Assert.Null(snapshot.NetworkIngressBytesPerSecond);
        Assert.Null(snapshot.NetworkEgressBytesPerSecond);
        Assert.Null(snapshot.RpcMethods);
        Assert.Null(snapshot.Settlement);
        Assert.Equal("player-contract", Assert.Single(snapshot.Players).PlayerId);
    }

    /// <summary>
    /// 从测试输出目录读取随项目复制的权威 Schema；缺失文件视为构建配置错误。
    /// </summary>
    private static async Task<JsonDocument> LoadSchemaAsync()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "runtime-telemetry-v1.schema.json");
        await using var stream = File.OpenRead(path);
        return await JsonDocument.ParseAsync(stream);
    }

    /// <summary>
    /// 返回根对象的排序属性名，排序消除 JSON 与反射声明顺序差异。
    /// </summary>
    private static string[] GetSchemaPropertyNames(JsonElement schema) =>
        schema.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// 返回指定嵌套定义的排序属性名；定义缺失会立即暴露契约文件损坏。
    /// </summary>
    private static string[] GetDefinitionPropertyNames(
        JsonElement schema,
        string definitionName) =>
        GetSchemaPropertyNames(
            schema.GetProperty("$defs").GetProperty(definitionName));

    /// <summary>
    /// 按生产 Web JSON 命名策略计算模型线协议属性名。
    /// </summary>
    private static string[] GetWirePropertyNames<T>() =>
        typeof(T)
            .GetProperties()
            .Select(property =>
                JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();
}
