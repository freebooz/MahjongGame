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
/// 承载权威结算提交、恢复与幂等校验；普通管理入口不能修改对局结果。
/// </summary>
public sealed partial class LobbyService
{
    /// <summary>
    /// 接收权威 Dedicated Server 结算；校验结果凭据、序列和摘要后幂等持久化。
    /// </summary>
    public async Task<MatchResultAck> SubmitMatchResultAsync(
        string requestId,
        string matchId,
        string resultCredential,
        MatchResultReport report,
        CancellationToken cancellationToken) => await SubmitMatchResultCoreAsync(
            requestId, matchId, resultCredential, report, trustedRecovery: false, cancellationToken);

    /// <summary>
    /// 执行受信恢复结算；仅供内部恢复链路绕过实例凭据，其他契约校验保持不变。
    /// </summary>
    public async Task<MatchResultAck> RecoverMatchResultAsync(
        string requestId,
        string matchId,
        MatchResultReport report,
        CancellationToken cancellationToken) => await SubmitMatchResultCoreAsync(
            requestId, matchId, string.Empty, report, trustedRecovery: true, cancellationToken);

    /// <summary>
    /// 统一提交和恢复结算事务，保证重复结果可确认、冲突结果被拒绝且回收可重试。
    /// </summary>
    private async Task<MatchResultAck> SubmitMatchResultCoreAsync(
        string requestId,
        string matchId,
        string resultCredential,
        MatchResultReport report,
        bool trustedRecovery,
        CancellationToken cancellationToken)
    {
        ValidateMatchResult(matchId, report);
        var room = await store.GetRoomByIdAsync(report.RoomId, cancellationToken)
            ?? throw new LobbyOperationException(
                LobbyErrorCode.RoomNotFound, "结算房间不存在", StatusCodes.Status404NotFound);
        var authoritativeInstanceId = room.LastServerInstanceId ?? room.Route?.ServerInstanceId;
        var scopeMatches = trustedRecovery
            ? authoritativeInstanceId == report.ServerInstanceId
            : room.Lifecycle == RoomLifecycle.Closed
                ? authoritativeInstanceId is null || authoritativeInstanceId == report.ServerInstanceId
                : room.Route?.ServerInstanceId == report.ServerInstanceId;
        if (room.MatchId != matchId
            || !scopeMatches
            || (!trustedRecovery && !VerifyCredential(resultCredential, room.ResultCredentialHash)))
        {
            throw new LobbyOperationException(
                LobbyErrorCode.SessionExpired,
                "GameServer 结算凭据或作用域无效",
                StatusCodes.Status401Unauthorized);
        }
        if (room.Lifecycle is not RoomLifecycle.Settling and not RoomLifecycle.Closed
            && !(trustedRecovery && room.Lifecycle == RoomLifecycle.Failed))
        {
            throw new LobbyOperationException(
                LobbyErrorCode.InvalidRequest,
                "房间尚未进入最终结算阶段",
                StatusCodes.Status409Conflict);
        }
        var expectedPlayers = room.PlayerIds.Order(StringComparer.Ordinal).ToArray();
        var reportedPlayers = report.Players.Select(player => player.PlayerId).Order(StringComparer.Ordinal).ToArray();
        if (report.CompletedRounds != room.RoundCount
            || !expectedPlayers.SequenceEqual(reportedPlayers, StringComparer.Ordinal)
            || !ShuffleFairnessVerifier.Verify(report, room))
        {
            // 公平性证明与结算结果处于同一事务门禁；证明无效时不得先保存结果再异步补审。
            throw Invalid("结算局数、玩家集合或洗牌公平性证明与权威房间不一致");
        }

        var closedRoom = room.Lifecycle == RoomLifecycle.Closed
            ? room
            : RoomStateMachine.Transition(room, RoomLifecycle.Closed, timeProvider) with
            {
                Route = null,
                PendingServerInstanceId = null,
                LastServerInstanceId = authoritativeInstanceId ?? report.ServerInstanceId
            };
        var finalizeStatus = await store.FinalizeMatchAsync(closedRoom, report, cancellationToken);
        if (finalizeStatus == FinalizeMatchStatus.Conflict)
        {
            throw new LobbyOperationException(
                LobbyErrorCode.InvalidRequest,
                "相同结算序号已提交不同结果",
                StatusCodes.Status409Conflict);
        }
        var duplicate = finalizeStatus == FinalizeMatchStatus.Duplicate;
        if (!duplicate)
        {
            logger.LogInformation(
                "牌局结算已持久化 RequestId={RequestId} RoomId={RoomId} MatchId={MatchId} ResultSequence={ResultSequence}",
                requestId, room.RoomId, matchId, report.ResultSequence);
            await events.PublishAsync(LobbyEventTypes.RoomClosed, ToDirectoryItem(closedRoom), cancellationToken);
        }
        // Lobby 持久化成功是结算完成的权威确认点；即使 Dedicated Server 随后立即回收，
        // Admin 仍能从监控存储看到 Completed，而不会永久停留在 Submitted。
        var runtime = await monitoringStore.GetRuntimeAsync(room.RoomId, cancellationToken);
        if (runtime is not null)
        {
            var resultPayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                report, TelemetryJsonOptions);
            var resultHash = Convert.ToHexString(SHA256.HashData(resultPayload)).ToLowerInvariant();
            var completedAtUtc = timeProvider.GetUtcNow();
            var completedSettlement = new SettlementRuntimeTelemetry(
                "Completed",
                matchId,
                report.ResultSequence,
                resultHash,
                runtime.Settlement?.SubmittedAtUtc,
                completedAtUtc,
                null);
            await monitoringStore.SetRuntimeAsync(
                runtime with { Settlement = completedSettlement },
                cancellationToken);
            if (runtime.Settlement?.Status != completedSettlement.Status)
            {
                await AppendRuntimeEventAsync(
                    room,
                    requestId,
                    "SettlementStatusChanged",
                    new Dictionary<string, object?>
                    {
                        ["from"] = runtime.Settlement?.Status,
                        ["to"] = completedSettlement.Status,
                        ["matchId"] = matchId,
                        ["resultSequence"] = report.ResultSequence,
                        ["resultHash"] = resultHash
                    },
                    completedAtUtc,
                    cancellationToken);
            }
        }
        if (allocator.Enabled && !trustedRecovery)
        {
            try
            {
                await allocator.DrainAsync(requestId, report.ServerInstanceId, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                throw new LobbyOperationException(
                    LobbyErrorCode.ServerUnavailable,
                    "结算已保存，GameServer 回收暂未完成，请重试确认",
                    StatusCodes.Status503ServiceUnavailable,
                    1000);
            }
        }
        return new MatchResultAck(requestId, matchId, report.ResultSequence, true, duplicate);
    }

