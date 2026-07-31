using System.Security.Cryptography;
using System.Text;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Realtime;

namespace GuiyangMahjong.Lobby.Services;

/// <summary>阶段 7 GameData 权威核对和房间关闭回调；本文件不写结算、战绩、证据或资产表。</summary>
public sealed partial class LobbyService
{
    /// <summary>
    /// 核对 GameData 提交的凭据摘要和当前房间/实例/Epoch/版本绑定。
    /// 失败只返回稳定原因，不暴露存储的凭据摘要或房间敏感字段。
    /// </summary>
    public async Task<SettlementAuthorityResponse> ValidateSettlementAuthorityAsync(
        SettlementAuthorityRequest request,
        CancellationToken cancellationToken)
    {
        var room = await store.GetRoomByIdAsync(request.RoomId, cancellationToken);
        var authoritativeInstance = room?.Route?.ServerInstanceId ?? room?.LastServerInstanceId ?? string.Empty;
        var authorized = room is not null
            && room.MatchId == request.MatchId
            && authoritativeInstance == request.ServerInstanceId
            && room.RoomEpoch == request.RoomEpoch
            && room.RuleSetVersion == request.RuleSetVersion
            && room.BuildVersion == request.ServerBuild
            && room.Lifecycle is RoomLifecycle.Settling or RoomLifecycle.Closed
            && FixedTimeHashEquals(request.CredentialSha256, room.ResultCredentialHash);
        return new SettlementAuthorityResponse(
            authorized,
            request.MatchId,
            request.RoomId,
            request.ServerInstanceId,
            request.RoomEpoch,
            request.RuleSetVersion,
            request.ServerBuild,
            authorized ? room!.PlayerIds : [],
            authorized ? null : "SETTLEMENT_AUTHORITY_MISMATCH");
    }

    /// <summary>
    /// 在 GameData 已不可变提交后关闭房间并释放活动成员索引。
    /// 重复回调返回首次终态；本方法永远不写 public.match_results。
    /// </summary>
    public async Task<ExternalSettlementCommittedAck> MarkExternalSettlementCommittedAsync(
        string requestId,
        ExternalSettlementCommittedRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.MatchId, out _)
            || !Guid.TryParse(request.RoomId, out _)
            || !Guid.TryParse(request.SettlementId, out _)
            || request.RoundNo is < 1 or > 16
            || request.SettlementVersion < 1)
            throw Invalid("GameData 结算回调格式无效");
        var room = await store.GetRoomByIdAsync(request.RoomId, cancellationToken)
            ?? throw new LobbyOperationException(
                LobbyErrorCode.RoomNotFound, "结算房间不存在", StatusCodes.Status404NotFound);
        if (room.MatchId != request.MatchId)
            throw Invalid("GameData 结算回调与房间比赛不匹配");
        if (room.Lifecycle == RoomLifecycle.Closed)
            return new ExternalSettlementCommittedAck(
                request.MatchId, request.RoomId, request.SettlementId, true, true);
        if (room.Lifecycle != RoomLifecycle.Settling)
            throw new LobbyOperationException(
                LobbyErrorCode.InvalidRequest, "房间尚未进入结算阶段", StatusCodes.Status409Conflict);
        var closed = RoomStateMachine.Transition(room, RoomLifecycle.Closed, timeProvider) with
        {
            Route = null,
            PendingServerInstanceId = null,
            LastServerInstanceId = room.Route?.ServerInstanceId ?? room.LastServerInstanceId
        };
        if (!await store.UpdateRoomAsync(closed, cancellationToken))
            throw new LobbyOperationException(
                LobbyErrorCode.RequestInProgress, "房间状态已变化，请幂等重试回调", StatusCodes.Status409Conflict);
        await events.PublishAsync(LobbyEventTypes.RoomClosed, ToDirectoryItem(closed), cancellationToken);
        logger.LogInformation(
            "GameData 结算已关闭房间 RequestId={RequestId} RoomId={RoomId} MatchId={MatchId} SettlementId={SettlementId}",
            requestId, request.RoomId, request.MatchId, request.SettlementId);
        return new ExternalSettlementCommittedAck(
            request.MatchId, request.RoomId, request.SettlementId, true, false);
    }

    private static bool FixedTimeHashEquals(string suppliedHash, string? expectedHash)
    {
        if (suppliedHash.Length != 64 || expectedHash is not { Length: 64 }) return false;
        var supplied = Encoding.ASCII.GetBytes(suppliedHash.ToLowerInvariant());
        var expected = Encoding.ASCII.GetBytes(expectedHash.ToLowerInvariant());
        var valid = CryptographicOperations.FixedTimeEquals(supplied, expected);
        CryptographicOperations.ZeroMemory(supplied);
        CryptographicOperations.ZeroMemory(expected);
        return valid;
    }
}
