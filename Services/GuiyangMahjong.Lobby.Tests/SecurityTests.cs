using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Security;
using GuiyangMahjong.Lobby.Rooms;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace GuiyangMahjong.Lobby.Tests;

public sealed class SecurityTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Token_ValidSignature_AcceptsTrustedIdentity()
    {
        var time = new FixedTimeProvider(Now);
        var validator = CreateValidator(time);
        var token = HmacPlayerTokenValidator.CreateSignedToken(
            LobbyWebApplicationFactory.SigningKey,
            new PlayerIdentity("guest-001", "玩家甲", "Guest"),
            Now.AddMinutes(5));

        var result = validator.Validate(token);

        Assert.True(result.IsValid);
        Assert.Equal("guest-001", result.Player?.PlayerId);
    }

    /// <summary>会话标识和 Epoch 必须只在签名校验成功后进入 Lobby 调用身份。</summary>
    [Fact]
    public void Token_SignedSessionClaims_ArePreservedForJoinTicketBinding()
    {
        var validator = CreateValidator(new FixedTimeProvider(Now));
        var token = HmacPlayerTokenValidator.CreateSignedToken(
            LobbyWebApplicationFactory.SigningKey,
            new PlayerIdentity(
                "guest-session",
                "会话玩家",
                "Guest",
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                7,
                3),
            Now.AddMinutes(5));

        var result = validator.Validate(token);

        Assert.True(result.IsValid);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", result.Player?.SessionId);
        Assert.Equal(7, result.Player?.SessionEpoch);
        Assert.Equal(3, result.Player?.SecurityEpoch);
    }

    /// <summary>Join Ticket 必须把座位、会话、实例、Epoch 和三类版本冻结进签名载荷。</summary>
    [Fact]
    public void JoinTicket_StrongClaims_AreBoundToAllocatedRoom()
    {
        var options = CreateOptions();
        var issuer = new HmacJoinTicketIssuer(
            Microsoft.Extensions.Options.Options.Create(options),
            new FixedTimeProvider(Now));
        var player = new PlayerIdentity(
            "guest-ticket",
            "票据玩家",
            "Guest",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            5,
            2,
            "client-build-17",
            "1");
        var room = new LobbyRoom
        {
            RoomId = "11111111-1111-1111-1111-111111111111",
            RoomCode = "123456",
            OwnerPlayerId = player.PlayerId,
            RoundCount = 4,
            PublicRoom = false,
            AutoStart = false,
            MaximumPlayers = 4,
            RuleSnapshot = [],
            Lifecycle = RoomLifecycle.Playing,
            PlayerIds = [player.PlayerId],
            Seats = [new RoomSeat(player.PlayerId, 2, Now)],
            MatchId = "22222222-2222-2222-2222-222222222222",
            RoomEpoch = 8,
            RuleSetVersion = "guiyang-zhuoji-v1",
            BuildVersion = "server-build-42",
            Route = new GameServerRoute(
                "request", player.PlayerId, "11111111-1111-1111-1111-111111111111",
                "33333333-3333-3333-3333-333333333333",
                "22222222-2222-2222-2222-222222222222",
                "127.0.0.1", 7777, "", Now, 8, "server-build-42", "guiyang-zhuoji-v1", 1)
        };

        var issued = issuer.Issue(player, room, room.Route.ServerInstanceId);
        var encodedPayload = issued.Ticket.Split('.')[0].Replace('-', '+').Replace('_', '/');
        encodedPayload = encodedPayload.PadRight(
            encodedPayload.Length + ((4 - encodedPayload.Length % 4) % 4), '=');
        using var payload = JsonDocument.Parse(Convert.FromBase64String(encodedPayload));

        Assert.Equal(2, payload.RootElement.GetProperty("seatId").GetInt32());
        Assert.Equal(player.SessionId, payload.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(5, payload.RootElement.GetProperty("sessionEpoch").GetInt64());
        Assert.Equal(8, payload.RootElement.GetProperty("roomEpoch").GetInt64());
        Assert.Equal("client-build-17", payload.RootElement.GetProperty("clientBuild").GetString());
        Assert.Equal("guiyang-zhuoji-v1", payload.RootElement.GetProperty("ruleSetVersion").GetString());
    }

    [Fact]
    public void Token_Expired_IsRejected()
    {
        var time = new FixedTimeProvider(Now);
        var validator = CreateValidator(time);
        var token = HmacPlayerTokenValidator.CreateSignedToken(
            LobbyWebApplicationFactory.SigningKey,
            new PlayerIdentity("guest-expired", "过期玩家", "Guest"),
            Now.AddSeconds(-1));

        var result = validator.Validate(token);

        Assert.False(result.IsValid);
        Assert.Contains("过期", result.ChineseReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Token_ClientTampering_IsRejected()
    {
        var time = new FixedTimeProvider(Now);
        var validator = CreateValidator(time);
        var token = HmacPlayerTokenValidator.CreateSignedToken(
            LobbyWebApplicationFactory.SigningKey,
            new PlayerIdentity("guest-001", "玩家甲", "Guest"),
            Now.AddMinutes(5));
        var tampered = $"A{token[1..]}";

        Assert.False(validator.Validate(tampered).IsValid);
    }

    /// <summary>密钥轮换重叠窗口内，Lobby 必须接受旧密钥签发且尚未过期的访问令牌。</summary>
    [Fact]
    public void Token_PreviousRotationKey_IsAcceptedDuringOverlapWindow()
    {
        const string previousKey =
            "test-only-previous-player-signing-key-long-enough";
        var options = CreateOptions();
        options = new LobbyOptions
        {
            TokenSigningKey = options.TokenSigningKey,
            PreviousTokenValidationKeys = [previousKey],
            PasswordFailureLimit = options.PasswordFailureLimit,
            PasswordFailureWindowSeconds = options.PasswordFailureWindowSeconds
        };
        var validator = new HmacPlayerTokenValidator(
            Microsoft.Extensions.Options.Options.Create(options),
            new FixedTimeProvider(Now));
        var token = HmacPlayerTokenValidator.CreateSignedToken(
            previousKey,
            new PlayerIdentity("guest-rotation", "轮换测试玩家", "Guest"),
            Now.AddMinutes(5));

        Assert.True(validator.Validate(token).IsValid);
    }

    [Fact]
    public void Password_WrongAttempts_AreRateLimitedAndRecoverAfterWindow()
    {
        var time = new FixedTimeProvider(Now);
        var options = Microsoft.Extensions.Options.Options.Create(CreateOptions());
        var service = new RoomPasswordService(options, time);
        var protectedPassword = service.Protect("654321");

        for (var index = 0; index < 5; index++)
        {
            Assert.Equal(
                PasswordVerificationStatus.Wrong,
                service.Verify("player", "room", protectedPassword, "wrong00").Status);
        }
        Assert.Equal(
            PasswordVerificationStatus.RateLimited,
            service.Verify("player", "room", protectedPassword, "654321").Status);

        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(
            PasswordVerificationStatus.Success,
            service.Verify("player", "room", protectedPassword, "654321").Status);
    }

    private static HmacPlayerTokenValidator CreateValidator(TimeProvider timeProvider) =>
        new(Microsoft.Extensions.Options.Options.Create(CreateOptions()), timeProvider);

    private static LobbyOptions CreateOptions() => new()
    {
        TokenSigningKey = LobbyWebApplicationFactory.SigningKey,
        JoinTicketSigningKey = "test-only-join-ticket-signing-key-long-enough",
        PasswordFailureLimit = 5,
        PasswordFailureWindowSeconds = 300
    };
}
