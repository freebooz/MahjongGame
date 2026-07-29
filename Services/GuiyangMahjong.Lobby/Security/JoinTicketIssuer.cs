using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Security;

public interface IJoinTicketIssuer
{
    (string Ticket, DateTimeOffset ExpiresAtUtc) Issue(
        string playerId,
        string displayName,
        LobbyRoom room,
        string serverInstanceId);
}

public sealed class HmacJoinTicketIssuer(
    IOptions<LobbyOptions> options,
    TimeProvider timeProvider) : IJoinTicketIssuer
{
    private readonly byte[] key = Encoding.UTF8.GetBytes(options.Value.JoinTicketSigningKey);

    public (string Ticket, DateTimeOffset ExpiresAtUtc) Issue(
        string playerId,
        string displayName,
        LobbyRoom room,
        string serverInstanceId)
    {
        var expiresAt = timeProvider.GetUtcNow().AddSeconds(30);
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            playerId,
            displayName,
            roomId = room.RoomId,
            matchId = room.MatchId,
            serverInstanceId,
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
