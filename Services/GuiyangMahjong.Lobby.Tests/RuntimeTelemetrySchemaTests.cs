using System.Text.Json;
using GuiyangMahjong.Lobby.Domain;

namespace GuiyangMahjong.Lobby.Tests;

/// <summary>
/// 验证 Lobby 生产模型遵守运行遥测 v1 JSON Schema。
/// 与 Admin 的消费者测试共同形成双向门禁，但两个测试项目不互相引用生产程序集。
/// </summary>
public sealed class RuntimeTelemetrySchemaTests
{
    /// <summary>
    /// 校验房间、玩家、RPC 与结算模型属性集合，防止生产者单方面修改线协议。
    /// </summary>
    [Fact]
    public async Task LobbyRuntimeModels_MatchCanonicalSchemaPropertySets()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "runtime-telemetry-v1.schema.json");
        await using var stream = File.OpenRead(path);
        using var schema = await JsonDocument.ParseAsync(stream);
        var root = schema.RootElement;

        Assert.Equal(
            GetSchemaPropertyNames(root),
            GetWirePropertyNames<RoomRuntimeTelemetry>());
        Assert.Equal(
            GetDefinitionPropertyNames(root, "playerRuntimeTelemetry"),
            GetWirePropertyNames<PlayerRuntimeTelemetry>());
        Assert.Equal(
            GetDefinitionPropertyNames(root, "rpcMethodTelemetry"),
            GetWirePropertyNames<RpcMethodTelemetry>());
        Assert.Equal(
            GetDefinitionPropertyNames(root, "settlementRuntimeTelemetry"),
            GetWirePropertyNames<SettlementRuntimeTelemetry>());
    }

    /// <summary>
    /// 返回 Schema 对象的排序属性名，确保比较只关注线协议集合而不依赖声明顺序。
    /// </summary>
    private static string[] GetSchemaPropertyNames(JsonElement schema) =>
        schema.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// 从 `$defs` 读取嵌套模型属性，缺失定义时测试直接失败。
    /// </summary>
    private static string[] GetDefinitionPropertyNames(
        JsonElement schema,
        string definitionName) =>
        GetSchemaPropertyNames(
            schema.GetProperty("$defs").GetProperty(definitionName));

    /// <summary>
    /// 使用生产 Web JSON camelCase 策略计算 C# 模型的线协议属性。
    /// </summary>
    private static string[] GetWirePropertyNames<T>() =>
        typeof(T)
            .GetProperties()
            .Select(property =>
                JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();
}
