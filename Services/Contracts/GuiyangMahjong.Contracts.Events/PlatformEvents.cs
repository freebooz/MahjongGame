using System.Text.Json.Serialization;
using GuiyangMahjong.Contracts.Common;

namespace GuiyangMahjong.Contracts.Events;

/// <summary>第一批平台事件名称；名称一经发布不得重用于不同语义。</summary>
public static class PlatformEventTypes
{
    public const string IdentityAuthenticated = "identity.authenticated";
    public const string SessionCreated = "session.created";
    public const string SessionRevoked = "session.revoked";
    public const string RoomCreated = "room.created";
    public const string RoomMemberJoined = "room.member_joined";
    public const string RoomStateChanged = "room.state_changed";
    public const string AllocationRequested = "allocation.requested";
    public const string GameServerAllocated = "game_server.allocated";
    public const string GameServerReady = "game_server.ready";
    public const string PlayerConnected = "player.connected";
    public const string PlayerDisconnected = "player.disconnected";
    public const string MatchStarted = "match.started";
    public const string MatchFinished = "match.finished";
    public const string SettlementCommitted = "settlement.committed";
    public const string RoomTerminated = "room.terminated";
}

/// <summary>身份凭据验证成功事实；不包含 Token、设备原始指纹或 IP。</summary>
public sealed record IdentityAuthenticated(
    [property: JsonPropertyName("player_id")] PlayerId PlayerId,
    [property: JsonPropertyName("account_id")] AccountId AccountId,
    [property: JsonPropertyName("authenticated_at")] DateTimeOffset AuthenticatedAt)
    : IVersionedEventPayload
{
    public static string EventType => PlatformEventTypes.IdentityAuthenticated;
    public static int SchemaVersion => 1;
}

/// <summary>可撤销认证会话已经创建的事实。</summary>
public sealed record SessionCreated(
    [property: JsonPropertyName("session_id")] SessionId SessionId,
    [property: JsonPropertyName("player_id")] PlayerId PlayerId,
    [property: JsonPropertyName("device_id")] DeviceId DeviceId,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt)
    : IVersionedEventPayload
{
    public static string EventType => PlatformEventTypes.SessionCreated;
    public static int SchemaVersion => 1;
}

/// <summary>认证会话已经撤销的事实；ReasonCode 必须是稳定枚举式代码。</summary>
public sealed record SessionRevoked(
    [property: JsonPropertyName("session_id")] SessionId SessionId,
    [property: JsonPropertyName("player_id")] PlayerId PlayerId,
    [property: JsonPropertyName("reason_code")] string ReasonCode,
    [property: JsonPropertyName("revoked_at")] DateTimeOffset RevokedAt)
    : IVersionedEventPayload
{
    public static string EventType => PlatformEventTypes.SessionRevoked;
    public static int SchemaVersion => 1;
}

/// <summary>房间控制面聚合创建完成的事实，不代表 Dedicated Server 已就绪。</summary>
public sealed record RoomCreated(
    [property: JsonPropertyName("room_id")] RoomId RoomId,
    [property: JsonPropertyName("room_epoch")] RoomEpoch RoomEpoch,
    [property: JsonPropertyName("owner_player_id")] PlayerId OwnerPlayerId,
    [property: JsonPropertyName("rule_set_version")] RuleSetVersion RuleSetVersion,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt)
    : IVersionedEventPayload
{
    public static string EventType => PlatformEventTypes.RoomCreated;
    public static int SchemaVersion => 1;
}

/// <summary>玩家已加入房间成员列表的控制面事实。</summary>
public sealed record RoomMemberJoined(
    [property: JsonPropertyName("room_id")] RoomId RoomId,
    [property: JsonPropertyName("room_epoch")] RoomEpoch RoomEpoch,
    [property: JsonPropertyName("player_id")] PlayerId PlayerId,
    [property: JsonPropertyName("joined_at")] DateTimeOffset JoinedAt)
    : IVersionedEventPayload
{
    public static string EventType => PlatformEventTypes.RoomMemberJoined;
    public static int SchemaVersion => 1;
}

/// <summary>房间控制面状态发生单调版本迁移的事实。</summary>
public sealed record RoomStateChanged(
    [property: JsonPropertyName("room_id")] RoomId RoomId,
    [property: JsonPropertyName("room_epoch")] RoomEpoch RoomEpoch,
    [property: JsonPropertyName("previous_state")] string PreviousState,
    [property: JsonPropertyName("current_state")] string CurrentState,
    [property: JsonPropertyName("state_version")] StateVersion StateVersion)
    : IVersionedEventPayload
{
    public static string EventType => PlatformEventTypes.RoomStateChanged;
    public static int SchemaVersion => 1;
}

