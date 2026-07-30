#include "MahjongCoreTestSupport.h"

#if WITH_DEV_AUTOMATION_TESTS

/**
 * 覆盖胡牌、贵阳特殊规则和结算计分零和约束。
 * 保持原自动化测试路径和断言不变，失败由 Unreal Automation Framework 汇总。
 */
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongHuTest, "GuiyangMahjong.Rules.StandardHu", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongHuTest::RunTest(const FString& Parameters)
{
    const FMahjongHand Valid = MahjongTest::MakeHand({0,1,2, 9,10,11, 18,18,18, 27,27,27, 31,31});
    const FMahjongHand Invalid = MahjongTest::MakeHand({0,1,2,3,4,5,6,7,8,9,10,11,12,13});
    TestTrue(TEXT("标准四组加一对必须可胡"), UMahjongHuChecker::CanHu(Valid, false));
    TestFalse(TEXT("无将牌的牌型不能胡"), UMahjongHuChecker::CanHu(Invalid, false));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongQiDuiTest, "GuiyangMahjong.Rules.QiDuiSwitch", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongQiDuiTest::RunTest(const FString& Parameters)
{
    const FMahjongHand QiDui = MahjongTest::MakeHand({0,0, 2,2, 9,9, 11,11, 18,18, 27,27, 31,31});
    TestTrue(TEXT("七对开关开启时可胡"), UMahjongHuChecker::CanHu(QiDui, true));
    TestFalse(TEXT("七对开关关闭时不可按七对胡"), UMahjongHuChecker::CanHu(QiDui, false));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongActionCheckTest, "GuiyangMahjong.Rules.PengGang", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongActionCheckTest::RunTest(const FString& Parameters)
{
    FMahjongHand Hand = MahjongTest::MakeHand({0,0,0, 4,4,4,4, 9,10, 11,12, 13,14,15});
    const FMahjongTile Discard = MahjongTest::MakeTile(0, 100);
    TestTrue(TEXT("两张同牌可碰"), UMahjongChiPengChecker::CanPeng(Hand, Discard));
    TestTrue(TEXT("三张同牌可明杠"), UMahjongGangChecker::CanMingGang(Hand, Discard));
    TestTrue(TEXT("四张同牌可暗杠"), UMahjongGangChecker::FindAnGangRuleIndices(Hand).Contains(4));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongSpecialJiRuleTest, "GuiyangMahjong.Rules.SpecialJiConfig", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongSpecialJiRuleTest::RunTest(const FString& Parameters)
{
    FMahjongRuleConfig Config;
    Config.WuGuJiValue = 3;
    Config.JiCountingScope = EMahjongJiCountingScope::HandOnly;
    FMahjongHand Hand = MahjongTest::MakeHand({25, 0,1,2,3,4,5,6,7,8,10,11,12,13});
    FMahjongMeld Meld;
    Meld.Type = EMahjongMeldType::Peng;
    Meld.Tiles = { MahjongTest::MakeTile(25, 100), MahjongTest::MakeTile(25, 101), MahjongTest::MakeTile(25, 102) };
    Hand.Melds.Add(Meld);
    TestTrue(TEXT("Eight dots is WuGu Ji"), UGuiyangJiCalculator::IsWuGuJi(Hand.Tiles[0]));
    TestEqual(TEXT("Hand-only scope ignores meld WuGu Ji"),
        UGuiyangJiCalculator::CountJiUnits(Hand, FMahjongTile(), Config), 3);
    Config.JiCountingScope = EMahjongJiCountingScope::HandAndMeld;
    TestEqual(TEXT("Hand-and-meld scope counts configured WuGu units"),
        UGuiyangJiCalculator::CountJiUnits(Hand, FMahjongTile(), Config), 12);
    const FGuiyangRuleSnapshot FirstSnapshot = UGuiyangRuleSnapshotLibrary::CreateSnapshot(Config);
    Config.WuGuJiValue = 4;
    const FGuiyangRuleSnapshot SecondSnapshot = UGuiyangRuleSnapshotLibrary::CreateSnapshot(Config);
    TestNotEqual(TEXT("Special Ji values participate in rule hash"), FirstSnapshot.RuleHash, SecondSnapshot.RuleHash);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongSpecialJiEventTest, "GuiyangMahjong.Table.ChongFengAndZeRenJi", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongSpecialJiEventTest::RunTest(const FString& Parameters)
{
    TArray<FMahjongSeatInfo> Seats;
    Seats.SetNum(4);
    for (int32 Seat = 0; Seat < 4; ++Seat)
    {
        Seats[Seat].SeatIndex = Seat;
        Seats[Seat].PlayerId = FString::Printf(TEXT("special-ji-p%d"), Seat);
        Seats[Seat].bOccupied = true;
        Seats[Seat].Score = 100;
    }
    FMahjongRuleConfig Config;
    Config.JiScore = 2;
    Config.ChongFengJiValue = 2;
    Config.ZeRenJiValue = 3;
    UMahjongTableEngine* Engine = NewObject<UMahjongTableEngine>();
    FString Error;
    TestTrue(TEXT("Special Ji table starts"), Engine->StartRound(
        UGuiyangRuleSnapshotLibrary::CreateSnapshot(Config), Seats, 0, 105, Error));

    Engine->SetHandForServerTest(0, MahjongTest::MakeHand({9,0,1,2,3,4,5,6,7,8,10,11,12,13}));
    Engine->SetHandForServerTest(1, MahjongTest::MakeHand({9,9,0,2,4,6,8,10,12,14,16,18,20}));
    const FMahjongHand NoHu = MahjongTest::MakeHand({6,8,10,12,14,16,18,20,22,24,26,5,7});
    Engine->SetHandForServerTest(2, NoHu);
    Engine->SetHandForServerTest(3, NoHu);

    FMahjongActionRequest Play;
    Play.Type = EMahjongActionType::Play;
    Play.RoundId = Engine->GetPublicState().RoundId;
    Play.TurnId = Engine->GetPublicState().TurnId;
    Play.TargetTileId = 0;
    Play.ClientSequence = 1;
    TestTrue(TEXT("First basic Ji discard succeeds"), Engine->SubmitPlayTile(0, Play).bSuccess);
    TestEqual(TEXT("First Ji discard records ChongFeng event"), Engine->GetPublicState().JiEvents.Num(), 1);
    TestEqual(TEXT("ChongFeng event belongs to discarder"), Engine->GetPublicState().JiEvents[0].ActorSeat, 0);

    for (int32 Seat = 2; Seat < 4; ++Seat)
    {
        if (Engine->GetAvailableActions(Seat).IsEmpty()) continue;
        FMahjongActionRequest Pass;
        Pass.Type = EMahjongActionType::Pass;
        Pass.RoundId = Engine->GetPublicState().RoundId;
        Pass.TurnId = Engine->GetPublicState().TurnId;
        Pass.ClientSequence = 1;
        Engine->SubmitReaction(Seat, Pass);
    }
    FMahjongActionRequest PengRequest;
    PengRequest.Type = EMahjongActionType::Peng;
    PengRequest.RoundId = Engine->GetPublicState().RoundId;
    PengRequest.TurnId = Engine->GetPublicState().TurnId;
    PengRequest.ClientSequence = 1;
    TestTrue(TEXT("Other player claims first Ji by Peng"), Engine->SubmitReaction(1, PengRequest).bSuccess);
    TestEqual(TEXT("Peng creates ZeRen event"), Engine->GetPublicState().JiEvents.Num(), 2);
    TestEqual(TEXT("ZeRen event targets discarder"), Engine->GetPublicState().JiEvents[1].TargetSeat, 0);

    FMahjongPrivatePlayerState Claimant;
    Engine->GetPrivateState(1, Claimant);
    Claimant.Hand.Tiles = MahjongTest::MakeHand({0,1,2, 3,4,5, 18,18,18, 13,13}).Tiles;
    Engine->SetHandForServerTest(1, Claimant.Hand);
    FMahjongActionRequest Hu;
    Hu.Type = EMahjongActionType::Hu;
    Hu.RoundId = Engine->GetPublicState().RoundId;
    Hu.TurnId = Engine->GetPublicState().TurnId;
    Hu.ClientSequence = 2;
    TestTrue(TEXT("Claimant self draw settles special Ji"), Engine->SubmitTurnAction(1, Hu).bSuccess);
    FMahjongSettlementResult Settlement;
    Engine->GetSettlementResult(Settlement);
    TestEqual(TEXT("Settlement carries both special Ji events"), Settlement.JiEvents.Num(), 2);
    TestEqual(TEXT("Responsibility payer receives configured negative delta"),
        Settlement.PlayerResults[0].SpecialJiScoreDelta, -6);
    TestEqual(TEXT("Responsibility claimant receives configured positive delta"),
        Settlement.PlayerResults[1].SpecialJiScoreDelta, 6);
    int32 TotalDelta = 0;
    for (const FMahjongPlayerScoreResult& Player : Settlement.PlayerResults) TotalDelta += Player.TotalDelta;
    TestEqual(TEXT("Special Ji settlement remains zero sum"), TotalDelta, 0);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongJiTest, "GuiyangMahjong.Rules.Ji", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongJiTest::RunTest(const FString& Parameters)
{
    const FMahjongTile OneBamboo = MahjongTest::MakeTile(9, 1);
    const FMahjongTile NineCharacters = MahjongTest::MakeTile(8, 2);
    TestTrue(TEXT("幺鸡必须识别一条"), UGuiyangJiCalculator::IsBasicJi(OneBamboo));
    TestEqual(TEXT("九万翻鸡循环到一万"), UGuiyangJiCalculator::GetFlippedJiRuleIndex(NineCharacters), 0);
    const FMahjongHand Hand = MahjongTest::MakeHand({9,9,0,0,1,2,3,4,5,6,7,8,27,27});
    TestEqual(TEXT("两张幺鸡加两张翻鸡"), UGuiyangJiCalculator::CountJi(Hand, NineCharacters), 4);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongScoreTest, "GuiyangMahjong.Rules.ScoreZeroSum", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongScoreTest::RunTest(const FString& Parameters)
{
    FMahjongRuleConfig Config;
    const TArray<int32> JiCounts = {1, 2, 0, 3};
    const TArray<int32> GangDeltas = {0, 0, 0, 0};
    const TArray<int32> Scores = {100, 100, 100, 100};
    const FMahjongSettlementResult Result = UMahjongScoreCalculator::CalculateWin(1, 3, false, JiCounts, GangDeltas, Scores, Config);
    int32 TotalDelta = 0;
    for (const FMahjongPlayerScoreResult& Player : Result.PlayerResults) TotalDelta += Player.TotalDelta;
    TestEqual(TEXT("四名玩家总分变化必须为零"), TotalDelta, 0);
    TestTrue(TEXT("赢家基础分必须增加"), Result.PlayerResults[1].BaseScoreDelta > 0);
    TestTrue(TEXT("放炮玩家基础分必须减少"), Result.PlayerResults[3].BaseScoreDelta < 0);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongMultiScoreTest, "GuiyangMahjong.Rules.MultiWinScoreZeroSum", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongMultiScoreTest::RunTest(const FString& Parameters)
{
    const TArray<int32> JiCounts = {1, 2, 0, 3};
    const TArray<int32> GangDeltas = {0, 0, 0, 0};
    const TArray<int32> Scores = {100, 100, 100, 100};
    const FMahjongSettlementResult Result = UMahjongScoreCalculator::CalculateWins(
        {1, 2}, 3, false, JiCounts, GangDeltas, Scores, FMahjongRuleConfig());
    int32 TotalDelta = 0;
    for (const FMahjongPlayerScoreResult& Player : Result.PlayerResults) TotalDelta += Player.TotalDelta;
    TestEqual(TEXT("Multi-win settlement remains zero sum"), TotalDelta, 0);
    TestEqual(TEXT("Multi-win settlement keeps both winners"), Result.WinningSeats.Num(), 2);
    TestTrue(TEXT("First winner gains base score"), Result.PlayerResults[1].BaseScoreDelta > 0);
    TestTrue(TEXT("Second winner gains base score"), Result.PlayerResults[2].BaseScoreDelta > 0);
    return true;
}

#endif
