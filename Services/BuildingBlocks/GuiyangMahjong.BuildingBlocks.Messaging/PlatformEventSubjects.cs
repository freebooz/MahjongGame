using GuiyangMahjong.Contracts.Events;

namespace GuiyangMahjong.BuildingBlocks.Messaging;

/// <summary>
/// 平台事件到版本化 JetStream Subject 的唯一映射表。
/// Subject 是传输地址而不是事件语义；事件类型升级和 Schema 版本升级必须显式修改映射，
/// 禁止通过字符串拼接意外发布到未授权 Subject。
/// </summary>
public static class PlatformEventSubjects
{
    /// <summary>承载首批平台事件的 JetStream 名称。</summary>
    public const string StreamName = "MAHJONG_PLATFORM_EVENTS";

    /// <summary>毒消息和超过最大投递次数的事件进入人工处理流，不再自动产生业务副作用。</summary>
    public const string DeadLetterSubject = "platform.failed.v1";

    private static readonly IReadOnlyDictionary<string, string> Subjects =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PlatformEventTypes.SessionCreated] = "identity.session.created.v1",
            [PlatformEventTypes.SessionRevoked] = "identity.session.revoked.v1",
            [PlatformEventTypes.RoomCreated] = "room.created.v1",
            [PlatformEventTypes.RoomStateChanged] = "room.state.changed.v1",
            [PlatformEventTypes.AllocationRequested] = "allocation.requested.v1",
            [PlatformEventTypes.GameServerAllocated] = "gameserver.allocated.v1",
            [PlatformEventTypes.GameServerReady] = "gameserver.ready.v1",
            [PlatformEventTypes.PlayerConnected] = "player.connected.v1",
            [PlatformEventTypes.PlayerDisconnected] = "player.disconnected.v1",
            [PlatformEventTypes.MatchStarted] = "match.started.v1",
            [PlatformEventTypes.MatchFinished] = "match.finished.v1",
            [PlatformEventTypes.SettlementCommitted] = "settlement.committed.v1",
            [PlatformEventTypes.RoomTerminated] = "room.terminated.v1",
            [PlatformEventTypes.ConfigurationPublished] = "configuration.published.v1"
        };

    /// <summary>返回所有允许进入平台 Stream 的业务 Subject，不包含 DLQ。</summary>
    public static IReadOnlyCollection<string> All => Subjects.Values.ToArray();

    /// <summary>
    /// 根据事件类型和 Schema 版本解析 Subject；未知事件或非 v1 契约会失败关闭，
    /// 避免消费者把不理解的新版本当作旧版本处理。
    /// </summary>
    public static string Resolve(string eventType, int schemaVersion)
    {
        if (schemaVersion != 1 || !Subjects.TryGetValue(eventType, out var subject))
        {
            throw new InvalidDataException(
                $"事件没有已批准的 JetStream Subject：{eventType} v{schemaVersion}。");
        }
        return subject;
    }

    /// <summary>验证收到的 Subject 与信封声明一致，阻止跨 Subject 伪装事件。</summary>
    public static bool Matches(
        string subject,
        string eventType,
        int schemaVersion) =>
        string.Equals(
            subject,
            Resolve(eventType, schemaVersion),
            StringComparison.Ordinal);
}
