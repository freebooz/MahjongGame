#include "MahjongCoreTestSupport.h"
#include "Server/GuiyangFairShuffle.h"

#if WITH_DEV_AUTOMATION_TESTS

/**
 * 覆盖牌墙构成、发牌方向和规则快照确定性。
 * 保持原自动化测试路径和断言不变，失败由 Unreal Automation Framework 汇总。
 */
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongDefaultDeckTest, "GuiyangMahjong.Core.Deck.Default108", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongDefaultDeckTest::RunTest(const FString& Parameters)
{
    UMahjongDeckManager* Deck = NewObject<UMahjongDeckManager>();
    Deck->InitializeDeck(FMahjongRuleConfig());
    TestEqual(TEXT("贵阳主流默认牌墙必须为 108 张"), Deck->GetRemainingCount(), 108);
    int32 Counts[34] = {};
    for (const FMahjongTile& Tile : Deck->GetDeckForServerTest())
    {
        if (Tile.GetRuleIndex() >= 0) ++Counts[Tile.GetRuleIndex()];
    }
    for (int32 Index = 0; Index < 27; ++Index) TestEqual(FString::Printf(TEXT("数牌类型 %d 必须有 4 张"), Index), Counts[Index], 4);
    for (int32 Index = 27; Index < 34; ++Index) TestEqual(FString::Printf(TEXT("默认牌墙不得包含字牌类型 %d"), Index), Counts[Index], 0);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongDeckTest, "GuiyangMahjong.Core.Deck.Optional136", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongDeckTest::RunTest(const FString& Parameters)
{
    UMahjongDeckManager* Deck = NewObject<UMahjongDeckManager>();
    Deck->InitializeStandardDeck();
    TestEqual(TEXT("贵阳规则即使收到旧版 136 请求也必须回退到 108 张"), Deck->GetRemainingCount(), 108);
    TSet<int32> UniqueIds;
    int32 Counts[34] = {};
    for (const FMahjongTile& Tile : Deck->GetDeckForServerTest())
    {
        UniqueIds.Add(Tile.UniqueId);
        if (Tile.GetRuleIndex() >= 0) ++Counts[Tile.GetRuleIndex()];
    }
    TestEqual(TEXT("每张物理牌ID唯一"), UniqueIds.Num(), 108);
    for (int32 Index = 0; Index < 27; ++Index) TestEqual(FString::Printf(TEXT("数牌牌型%d必须有4张"), Index), Counts[Index], 4);
    for (int32 Index = 27; Index < 34; ++Index) TestEqual(FString::Printf(TEXT("贵阳牌库不得包含字牌%d"), Index), Counts[Index], 0);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongShuffleDealTest, "GuiyangMahjong.Core.Deck.ShuffleAndDeal", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongShuffleDealTest::RunTest(const FString& Parameters)
{
    UMahjongDeckManager* Deck = NewObject<UMahjongDeckManager>();
    Deck->InitializeDeck(FMahjongRuleConfig());
    Deck->ShuffleDeck(20260715);
    TArray<FMahjongHand> Hands;
    TestTrue(TEXT("初始发牌应成功"), Deck->DealInitialHands(Hands, 2));
    TestEqual(TEXT("必须有四手牌"), Hands.Num(), 4);
    for (int32 Seat = 0; Seat < 4; ++Seat) TestEqual(TEXT("庄家14张、闲家13张"), Hands[Seat].Tiles.Num(), Seat == 2 ? 14 : 13);
    TestEqual(TEXT("108 张牌墙发牌后应剩余 55 张"), Deck->GetRemainingCount(), 55);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongDirectionRuleTest, "GuiyangMahjong.Core.Deck.ClockwiseDrawCounterClockwisePlay",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongDirectionRuleTest::RunTest(const FString& Parameters)
{
    TestEqual(TEXT("庄家下家必须是右手边的逆时针下一座"),
        UMahjongTableEngine::GetNextTurnSeatCounterClockwise(2), 3);
    TestEqual(TEXT("从庄家到下家逆时针距离为一座"),
        UMahjongTableEngine::GetCounterClockwiseSeatDistance(2, 3), 1);
    TestEqual(TEXT("从庄家到上家逆时针距离为三座"),
        UMahjongTableEngine::GetCounterClockwiseSeatDistance(2, 1), 3);

    UMahjongDeckManager* Wall = NewObject<UMahjongDeckManager>();
    Wall->InitializeDeck(FMahjongRuleConfig());
    Wall->ConfigureWallBreak(2, 6);
    TestEqual(TEXT("骰子从庄家逆时针数六应落在下家牌墙"), Wall->GetWallBreakSide(), 3);
    TestEqual(TEXT("开门必须从牌墙右端向左数六墩"), Wall->GetWallBreakStackFromRight(), 6);
    FMahjongTile FirstDraw;
    TestTrue(TEXT("开门后第一张顺时针抓牌成功"), Wall->DrawTile(FirstDraw));
    TestEqual(TEXT("顺时针牌墙游标每抓一张增加一"), Wall->GetClockwiseDrawOffset(), 1);
    TestEqual(TEXT("完整双层牌墩中右端向左数后的物理起点必须稳定"),
        FirstDraw.UniqueId, 40);

    UMahjongDeckManager* DealWall = NewObject<UMahjongDeckManager>();
    DealWall->InitializeDeck(FMahjongRuleConfig());
    TArray<FMahjongHand> Hands;
    TestTrue(TEXT("从庄家开始按逆时针顺序发牌成功"), DealWall->DealInitialHands(Hands, 2));
    const auto HasUniqueId = [&Hands](const int32 Seat, const int32 UniqueId)
    {
        return Hands[Seat].Tiles.ContainsByPredicate(
            [UniqueId](const FMahjongTile& Tile) { return Tile.UniqueId == UniqueId; });
    };
    TestTrue(TEXT("庄家必须先取得第一组四张"), HasUniqueId(2, 0) && HasUniqueId(2, 3));
    TestTrue(TEXT("庄家下家必须取得第二组四张"), HasUniqueId(3, 4) && HasUniqueId(3, 7));
    TestTrue(TEXT("对家必须取得第三组四张"), HasUniqueId(0, 8) && HasUniqueId(0, 11));
    TestTrue(TEXT("上家必须取得第四组四张"), HasUniqueId(1, 12) && HasUniqueId(1, 15));
    TestEqual(TEXT("完成庄14闲13后牌墙顺时针消耗53张"), DealWall->GetClockwiseDrawOffset(), 53);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongRuleSnapshotTest, "GuiyangMahjong.Rules.SnapshotDeterminism", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongRuleSnapshotTest::RunTest(const FString& Parameters)
{
    FMahjongRuleConfig Requested;
    Requested.BaseScore = -5;
    Requested.ReconnectTimeoutSeconds = 9999;
    const FGuiyangRuleSnapshot First = UGuiyangRuleSnapshotLibrary::CreateSnapshot(Requested);
    const FGuiyangRuleSnapshot Second = UGuiyangRuleSnapshotLibrary::CreateSnapshot(Requested);

    TestEqual(TEXT("默认规则快照必须锁定 108 张牌"), First.GetTileCount(), 108);
    TestEqual(TEXT("非法底分必须规范化"), First.Config.BaseScore, 1);
    TestEqual(TEXT("重连超时必须限制在服务端允许范围"), First.Config.ReconnectTimeoutSeconds, 600);
    TestEqual(TEXT("相同配置必须产生相同规则哈希"), First.RuleHash, Second.RuleHash);
    TestTrue(TEXT("新建规则快照必须通过完整性校验"), UGuiyangRuleSnapshotLibrary::VerifySnapshot(First));

    FGuiyangRuleSnapshot Tampered = First;
    Tampered.Config.bEnableQiDui = !Tampered.Config.bEnableQiDui;
    TestFalse(TEXT("被修改的规则快照必须校验失败"), UGuiyangRuleSnapshotLibrary::VerifySnapshot(Tampered));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMahjongFairShuffleProofTest,
    "GuiyangMahjong.Server.Fairness.CommitmentAndTamperDetection",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongFairShuffleProofTest::RunTest(const FString& Parameters)
{
    const FString RoomId = TEXT("11111111-2222-3333-4444-555555555555");
    const FGuiyangRuleSnapshot Rules =
        UGuiyangRuleSnapshotLibrary::CreateSnapshot(FMahjongRuleConfig());
    int32 Seed = 0;
    FGuiyangShuffleAuditProof Proof;
    FString Error;
    TestTrue(TEXT("CSPRNG 必须生成可用洗牌材料"),
        FGuiyangFairShuffle::Generate(RoomId, 1, Rules, Seed, Proof, Error));
    TestEqual(TEXT("种子必须使用固定 8 位十六进制"), Proof.SeedHex.Len(), 8);
    TestEqual(TEXT("nonce 必须提供 256 位防穷举随机量"), Proof.ServerNonceHex.Len(), 64);
    TestEqual(TEXT("承诺必须是 SHA-256"), Proof.SeedCommitment.Len(), 64);

    UMahjongDeckManager* Deck = NewObject<UMahjongDeckManager>();
    Deck->InitializeDeck(Rules.Config);
    Deck->ShuffleDeck(Seed);
    Proof.DeckOrderDigest =
        FGuiyangFairShuffle::CalculateDeckOrderDigest(Deck->GetDeckForServerTest());
    Proof.RevealedAtUtc = Proof.CreatedAtUtc + FTimespan::FromSeconds(1);
    TestTrue(TEXT("未篡改证明必须通过复核"),
        FGuiyangFairShuffle::Verify(
            RoomId, Rules, Deck->GetDeckForServerTest(), Proof));

    FGuiyangShuffleAuditProof Tampered = Proof;
    Tampered.RuleVersion += 1;
    TestFalse(TEXT("修改规则版本必须使证明失效"),
        FGuiyangFairShuffle::Verify(
            RoomId, Rules, Deck->GetDeckForServerTest(), Tampered));
    TestNotEqual(TEXT("更换房间必须产生不同承诺"),
        Proof.SeedCommitment,
        FGuiyangFairShuffle::CalculateCommitment(
            TEXT("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), Proof));

    // 本地模式只有六位公开房间码；该身份仍需进入同一承诺协议，不能因不是 UUID 而阻断开局。
    int32 LocalSeed = 0;
    FGuiyangShuffleAuditProof LocalProof;
    FString LocalError;
    TestTrue(TEXT("六位公开房间码必须能够生成公平洗牌材料"),
        FGuiyangFairShuffle::Generate(
            TEXT("400472"), 1, Rules, LocalSeed, LocalProof, LocalError));
    TestEqual(TEXT("公开房间码生成的承诺必须为 SHA-256"),
        LocalProof.SeedCommitment.Len(), 64);
    TestFalse(TEXT("带协议分隔符的房间身份必须被拒绝"),
        FGuiyangFairShuffle::Generate(
            TEXT("400472|roomId=forged"), 1, Rules, LocalSeed, LocalProof, LocalError));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMahjongSecureRandomSmokeTest,
    "GuiyangMahjong.Server.Fairness.SecureRandomStatisticalSmoke",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongSecureRandomSmokeTest::RunTest(const FString& Parameters)
{
    const FString RoomId = TEXT("11111111-2222-3333-4444-555555555555");
    const FGuiyangRuleSnapshot Rules =
        UGuiyangRuleSnapshotLibrary::CreateSnapshot(FMahjongRuleConfig());
    constexpr int32 SampleCount = 1024;
    int32 HighBitCount = 0;
    TSet<FString> UniqueSeeds;
    for (int32 Sample = 0; Sample < SampleCount; ++Sample)
    {
        int32 Seed = 0;
        FGuiyangShuffleAuditProof Proof;
        FString Error;
        if (!FGuiyangFairShuffle::Generate(
            RoomId, Sample + 1, Rules, Seed, Proof, Error))
        {
            AddError(FString::Printf(TEXT("安全随机样本生成失败：%s"), *Error));
            return false;
        }
        UniqueSeeds.Add(Proof.SeedHex);
        if ((static_cast<uint32>(Seed) & 0x80000000u) != 0)
        {
            ++HighBitCount;
        }
    }

    // 该测试只用于发现固定值、严重偏置或熵源失效，不替代正式 NIST/Dieharder 离线检验。
    TestTrue(TEXT("32 位种子样本不得出现可疑的大量碰撞"),
        UniqueSeeds.Num() >= SampleCount - 4);
    TestTrue(TEXT("最高位分布应处于宽松的六西格玛冒烟范围"),
        HighBitCount >= 400 && HighBitCount <= 624);
    return true;
}

#endif
