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
/// 承载 Dedicated Server 注册、心跳与失败通知，负责实例凭据和房间租约边界。
/// </summary>
public sealed partial class LobbyService
{
    /// <summary>
    /// 校验实例身份并绑定房间路由；注册成功后只返回本实例所需的启动快照与结果凭据。
    /// </summary>
    public async Task<GameServerRegistrationAck> RegisterGameServerAsync(
        string requestId,
        GameServerRegistration registration,
        CancellationToken cancellationToken)
    {
        // 注册跨度把 Lobby 房间绑定与 Allocator 确认串在同一技术 Trace 中。
        using var activity = MahjongTelemetry.ActivitySource.StartActivity(
            "Lobby.RegisterGameServer",
            ActivityKind.Internal);
        activity?.SetTag("mahjong.room_id", registration.RoomId);
        activity?.SetTag(
            "mahjong.server_instance_id",
            registration.ServerInstanceId);
        activity?.SetTag("mahjong.match_id", registration.MatchId);
        if (!allocator.Enabled)
        {
            throw new LobbyOperationException(
                LobbyErrorCode.BackendNotConfigured,
                "Allocator integration is disabled.",
                StatusCodes.Status503ServiceUnavailable);
        }
        var room = await WaitForPendingAllocationAsync(
                registration.RoomId,
                registration.ServerInstanceId,
                cancellationToken)
            ?? throw new LobbyOperationException(
                LobbyErrorCode.RoomNotFound, "Room was not found.", StatusCodes.Status404NotFound);
        if (room.MatchId != registration.MatchId
            || room.Lifecycle != RoomLifecycle.Allocating
            || room.PendingServerInstanceId != registration.ServerInstanceId)
        {
            throw Invalid("GameServer registration does not match the room allocation.");
        }

        var acknowledgement = await allocator.ConfirmRegistrationAsync(
            requestId, registration, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var resultCredential = CreateResultCredential();
        room = RoomStateMachine.Transition(room, RoomLifecycle.Waiting, timeProvider) with
        {
            PendingServerInstanceId = null,
            Route = new GameServerRoute(
                requestId,
                string.Empty,
                room.RoomId,
                registration.ServerInstanceId,
                room.MatchId,
                registration.ListenIp,
                registration.ListenPort,
                string.Empty,
                now),
            LastServerInstanceId = registration.ServerInstanceId,
            ResultCredentialHash = HashCredential(resultCredential)
        };
        if (!await store.UpdateRoomAsync(room, cancellationToken))
        {
            throw new LobbyOperationException(
                LobbyErrorCode.InternalError,
                "Room route could not be persisted.",
                StatusCodes.Status500InternalServerError);
        }
        await events.PublishAsync(LobbyEventTypes.ServerAssigned, ToDirectoryItem(room), cancellationToken);
        await events.PublishAsync(LobbyEventTypes.RoomUpdated, ToDirectoryItem(room), cancellationToken);
        return new GameServerRegistrationAck(
            requestId,
            acknowledgement.Accepted,
            acknowledgement.HeartbeatIntervalSeconds,
            acknowledgement.HeartbeatCredential,
            resultCredential,
            new ManagedRoomBootstrap(
                room.RoomId,
                room.RoomCode,
                room.MatchId,
                room.OwnerPlayerId,
                room.RoundCount,
                room.MaximumPlayers,
                room.PublicRoom,
                room.AutoStart,
                room.Password is not null,
                CloneRuleSnapshot(room.RuleSnapshot)));
    }

    /// <summary>
    /// 接收 Dedicated Server 心跳，验证房间与实例归属后原子更新生命周期和运行遥测。
    /// </summary>
    public async Task RecordGameServerHeartbeatAsync(
        string requestId,
        string serverInstanceId,
        GameServerHeartbeat heartbeat,
        CancellationToken cancellationToken)
    {
        // 每次心跳跨度携带业务标识用于指标 exemplar 跳转；这些高基数字段不会成为指标标签。
        using var activity = MahjongTelemetry.ActivitySource.StartActivity(
            "Lobby.RecordGameServerHeartbeat",
            ActivityKind.Internal);
        activity?.SetTag("mahjong.room_id", heartbeat.RoomId);
        activity?.SetTag("mahjong.server_instance_id", serverInstanceId);
        activity?.SetTag("mahjong.request_id", requestId);
        // 主版本决定指标单位和空值语义；未知版本必须在触达下游前失败关闭，
        // 避免 Allocator 已接受心跳而 Lobby/Admin 使用错误口径展示数据。
        if (heartbeat.TelemetrySchemaVersion != 1)
        {
            throw Invalid("GameServer heartbeat telemetry schema version is unsupported.");
        }
        await allocator.RecordHeartbeatAsync(requestId, serverInstanceId, heartbeat, cancellationToken);
        var room = await store.GetRoomByIdAsync(heartbeat.RoomId, cancellationToken);
        if (room is null || room.Route?.ServerInstanceId != serverInstanceId) return;

        var now = timeProvider.GetUtcNow();
        if (heartbeat.ConnectedPlayerIds is { } connectedPlayerIds)
        {
            var distinctPlayerIds = connectedPlayerIds
                .Where(playerId => !string.IsNullOrWhiteSpace(playerId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (distinctPlayerIds.Length != connectedPlayerIds.Length
                || distinctPlayerIds.Length != heartbeat.ConnectedPlayers
                || distinctPlayerIds.Length > room.MaximumPlayers
                || distinctPlayerIds.Any(playerId => playerId.Length > 80))
            {
                throw Invalid("GameServer heartbeat player membership is invalid.");
            }
            await store.RefreshConnectedPlayersAsync(
                room.RoomId, distinctPlayerIds, now, cancellationToken);
        }

        await RecordRuntimeTelemetryAsync(
            requestId, serverInstanceId, room, heartbeat, now, cancellationToken);

        var reported = heartbeat.RoomLifecycle switch
        {
            "Playing" => RoomLifecycle.Playing,
            "Settling" => RoomLifecycle.Settling,
            _ => room.Lifecycle
        };
        var updated = room;
        var changed = false;
        if (reported != room.Lifecycle && RoomStateMachine.CanTransition(room.Lifecycle, reported))
        {
            updated = RoomStateMachine.Transition(room, reported, timeProvider);
            changed = true;
        }

        var shouldDrainEmptyRoom = false;
        if (updated.Lifecycle is RoomLifecycle.Waiting or RoomLifecycle.Playing or RoomLifecycle.Settling)
        {
            if (heartbeat.ConnectedPlayers == 0)
            {
                var emptySinceUtc = updated.EmptySinceUtc ?? now;
                if (!changed
                    && now - emptySinceUtc >= TimeSpan.FromSeconds(options.EmptyRoomTimeoutSeconds))
                {
                    var terminalLifecycle = updated.Lifecycle == RoomLifecycle.Playing
                        ? RoomLifecycle.Failed
                        : RoomLifecycle.Closed;
                    updated = RoomStateMachine.Transition(updated, terminalLifecycle, timeProvider) with
                    {
                        Route = null,
                        PendingServerInstanceId = null,
                        LastServerInstanceId = serverInstanceId,
                        EmptySinceUtc = emptySinceUtc
                    };
                    changed = true;
                    shouldDrainEmptyRoom = true;
                }
                else if (updated.EmptySinceUtc is null)
                {
                    updated = updated with
                    {
                        EmptySinceUtc = emptySinceUtc,
                        StateSequence = changed ? updated.StateSequence : updated.StateSequence + 1,
                        UpdatedAtUtc = now
                    };
                    changed = true;
                }
            }
            else if (updated.EmptySinceUtc is not null)
            {
                updated = updated with
                {
                    EmptySinceUtc = null,
                    StateSequence = changed ? updated.StateSequence : updated.StateSequence + 1,
                    UpdatedAtUtc = now
                };
                changed = true;
            }
        }

        if (!changed || !await store.UpdateRoomAsync(updated, cancellationToken)) return;
        await events.PublishAsync(
            shouldDrainEmptyRoom ? LobbyEventTypes.RoomClosed : LobbyEventTypes.RoomUpdated,
            ToDirectoryItem(updated),
            cancellationToken);
        if (!shouldDrainEmptyRoom) return;

        logger.LogInformation(
            "Empty room timed out and is being reclaimed RequestId={RequestId} RoomId={RoomId} InstanceId={InstanceId}",
            requestId,
            updated.RoomId,
            serverInstanceId);
        try
        {
            await allocator.DrainAsync(requestId, serverInstanceId, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(
                exception,
                "Empty room closed but Dedicated Server drain failed RoomId={RoomId} InstanceId={InstanceId}",
                updated.RoomId,
                serverInstanceId);
        }
    }

    /// <summary>
    /// 将匹配实例的活动房间标记失败；过期或不相干的失败通知保持幂等忽略。
    /// </summary>
    public async Task MarkGameServerFailedAsync(
        GameServerFailure failure,
        CancellationToken cancellationToken)
    {
        var room = await store.GetRoomByIdAsync(failure.RoomId, cancellationToken);
        if (room is null
            || (room.Route?.ServerInstanceId != failure.ServerInstanceId
                && room.PendingServerInstanceId != failure.ServerInstanceId)
            || room.Lifecycle is RoomLifecycle.Closed or RoomLifecycle.Failed)
        {
            return;
        }

        room = RoomStateMachine.Transition(room, RoomLifecycle.Failed, timeProvider) with
        {
            Route = null,
            PendingServerInstanceId = null
        };
        await store.UpdateRoomAsync(room, cancellationToken);
        logger.LogWarning(
            "GameServer failure closed room RoomId={RoomId} InstanceId={InstanceId} Reason={Reason}",
            failure.RoomId,
            failure.ServerInstanceId,
            failure.Reason);
        await events.PublishAsync(LobbyEventTypes.RoomClosed, ToDirectoryItem(room), cancellationToken);
    }

    /// <summary>
    /// 生成仅返回给权威实例的高熵结算凭据；存储层只保存其摘要。
    /// </summary>
    private static string CreateResultCredential() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// 计算结算凭据 SHA-256 摘要，用于持久化比较而不保存明文。
    /// </summary>
    private static string HashCredential(string credential) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));

    /// <summary>
    /// 以固定时间比较结算凭据摘要，并在比较后清理临时字节数组。
    /// </summary>
    private static bool VerifyCredential(string supplied, string? expectedHash)
    {
        if (string.IsNullOrWhiteSpace(supplied) || supplied.Length > 256 || string.IsNullOrEmpty(expectedHash))
            return false;
        var suppliedHash = Encoding.ASCII.GetBytes(HashCredential(supplied.Trim()));
        var expected = Encoding.ASCII.GetBytes(expectedHash);
        var valid = suppliedHash.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(suppliedHash, expected);
        CryptographicOperations.ZeroMemory(suppliedHash);
        return valid;
    }
}
