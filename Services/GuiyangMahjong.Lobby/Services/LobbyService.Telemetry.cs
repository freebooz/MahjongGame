using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Realtime;
using GuiyangMahjong.Lobby.Security;
using GuiyangMahjong.Lobby.Storage;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Services;

/// <summary>
/// 承载运行快照校验、派生网络速率与房间事件时间线写入；异常快照不得部分落库。
/// </summary>
public sealed partial class LobbyService
{
    /// <summary>
    /// 校验并保存一次 Dedicated Server 运行快照，计算派生网络速率，
    /// 并把连接、托管、生命周期和结算的真实变化追加到房间时间线。
    /// 任何字段越界都会失败关闭，且不会写入部分运行快照。
    /// </summary>
    private async Task RecordRuntimeTelemetryAsync(
        string requestId,
        string serverInstanceId,
        LobbyRoom room,
        GameServerHeartbeat heartbeat,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateRuntimeMetric(heartbeat.ServerTickMilliseconds, 0, 10_000, "server tick");
        ValidateRuntimeMetric(heartbeat.ServerFramesPerSecond, 0, 1_000, "server FPS");
        // v1 将进程 CPU 定义为按节点总容量归一化的百分比，超过 100
        // 通常表示生产者使用了“单核可超过 100%”的另一种口径，必须拒绝而非混入看板。
        ValidateRuntimeMetric(heartbeat.ProcessCpuPercent, 0, 100, "process CPU");
        ValidateRuntimeMetric(
            heartbeat.ProcessCpuSampleWindowMilliseconds, 250, 60_000, "process CPU sample window");
        if (heartbeat.RpcReceivedCount is < 0
            || heartbeat.ProcessMemoryBytes is < 0
            || heartbeat.NetworkIngressBytes is < 0
            || heartbeat.NetworkEgressBytes is < 0)
        {
            throw Invalid("GameServer heartbeat cumulative metric is invalid.");
        }
        ValidateRpcTelemetry(heartbeat.RpcMethods);
        ValidateSettlementTelemetry(room, heartbeat.Settlement);
        if (heartbeat.ActionSequence is < 0
            || heartbeat.StateVersion is < 0
            || heartbeat.SnapshotVersion is < 0
            || heartbeat.SnapshotCreatedAtUtc > observedAtUtc.AddMinutes(1)
            || heartbeat.RecoveryState is not null
                and not ("Healthy" or "Recovering" or "Recovered" or "Failed")
            || heartbeat.LastTraceId is { Length: > 128 })
        {
            // 恢复与快照字段参与事故判断，宁可拒绝异常口径也不能在看板展示虚假健康状态。
            throw Invalid("GameServer heartbeat recovery telemetry is invalid.");
        }

        var players = heartbeat.Players
            ?? (heartbeat.ConnectedPlayerIds ?? [])
                .Select(playerId => new PlayerRuntimeTelemetry(
                    playerId, -1, "Connected", null, null, null))
                .ToArray();
        var distinctPlayers = players
            .Select(player => player.PlayerId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctPlayers.Length != players.Length
            || players.Length > room.MaximumPlayers
            || players.Any(player =>
                string.IsNullOrWhiteSpace(player.PlayerId)
                || player.PlayerId.Length > 80
                || player.SeatIndex is < -1 or > 3
                || player.LatencyMilliseconds is < 0 or > 120_000
                || player.PacketLossPercent is < 0 or > 100
                || !IsFinite(player.PacketLossPercent)
                || player.IllegalActionCount is < 0
                || player.ReconnectCount is < 0
                || !IsFinite(player.LatencyMilliseconds)
                || player.ConnectionState is not ("Connected" or "Disconnected" or "Reconnecting")
                || player.ConnectionStateSequence is < 0
                || (player.ConnectionEventId is { Length: > 0 }
                    && !Guid.TryParse(player.ConnectionEventId, out _))
                || player.DisconnectReason is not null
                    and not ("NormalExit" or "NetworkInterrupted" or "ReconnectTimeout"
                        or "Kicked" or "ServerShutdown")))
        {
            throw Invalid("GameServer heartbeat player telemetry is invalid.");
        }

        var previous = await monitoringStore.GetRuntimeAsync(room.RoomId, cancellationToken);
        var (ingressRate, egressRate) = CalculateNetworkRates(
            previous, serverInstanceId, heartbeat, observedAtUtc);
        var runtime = new RoomRuntimeTelemetry(
            room.RoomId,
            serverInstanceId,
            observedAtUtc,
            heartbeat.GameStartedAtUtc ?? previous?.GameStartedAtUtc,
            heartbeat.RoomLifecycle,
            heartbeat.RoundId,
            heartbeat.ConnectedPlayers,
            heartbeat.ServerTickMilliseconds,
            heartbeat.ServerFramesPerSecond,
            heartbeat.RpcReceivedCount,
            heartbeat.ProcessMemoryBytes,
            heartbeat.ProcessCpuPercent,
            heartbeat.NetworkIngressBytes,
            heartbeat.NetworkEgressBytes,
            heartbeat.BuildVersion,
            players,
            heartbeat.TelemetrySchemaVersion,
            heartbeat.ProcessCpuSampleWindowMilliseconds,
            ingressRate,
            egressRate,
            heartbeat.RpcMethods,
            heartbeat.Settlement ?? previous?.Settlement,
            heartbeat.ActionSequence,
            heartbeat.StateVersion,
            room.RoomEpoch,
            heartbeat.SnapshotVersion,
            heartbeat.SnapshotCreatedAtUtc,
            heartbeat.RecoveryState,
            heartbeat.LastTraceId);
        var rpcDelta = previous is not null
            && previous.ServerInstanceId == serverInstanceId
            && heartbeat.RpcReceivedCount is { } currentRpc
            && previous.RpcReceivedCount is { } previousRpc
            && currentRpc >= previousRpc
                ? currentRpc - previousRpc
                : 0;
        var previousPlayersById = previous?.Players.ToDictionary(
            player => player.PlayerId,
            StringComparer.Ordinal);
        var disconnectDelta = previousPlayersById is null
            ? 0
            : runtime.Players.Count(player =>
                player.ConnectionState == "Disconnected"
                && previousPlayersById.TryGetValue(
                    player.PlayerId,
                    out var previousPlayer)
                && previousPlayer.ConnectionState != "Disconnected");
        MahjongTelemetry.RecordRoomHeartbeat(
            serverInstanceId,
            runtime.Lifecycle,
            runtime.BuildVersion,
            runtime.ConnectedPlayers,
            runtime.ServerTickMilliseconds,
            runtime.ServerFramesPerSecond,
            runtime.ProcessCpuPercent,
            runtime.ProcessMemoryBytes,
            runtime.NetworkIngressBytesPerSecond,
            runtime.NetworkEgressBytesPerSecond,
            rpcDelta,
            disconnectDelta);
        MahjongTelemetry.RecordTelemetryFreshness(
            observedAtUtc,
            timeProvider.GetUtcNow());
        await monitoringStore.SetRuntimeAsync(runtime, cancellationToken);

        if (previous is null)
        {
            await AppendRuntimeEventAsync(
                room,
                requestId,
                "ServerTelemetryStarted",
                new Dictionary<string, object?>
                {
                    ["serverInstanceId"] = serverInstanceId,
                    ["buildVersion"] = heartbeat.BuildVersion
                },
                observedAtUtc,
                cancellationToken);
            return;
        }

        if (!previous.Lifecycle.Equals(runtime.Lifecycle, StringComparison.Ordinal))
        {
            await AppendRuntimeEventAsync(
                room,
                requestId,
                "RoomLifecycleChanged",
                new Dictionary<string, object?>
                {
                    ["from"] = previous.Lifecycle,
                    ["to"] = runtime.Lifecycle,
                    ["roundId"] = runtime.CurrentRound
                },
                observedAtUtc,
                cancellationToken);
        }

        var previousPlayers = previous.Players.ToDictionary(
            player => player.PlayerId, StringComparer.Ordinal);
        foreach (var player in runtime.Players)
        {
            if (!previousPlayers.TryGetValue(player.PlayerId, out var oldPlayer))
            {
                continue;
            }
            // 新生产者使用单调序号和 EventId 提供幂等键；旧生产者仍退化为状态比较。
            var duplicateConnectionEvent = player.ConnectionStateSequence.HasValue
                && player.ConnectionStateSequence == oldPlayer.ConnectionStateSequence
                && player.ConnectionEventId == oldPlayer.ConnectionEventId;
            if (!duplicateConnectionEvent
                && !oldPlayer.ConnectionState.Equals(player.ConnectionState, StringComparison.Ordinal))
            {
                await AppendRuntimeEventAsync(
                    room,
                    requestId,
                    "PlayerConnectionChanged",
                    new Dictionary<string, object?>
                    {
                        ["playerId"] = player.PlayerId,
                        ["from"] = oldPlayer.ConnectionState,
                        ["to"] = player.ConnectionState,
                        ["reason"] = player.DisconnectReason,
                        ["latencyMilliseconds"] = player.LatencyMilliseconds,
                        ["connectionStateSequence"] = player.ConnectionStateSequence
                    },
                    player.ConnectionChangedAtUtc ?? observedAtUtc,
                    cancellationToken,
                    player.ConnectionEventId);
            }
            if (oldPlayer.Trustee != player.Trustee)
            {
                await AppendRuntimeEventAsync(
                    room,
                    requestId,
                    "PlayerTrusteeChanged",
                    new Dictionary<string, object?>
                    {
                        ["playerId"] = player.PlayerId,
                        ["from"] = oldPlayer.Trustee,
                        ["to"] = player.Trustee
                    },
                    player.TrusteeChangedAtUtc ?? observedAtUtc,
                    cancellationToken);
            }
        }

        if (runtime.Settlement is not null
            && previous.Settlement?.Status != runtime.Settlement.Status)
        {
            await AppendRuntimeEventAsync(
                room,
                requestId,
                "SettlementStatusChanged",
                new Dictionary<string, object?>
                {
                    ["from"] = previous.Settlement?.Status,
                    ["to"] = runtime.Settlement.Status,
                    ["matchId"] = runtime.Settlement.MatchId,
                    ["resultSequence"] = runtime.Settlement.ResultSequence,
                    ["resultHash"] = runtime.Settlement.ResultHash
                },
                observedAtUtc,
                cancellationToken);
        }
    }

