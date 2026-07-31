using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Rooms;

namespace GuiyangMahjong.Lobby.Administration;

/// <summary>
/// 管理命令读取边界，只允许读取房间控制面状态。
/// 管理模块不得借此修改结算、玩家资产或 Dedicated Server 内的实时牌局结果。
/// </summary>
public interface IRoomAdministrationReader : IRoomReader
{
}

/// <summary>
/// 经过审批后的房间控制命令。
/// ExpectedStateVersion 防止运营页面基于陈旧快照覆盖新的 DS 或房间状态。
/// </summary>
public sealed record RoomAdministrationCommand(
    string RoomId,
    string ActionType,
    long ExpectedStateVersion,
    string OperatorId,
    string Reason,
    string TraceId,
    string ApprovalId);

/// <summary>管理命令执行后的只读审计投影，不包含可编辑比赛结果。</summary>
public sealed record RoomAdministrationResult(
    string RoomId,
    RoomLifecycle Before,
    RoomLifecycle After,
    long StateVersion,
    long RoomEpoch,
    bool Duplicate);
