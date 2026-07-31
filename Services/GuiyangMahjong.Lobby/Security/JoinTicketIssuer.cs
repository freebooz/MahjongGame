// 加入房间票据签发器：把玩家、房间、服务器实例和短期有效期绑定到不可篡改票据。
// 验证必须检查全部绑定字段和过期时间，签名密钥不得下发给客户端或写入日志。
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Security;

/// <summary>短期房间加入票据签发边界；实现必须绑定玩家、房间、匹配和服务器实例。</summary>
public interface IJoinTicketIssuer
{
    /// <summary>
    /// 为当前路由签发一次短期票据并返回 UTC 过期时间。
    /// 调用前房间必须已分配且玩家具备加入资格。
    /// </summary>
    (string Ticket, DateTimeOffset ExpiresAtUtc) Issue(
        PlayerIdentity player,
        LobbyRoom room,
        string serverInstanceId);
}

/// <summary>
/// 基于 HMAC-SHA256 的加入票据实现。
/// 每张票据包含随机 nonce 并固定 30 秒有效期，密钥只由 Lobby 与游戏服共享。
/// </summary>
public sealed class HmacJoinTicketIssuer(
    IOptions<LobbyOptions> options,
    TimeProvider timeProvider) : IJoinTicketIssuer
{
    // 共享签名密钥只驻留服务内存；最低长度和生产注入由启动配置验证负责。
    private readonly byte[] key = Encoding.UTF8.GetBytes(options.Value.JoinTicketSigningKey);

    /// <inheritdoc/>
    public (string Ticket, DateTimeOffset ExpiresAtUtc) Issue(
        PlayerIdentity player,
        LobbyRoom room,
        string serverInstanceId)
    {
        var issuedAt = timeProvider.GetUtcNow();
        var expiresAt = issuedAt.AddSeconds(30);
        var seat = room.Seats.SingleOrDefault(item =>
            string.Equals(item.PlayerId, player.PlayerId, StringComparison.Ordinal));
        if (seat is null)
        {
            throw new InvalidOperationException("Join Ticket 只能为已分配座位的房间成员签发。");
        }
        // ticketId 用于跨日志关联，nonce 用于一次性消费；两者不能互相替代。
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            ticketId = Guid.NewGuid().ToString("N"),
            playerId = player.PlayerId,
            displayName = player.DisplayName,
            roomId = room.RoomId,
            matchId = room.MatchId,
            seatId = seat.SeatIndex,
            sessionId = player.SessionId,
            sessionEpoch = player.SessionEpoch,
            securityEpoch = player.SecurityEpoch,
            serverInstanceId,
            roomEpoch = room.RoomEpoch,
            clientBuild = player.ClientBuild,
            protocolVersion = int.Parse(player.ProtocolVersion, System.Globalization.CultureInfo.InvariantCulture),
            ruleSetVersion = room.RuleSetVersion,
            issuedAtUnixSeconds = issuedAt.ToUnixTimeSeconds(),
            expiresAtUnixSeconds = expiresAt.ToUnixTimeSeconds(),
            nonce = Guid.NewGuid().ToString("N")
        }));
        var signature = Base64Url(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));
        return ($"{payload}.{signature}", expiresAt);
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