/// <summary>控制面请求分配 Dedicated Server 的事实。</summary>
public sealed record AllocationRequested(
    [property: JsonPropertyName("allocation_id")] AllocationId AllocationId,
    [property: JsonPropertyName("room_id")] RoomId RoomId,
    [property: JsonPropertyName("room_epoch")] RoomEpoch RoomEpoch,
    [property: JsonPropertyName("requested_at")] DateTimeOffset RequestedAt)
    : IVersionedEventPayload
{
    public static string EventType => PlatformEventTypes.AllocationRequested;
    public static int SchemaVersion => 1;
}

/// <summary>编排层已绑定服务器实例的事实；不包含 Join Ticket。</summary>
public sealed record GameServerAllocated(
    [property: JsonPropertyName("allocation_id")] AllocationId AllocationId,
    [property: JsonPropertyName("room_id")] RoomId RoomId,
    [property: JsonPropertyName("server_instance_id")] ServerInstanceId ServerInstanceId,
    [property: JsonPropertyName("allocated_at")] DateTimeOffset AllocatedAt)
    : IVersionedEventPayload
{
    public static string EventType => PlatformEventTypes.GameServerAllocated;
    public static int SchemaVersion => 1;
}

/// <summary>服务器实例完成资源加载并可接受玩家的事实。</summary>
public sealed record GameServerReady(
    [property: JsonPropertyName("server_instance_id")] ServerInstanceId ServerInstanceId,
    [property: JsonPropertyName("room_id")] RoomId RoomId,
    [property: JsonPropertyName("build_version")] BuildVersion BuildVersion,
    [property: JsonPropertyName("ready_at")] DateTimeOffset ReadyAt)
    : IVersionedEventPayload
{
    public static string EventType => PlatformEventTypes.GameServerReady;
    public static int SchemaVersion => 1;
}

/// <summary>权威游戏服务器确认玩家网络连接完成的事实。</summary>
public sealed record PlayerConnected(
    [property: JsonPropertyName("room_id")] RoomId RoomId,
    [property: JsonPropertyName("player_id")] PlayerId PlayerId,
    [property: JsonPropertyName("server_instance_id")] ServerInstanceId ServerInstanceId,
    [property: JsonPropertyName("connected_at")] DateTimeOffset ConnectedAt)
    : IVersionedEventPayload
{
    public static string EventType => PlatformEventTypes.PlayerConnected;
    public static int SchemaVersion => 1;
}

/// <summary>权威游戏服务器观察到玩家断开连接的事实。</summary>
public sealed record PlayerDisconnected(
    [property: JsonPropertyName("room_id")] RoomId RoomId,
    [property: JsonPropertyName("player_id")] PlayerId PlayerId,
    [property: JsonPropertyName("reason_code")] string ReasonCode,
    [property: JsonPropertyName("disconnected_at")] DateTimeOffset DisconnectedAt)
    : IVersionedEventPayload
{
    public static string EventType => PlatformEventTypes.PlayerDisconnected;
    public static int SchemaVersion => 1;
}

/// <summary>权威服务器开始一场牌局的事实，不公开随机种子或牌序。</summary>
public sealed record MatchStarted(
    [property: JsonPropertyName("match_id")] MatchId MatchId,
    [property: JsonPropertyName("room_id")] RoomId RoomId,
    [property: JsonPropertyName("rule_set_version")] RuleSetVersion RuleSetVersion,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt)
    : IVersionedEventPayload
{
    public static string EventType => PlatformEventTypes.MatchStarted;
    public static int SchemaVersion => 1;
}

/// <summary>权威服务器结束一场牌局的事实；只携带摘要，不携带最终资产写入。</summary>
public sealed record MatchFinished(
    [property: JsonPropertyName("match_id")] MatchId MatchId,
    [property: JsonPropertyName("room_id")] RoomId RoomId,
    [property: JsonPropertyName("result_digest")] string ResultDigest,
    [property: JsonPropertyName("finished_at")] DateTimeOffset FinishedAt)
    : IVersionedEventPayload
{
    public static string EventType => PlatformEventTypes.MatchFinished;
    public static int SchemaVersion => 1;
}

/// <summary>Settlement 模块已经幂等提交最终结算的事实。</summary>
public sealed record SettlementCommitted(
    [property: JsonPropertyName("match_id")] MatchId MatchId,
    [property: JsonPropertyName("room_id")] RoomId RoomId,
    [property: JsonPropertyName("settlement_id")] string SettlementId,
    [property: JsonPropertyName("committed_at")] DateTimeOffset CommittedAt)
    : IVersionedEventPayload
{
    public static string EventType => PlatformEventTypes.SettlementCommitted;
    public static int SchemaVersion => 1;
}

/// <summary>房间控制面进入不可恢复终态的事实。</summary>
public sealed record RoomTerminated(
    [property: JsonPropertyName("room_id")] RoomId RoomId,
    [property: JsonPropertyName("room_epoch")] RoomEpoch RoomEpoch,
    [property: JsonPropertyName("reason_code")] string ReasonCode,
    [property: JsonPropertyName("terminated_at")] DateTimeOffset TerminatedAt)
    : IVersionedEventPayload
{
    public static string EventType => PlatformEventTypes.RoomTerminated;
    public static int SchemaVersion => 1;
}