    /// <summary>
    /// 追加房间运行事件；调用方提供 TraceId 和状态序列，供调查时间线稳定回放。
    /// </summary>
    private Task AppendRuntimeEventAsync(
        LobbyRoom room,
        string traceId,
        string eventType,
        Dictionary<string, object?> data,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken,
        string? eventId = null) =>
        monitoringStore.AppendEventAsync(
            room.RoomId,
            new RoomTimelineEvent(
                eventId ?? Guid.NewGuid().ToString(),
                eventType,
                occurredAtUtc,
                room.StateSequence,
                traceId,
                data),
            cancellationToken);

    /// <summary>
    /// 仅在同一实例、计数器单调且时间前进时计算网络速率；进程重启、驱动重建或计数器回退时返回 null，
    /// 从而避免把重置误算成负速率或尖峰。
    /// </summary>
    private static (double? Ingress, double? Egress) CalculateNetworkRates(
        RoomRuntimeTelemetry? previous,
        string serverInstanceId,
        GameServerHeartbeat current,
        DateTimeOffset observedAtUtc)
    {
        if (previous is null
            || previous.ServerInstanceId != serverInstanceId
            || current.NetworkIngressBytes is null
            || current.NetworkEgressBytes is null
            || previous.NetworkIngressBytes is null
            || previous.NetworkEgressBytes is null
            || current.NetworkIngressBytes < previous.NetworkIngressBytes
            || current.NetworkEgressBytes < previous.NetworkEgressBytes)
        {
            return (null, null);
        }
        var elapsedSeconds = (observedAtUtc - previous.ObservedAtUtc).TotalSeconds;
        if (elapsedSeconds <= 0) return (null, null);
        return (
            (current.NetworkIngressBytes.Value - previous.NetworkIngressBytes.Value) / elapsedSeconds,
            (current.NetworkEgressBytes.Value - previous.NetworkEgressBytes.Value) / elapsedSeconds);
    }

