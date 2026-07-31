using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Security;

namespace GuiyangMahjong.Lobby.Tests;

/// <summary>覆盖洗牌承诺、房间绑定和事件链防篡改门禁。</summary>
public sealed class ShuffleFairnessVerifierTests
{
    [Fact]
    public void ValidProofChain_IsAccepted()
    {
        var room = NewRoom();
        var report = TestShuffleFairness.CreateReport(
            room.RoomId, room.LastServerInstanceId!, 1, room.RoundCount,
            [new MatchPlayerResult(room.OwnerPlayerId, 0, 1, 8)]);

        Assert.True(ShuffleFairnessVerifier.Verify(report, room));
    }

    [Fact]
    public void CommitmentBoundToDifferentRoom_IsRejected()
    {
        var room = NewRoom();
        var report = TestShuffleFairness.CreateReport(
            Guid.NewGuid().ToString(), room.LastServerInstanceId!, 1, room.RoundCount,
            [new MatchPlayerResult(room.OwnerPlayerId, 0, 1, 8)]) with
        {
            RoomId = room.RoomId
        };

        // 只替换 report.RoomId 不会重算证明，验证器必须识别承诺仍绑定到另一个房间。
        Assert.False(ShuffleFairnessVerifier.Verify(report, room));
    }

    [Fact]
    public void ReorderedProofsOrTamperedRule_IsRejected()
    {
        var room = NewRoom();
        var report = TestShuffleFairness.CreateReport(
            room.RoomId, room.LastServerInstanceId!, 1, room.RoundCount,
            [new MatchPlayerResult(room.OwnerPlayerId, 0, 1, 8)]);
        var reordered = report with
        {
            ShuffleProofs = report.ShuffleProofs!.Reverse().ToArray()
        };
        var tamperedRule = report with
        {
            ShuffleProofs =
            [
                report.ShuffleProofs![0] with { RuleVersion = 2 },
                .. report.ShuffleProofs.Skip(1)
            ]
        };

        Assert.False(ShuffleFairnessVerifier.Verify(reordered, room));
        Assert.False(ShuffleFairnessVerifier.Verify(tamperedRule, room));
    }

    /// <summary>创建只包含验证器所需冻结规则和身份字段的权威房间。</summary>
    private static LobbyRoom NewRoom() => new()
    {
        RoomId = Guid.NewGuid().ToString(),
        RoomCode = "123456",
        OwnerPlayerId = "fairness-owner",
        RoundCount = 4,
        PublicRoom = false,
        AutoStart = true,
        MaximumPlayers = 4,
        RuleSnapshot = new Dictionary<string, object?>
        {
            ["ruleId"] = "GuiyangMainstreamV1",
            ["ruleVersion"] = 1
        },
        Lifecycle = RoomLifecycle.Settling,
        PlayerIds = ["fairness-owner"],
        LastServerInstanceId = Guid.NewGuid().ToString(),
        MatchId = Guid.NewGuid().ToString(),
        StateSequence = 1,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };
}