    /// <summary>
    /// 校验结算作用域、玩家座位与排名唯一性；不允许格式错误结果进入幂等比较。
    /// </summary>
    private static void ValidateMatchResult(string matchId, MatchResultReport report)
    {
        if (!Guid.TryParse(matchId, out _)
            || !Guid.TryParse(report.RoomId, out _)
            || !Guid.TryParse(report.ServerInstanceId, out _)
            || report.ResultSequence < 1
            || report.CompletedRounds is < 1 or > 16
            || report.Players.Length is < 1 or > 4
            || report.Players.Select(player => player.PlayerId).Distinct(StringComparer.Ordinal).Count()
                != report.Players.Length
            || report.Players.Select(player => player.SeatIndex).Distinct().Count() != report.Players.Length
            || report.Players.Select(player => player.Rank).Distinct().Count() != report.Players.Length
            || report.Players.Any(player => string.IsNullOrWhiteSpace(player.PlayerId)
                || player.PlayerId.Length > 80
                || player.SeatIndex is < 0 or > 3
                || player.Rank is < 1 or > 4)
            || report.ShuffleProofs is null
            || report.ShuffleProofs.Length != report.CompletedRounds
            || string.IsNullOrWhiteSpace(report.EventChainDigest))
        {
            throw Invalid("结算结果格式无效");
        }
    }
}