    /// <summary>
    /// 校验 RPC 指标的固定白名单形态与累计量，禁止动态高基数方法名进入监控存储。
    /// </summary>
    private static void ValidateRpcTelemetry(RpcMethodTelemetry[]? methods)
    {
        if (methods is null) return;
        var names = methods.Select(metric => metric.MethodName).ToArray();
        if (methods.Length > 32
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length
            || methods.Any(metric =>
                string.IsNullOrWhiteSpace(metric.MethodName)
                || metric.MethodName.Length > 80
                || !metric.MethodName.StartsWith("Server.", StringComparison.Ordinal)
                || metric.ReceivedCount < 0
                || metric.RejectedCount is < 0
                || metric.FailedCount is < 0
                || metric.TimeoutCount is < 0
                || metric.RejectedCount > metric.ReceivedCount
                || metric.FailedCount > metric.ReceivedCount
                || metric.TimeoutCount > metric.ReceivedCount
                || !IsFinite(metric.P95DurationMilliseconds)
                || !IsFinite(metric.P99DurationMilliseconds)
                || metric.P95DurationMilliseconds is < 0 or > 60_000
                || metric.P99DurationMilliseconds is < 0 or > 60_000
                || metric.P95DurationMilliseconds > metric.P99DurationMilliseconds))
        {
            throw Invalid("GameServer heartbeat RPC telemetry is invalid.");
        }
    }

