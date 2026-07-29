extern alias LobbyContract;

using System.Text.Json;
using AdminPlayerTelemetry = GuiyangMahjong.Admin.Domain.PlayerRuntimeTelemetry;
using AdminRuntimeTelemetry = GuiyangMahjong.Admin.Domain.RoomRuntimeTelemetry;
using LobbyPlayerTelemetry =
    LobbyContract::GuiyangMahjong.Lobby.Domain.PlayerRuntimeTelemetry;
using LobbyRuntimeTelemetry =
    LobbyContract::GuiyangMahjong.Lobby.Domain.RoomRuntimeTelemetry;
using LobbyRpcTelemetry =
    LobbyContract::GuiyangMahjong.Lobby.Domain.RpcMethodTelemetry;
using LobbySettlementTelemetry =
    LobbyContract::GuiyangMahjong.Lobby.Domain.SettlementRuntimeTelemetry;

namespace GuiyangMahjong.Admin.Tests;

/// <summary>
/// 验证 Lobby 的运行快照可以通过实际 JSON 线格式被 Admin 无损读取。
/// 该测试阻止两个服务在独立演进时发生字段改名、类型漂移或默认值分歧。
/// </summary>
public sealed class RuntimeTelemetryWireContractTests
{
    /// <summary>
    /// 模拟 ASP.NET Core 默认 Web JSON 格式，确保属性使用 camelCase 且大小写处理与生产一致。
    /// </summary>
    private static readonly JsonSerializerOptions WireJsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 验证 Lobby 完整 v1 快照序列化后，Admin 能够无损还原所有房间和玩家字段。
    /// </summary>
    [Fact]
    public void LobbyV1Snapshot_DeserializesInAdminWithoutLoss()
    {
        var observedAtUtc = DateTimeOffset.Parse("2026-07-29T01:00:00Z");
        var gameStartedAtUtc = observedAtUtc.AddMinutes(-2);
        var disconnectedAtUtc = observedAtUtc.AddSeconds(-20);
        var lobbySnapshot = new LobbyRuntimeTelemetry(
            "room-contract",
            "instance-contract",
            observedAtUtc,
            gameStartedAtUtc,
            "Playing",
            2,
            1,
            16.67,
            59.98,
            1_234,
            256L * 1024 * 1024,
            37.5,
            9_876,
            5_432,
            "contract-build-1",
            [
                new LobbyPlayerTelemetry(
                    "player-contract",
                    0,
                    "Reconnecting",
                    42.5,
                    disconnectedAtUtc,
                    true,
                    observedAtUtc.AddSeconds(-10),
                    disconnectedAtUtc,
                    null,
                    "NetworkInterrupted",
                    3,
                    Guid.NewGuid().ToString())
            ],
            1,
            250,
            300,
            400,
            [new LobbyRpcTelemetry("Server.RequestAction", 100, 2, 1, 0, 3.5, 8.2)],
            new LobbySettlementTelemetry(
                "Submitted",
                Guid.NewGuid().ToString(),
                9,
                new string('a', 64),
                observedAtUtc,
                null,
                null));

        var json = JsonSerializer.Serialize(lobbySnapshot, WireJsonOptions);
        var adminSnapshot = JsonSerializer.Deserialize<AdminRuntimeTelemetry>(
            json,
            WireJsonOptions);

        Assert.NotNull(adminSnapshot);
        Assert.Equal(lobbySnapshot.RoomId, adminSnapshot.RoomId);
        Assert.Equal(lobbySnapshot.ServerInstanceId, adminSnapshot.ServerInstanceId);
        Assert.Equal(lobbySnapshot.ObservedAtUtc, adminSnapshot.ObservedAtUtc);
        Assert.Equal(lobbySnapshot.GameStartedAtUtc, adminSnapshot.GameStartedAtUtc);
        Assert.Equal(lobbySnapshot.Lifecycle, adminSnapshot.Lifecycle);
        Assert.Equal(lobbySnapshot.CurrentRound, adminSnapshot.CurrentRound);
        Assert.Equal(lobbySnapshot.ConnectedPlayers, adminSnapshot.ConnectedPlayers);
        Assert.Equal(lobbySnapshot.ServerTickMilliseconds, adminSnapshot.ServerTickMilliseconds);
        Assert.Equal(lobbySnapshot.ServerFramesPerSecond, adminSnapshot.ServerFramesPerSecond);
        Assert.Equal(lobbySnapshot.RpcReceivedCount, adminSnapshot.RpcReceivedCount);
        Assert.Equal(lobbySnapshot.ProcessMemoryBytes, adminSnapshot.ProcessMemoryBytes);
        Assert.Equal(lobbySnapshot.ProcessCpuPercent, adminSnapshot.ProcessCpuPercent);
        Assert.Equal(lobbySnapshot.NetworkIngressBytes, adminSnapshot.NetworkIngressBytes);
        Assert.Equal(lobbySnapshot.NetworkEgressBytes, adminSnapshot.NetworkEgressBytes);
        Assert.Equal(lobbySnapshot.BuildVersion, adminSnapshot.BuildVersion);
        Assert.Equal(lobbySnapshot.TelemetrySchemaVersion, adminSnapshot.TelemetrySchemaVersion);
        Assert.Equal(
            lobbySnapshot.ProcessCpuSampleWindowMilliseconds,
            adminSnapshot.ProcessCpuSampleWindowMilliseconds);
        Assert.Equal(lobbySnapshot.NetworkIngressBytesPerSecond, adminSnapshot.NetworkIngressBytesPerSecond);
        Assert.Equal(lobbySnapshot.NetworkEgressBytesPerSecond, adminSnapshot.NetworkEgressBytesPerSecond);
        Assert.Equal(lobbySnapshot.RpcMethods![0].MethodName, adminSnapshot.RpcMethods![0].MethodName);
        Assert.Equal(lobbySnapshot.Settlement!.Status, adminSnapshot.Settlement!.Status);

        var adminPlayer = Assert.Single(adminSnapshot.Players);
        var lobbyPlayer = Assert.Single(lobbySnapshot.Players);
        Assert.Equal(lobbyPlayer.PlayerId, adminPlayer.PlayerId);
        Assert.Equal(lobbyPlayer.SeatIndex, adminPlayer.SeatIndex);
        Assert.Equal(lobbyPlayer.ConnectionState, adminPlayer.ConnectionState);
        Assert.Equal(lobbyPlayer.LatencyMilliseconds, adminPlayer.LatencyMilliseconds);
        Assert.Equal(lobbyPlayer.DisconnectedAtUtc, adminPlayer.DisconnectedAtUtc);
        Assert.Equal(lobbyPlayer.Trustee, adminPlayer.Trustee);
        Assert.Equal(lobbyPlayer.TrusteeChangedAtUtc, adminPlayer.TrusteeChangedAtUtc);
        Assert.Equal(lobbyPlayer.ConnectionChangedAtUtc, adminPlayer.ConnectionChangedAtUtc);
        Assert.Equal(lobbyPlayer.DisconnectReason, adminPlayer.DisconnectReason);
        Assert.Equal(lobbyPlayer.ConnectionStateSequence, adminPlayer.ConnectionStateSequence);
        Assert.Equal(lobbyPlayer.ConnectionEventId, adminPlayer.ConnectionEventId);
    }

