using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Rooms;

namespace GuiyangMahjong.Lobby.Lobby;

/// <summary>
/// 大厅只读聚合服务，仅负责把房间写模型转换为公开目录。
/// 它不修改成员、状态机或分配实例，也不得直接访问 Kubernetes。
/// </summary>
public sealed class LobbyReadService(IRoomReader rooms)
{
    /// <summary>返回公开房间目录，显式排除玩家身份、密码、路由和内部版本凭据。</summary>
    public async Task<IReadOnlyList<RoomDirectoryItem>> ListRoomsAsync(
        CancellationToken cancellationToken) =>
        (await rooms.ListPublicRoomsAsync(cancellationToken))
        .Select(room => new RoomDirectoryItem(
            room.RoomCode,
            room.Lifecycle,
            room.PlayerIds.Length,
            room.MaximumPlayers,
            room.Password is not null,
            room.RoundCount))
        .ToArray();
}