    /// <summary>
    /// 校验结算投影与权威房间作用域一致；投影只读且不能携带可编辑的玩家结果。
    /// </summary>
    private static void ValidateSettlementTelemetry(
        LobbyRoom room, SettlementRuntimeTelemetry? settlement)
    {
        if (settlement is null) return;
        if (settlement.MatchId != room.MatchId
            || settlement.Status is not ("Calculating" or "Submitted" or "Accepted"
                or "Failed" or "Compensating" or "Completed")
            || settlement.ResultSequence is < 1
            || (settlement.Status != "Calculating"
                && (settlement.ResultSequence is null || !IsSha256(settlement.ResultHash)))
            || (settlement.ResultHash is not null && !IsSha256(settlement.ResultHash))
            || settlement.FailureReason is { Length: > 256 })
        {
            throw Invalid("GameServer heartbeat settlement telemetry is invalid.");
        }
    }

    /// <summary>只接受固定 64 位十六进制 SHA-256，避免把任意文本冒充结果摘要。</summary>
    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64) return false;
        return value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F');
    }

    /// <summary>
    /// 校验可选运行指标的有限数值和业务范围；异常心跳在写入前整体拒绝。
    /// </summary>
    private static void ValidateRuntimeMetric(
        double? value, double minimum, double maximum, string name)
    {
        if (value.HasValue
            && (!double.IsFinite(value.Value) || value < minimum || value > maximum))
        {
            throw Invalid($"GameServer heartbeat {name} metric is invalid.");
        }
    }

    /// <summary>
    /// 判断可选指标是否为有限数；空值代表数据源未提供而不是零值。
    /// </summary>
    private static bool IsFinite(double? value) =>
        !value.HasValue || double.IsFinite(value.Value);
}