    /// <summary>
    /// 验证旧 v1 快照缺少版本和可选指标时，Lobby 与 Admin 都保留相同默认值和 null 语义。
    /// </summary>
    [Fact]
    public void LegacyV1Snapshot_MissingOptionalFieldsHasSameDefaultsInLobbyAndAdmin()
    {
        const string json = """
        {
          "roomId": "room-legacy",
          "serverInstanceId": "instance-legacy",
          "observedAtUtc": "2026-07-29T01:00:00Z",
          "gameStartedAtUtc": null,
          "lifecycle": "Waiting",
          "currentRound": 0,
          "connectedPlayers": 0,
          "serverTickMilliseconds": null,
          "serverFramesPerSecond": null,
          "rpcReceivedCount": null,
          "processMemoryBytes": null,
          "processCpuPercent": null,
          "networkIngressBytes": null,
          "networkEgressBytes": null,
          "buildVersion": "legacy-build",
          "players": []
        }
        """;

        var lobbySnapshot = JsonSerializer.Deserialize<LobbyRuntimeTelemetry>(
            json,
            WireJsonOptions);
        var adminSnapshot = JsonSerializer.Deserialize<AdminRuntimeTelemetry>(
            json,
            WireJsonOptions);

        Assert.NotNull(lobbySnapshot);
        Assert.NotNull(adminSnapshot);
        Assert.Equal(1, lobbySnapshot.TelemetrySchemaVersion);
        Assert.Equal(1, adminSnapshot.TelemetrySchemaVersion);
        Assert.Null(lobbySnapshot.ProcessCpuPercent);
        Assert.Null(adminSnapshot.ProcessCpuPercent);
        Assert.Null(lobbySnapshot.NetworkIngressBytes);
        Assert.Null(adminSnapshot.NetworkIngressBytes);
        Assert.Empty(lobbySnapshot.Players);
        Assert.Empty(adminSnapshot.Players);
    }

    /// <summary>
    /// 验证 Lobby 与 Admin 运行快照公开属性集合一致，
    /// 防止新增字段只更新一个服务而导致静默丢失。
    /// </summary>
    [Fact]
    public void LobbyAndAdminRuntimeModels_ExposeTheSameWirePropertyNames()
    {
        var lobbyProperties = typeof(LobbyRuntimeTelemetry)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var adminProperties = typeof(AdminRuntimeTelemetry)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var lobbyPlayerProperties = typeof(LobbyPlayerTelemetry)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var adminPlayerProperties = typeof(AdminPlayerTelemetry)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(lobbyProperties, adminProperties);
        Assert.Equal(lobbyPlayerProperties, adminPlayerProperties);
    }

    /// <summary>
    /// 验证 OpenAPI 同步列出 v1 心跳字段，使实现、线协议和人工数据字典保持三方一致。
    /// </summary>
    [Fact]
    public async Task LobbyOpenApi_DocumentsEveryV1HeartbeatField()
    {
        var contractPath = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "lobby-v1.openapi.yaml");
        var openApi = await File.ReadAllTextAsync(contractPath);
        string[] expectedFields =
        [
            "telemetrySchemaVersion",
            "roomId",
            "heartbeatCredential",
            "connectedPlayers",
            "connectedPlayerIds",
            "roomLifecycle",
            "roundId",
            "buildVersion",
            "sentAtUtc",
            "gameStartedAtUtc",
            "serverTickMilliseconds",
            "serverFramesPerSecond",
            "rpcReceivedCount",
            "processMemoryBytes",
            "processCpuPercent",
            "processCpuSampleWindowMilliseconds",
            "networkIngressBytes",
            "networkEgressBytes",
            "rpcMethods",
            "settlement",
            "players",
            "playerId",
            "seatIndex",
            "connectionState",
            "latencyMilliseconds",
            "disconnectedAtUtc",
            "trustee",
            "trusteeChangedAtUtc",
            "connectionChangedAtUtc",
            "reconnectedAtUtc",
            "disconnectReason",
            "connectionStateSequence",
            "connectionEventId",
            "methodName",
            "receivedCount",
            "rejectedCount",
            "failedCount",
            "timeoutCount",
            "p95DurationMilliseconds",
            "p99DurationMilliseconds",
            "status",
            "resultSequence",
            "resultHash",
            "submittedAtUtc",
            "confirmedAtUtc",
            "failureReason"
        ];

        foreach (var field in expectedFields)
        {
            Assert.Contains($"{field}:", openApi, StringComparison.Ordinal);
        }
    }
}
