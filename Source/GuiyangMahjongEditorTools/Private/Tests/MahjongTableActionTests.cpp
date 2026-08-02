#include "MahjongCoreTestSupport.h"

#if WITH_DEV_AUTOMATION_TESTS

/**
 * 覆盖权威回合、超时、响应优先级、自摸、杠牌与抢杠胡动作状态机。
 * 保持原自动化测试路径和断言不变，失败由 Unreal Automation Framework 汇总。
 */
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongAuthoritativeTurnTest, "GuiyangMahjong.Table.AuthoritativeTurnAndReplayGuard", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongAuthoritativeTurnTest::RunTest(const FString& Parameters)
{
    TArray<FMahjongSeatInfo> Seats;
    Seats.SetNum(4);
    for (int32 Index = 0; Index < 4; ++Index)
    {
        Seats[Index].SeatIndex = Index;
        Seats[Index].PlayerId = FString::Printf(TEXT("table-p%d"), Index);
        Seats[Index].PlayerName = FString::Printf(TEXT("牌桌玩家%d"), Index);
        Seats[Index].bOccupied = true;
        Seats[Index].bOnline = true;
    }
    const FGuiyangRuleSnapshot Rules = UGuiyangRuleSnapshotLibrary::CreateSnapshot(FMahjongRuleConfig());
    UMahjongTableEngine* First = NewObject<UMahjongTableEngine>();
    UMahjongTableEngine* Second = NewObject<UMahjongTableEngine>();
    FString Error;
    TestTrue(TEXT("第一张牌桌应成功开局"), First->StartRound(Rules, Seats, 0, 20260716, Error));
    TestTrue(TEXT("第二张牌桌应成功开局"), Second->StartRound(Rules, Seats, 0, 20260716, Error));

    const FMahjongPublicTableState& Initial = First->GetPublicState();
    TestEqual(TEXT("开局阶段为庄家出牌"), Initial.Phase, EMahjongTablePhase::PlayerTurn);
    TestEqual(TEXT("庄家座位为 0"), Initial.CurrentTurnSeat, 0);
    TestEqual(TEXT("108 张牌发完 53 张后剩余 55 张"), Initial.RemainingTileCount, 55);
    TestEqual(TEXT("庄家公开牌数为 14"), Initial.Seats[0].HandTileCount, 14);
    for (int32 Seat = 1; Seat < 4; ++Seat) TestEqual(TEXT("闲家公开牌数为 13"), Initial.Seats[Seat].HandTileCount, 13);

    FMahjongPrivatePlayerState FirstDealer;
    FMahjongPrivatePlayerState SecondDealer;
    TestTrue(TEXT("可读取庄家私有快照"), First->GetPrivateState(0, FirstDealer));
    TestTrue(TEXT("可读取第二桌庄家私有快照"), Second->GetPrivateState(0, SecondDealer));
    TestEqual(TEXT("相同种子产生相同庄家手牌数量"), FirstDealer.Hand.Tiles.Num(), SecondDealer.Hand.Tiles.Num());
    for (int32 Index = 0; Index < FirstDealer.Hand.Tiles.Num(); ++Index)
        TestEqual(TEXT("相同种子必须产生相同牌序"), FirstDealer.Hand.Tiles[Index].UniqueId, SecondDealer.Hand.Tiles[Index].UniqueId);

    FMahjongActionRequest Play;
    Play.Type = EMahjongActionType::Play;
    Play.RoundId = Initial.RoundId;
    Play.TurnId = Initial.TurnId;
    Play.TargetTileId = FirstDealer.Hand.Tiles[0].UniqueId;
    Play.ClientSequence = 1;
    FMahjongActionRequest StaleStatePlay = Play;
    StaleStatePlay.ExpectedStateVersion = Initial.StateSequence + 1;
    TestFalse(TEXT("客户端基于错误状态版本生成的动作必须拒绝"),
        First->SubmitPlayTile(0, StaleStatePlay).bSuccess);
    Play.ExpectedStateVersion = Initial.StateSequence;
    const FMahjongActionResult Played = First->SubmitPlayTile(0, Play);
    TestTrue(TEXT("庄家合法出牌必须成功"), Played.bSuccess);
    TestFalse(TEXT("完全相同的请求不得重放"), First->SubmitPlayTile(0, Play).bSuccess);

    if (First->GetPublicState().Phase == EMahjongTablePhase::WaitingForAction)
    {
        for (int32 Seat = 1; Seat < 4 && First->GetPublicState().Phase == EMahjongTablePhase::WaitingForAction; ++Seat)
        {
            if (First->GetAvailableActions(Seat).IsEmpty()) continue;
            FMahjongActionRequest Pass;
            Pass.Type = EMahjongActionType::Pass;
            Pass.RoundId = First->GetPublicState().RoundId;
            Pass.TurnId = First->GetPublicState().TurnId;
            Pass.ClientSequence = 1;
            TestTrue(TEXT("反应窗口过牌必须成功"), First->SubmitReaction(Seat, Pass).bSuccess);
        }
    }

    const FMahjongPublicTableState& Advanced = First->GetPublicState();
    TestEqual(TEXT("无人声明后轮到下一座位"), Advanced.CurrentTurnSeat, 1);
    TestEqual(TEXT("下一家摸牌后持有 14 张"), Advanced.Seats[1].HandTileCount, 14);
    TestEqual(TEXT("轮转摸牌后牌墙剩余 54 张"), Advanced.RemainingTileCount, 54);
    TestEqual(TEXT("弃牌记录必须由服务端生成"), Advanced.Discards.Num(), 1);

    FMahjongPrivatePlayerState NextPlayer;
    First->GetPrivateState(1, NextPlayer);
    FMahjongActionRequest Forged;
    Forged.Type = EMahjongActionType::Play;
    Forged.RoundId = Advanced.RoundId;
    Forged.TurnId = Advanced.TurnId;
    Forged.TargetTileId = 999999;
    Forged.ClientSequence = 2;
    const int32 SequenceBefore = Advanced.ServerActionSequence;
    TestFalse(TEXT("伪造不属于手牌的牌 ID 必须被拒绝"), First->SubmitPlayTile(1, Forged).bSuccess);
    TestEqual(TEXT("拒绝请求不得推进服务端动作序号"), First->GetPublicState().ServerActionSequence, SequenceBefore);
    const int32 PreviousRoundId = First->GetPublicState().RoundId;
    TestTrue(TEXT("Same engine starts next round"), First->StartRound(Rules, Seats, 1, 20260717, Error));
    TestEqual(TEXT("Round id increases across rounds"), First->GetPublicState().RoundId, PreviousRoundId + 1);
    Forged.ClientSequence = 99;
    TestFalse(TEXT("Request from previous round is rejected"), First->SubmitPlayTile(1, Forged).bSuccess);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongActionTimeoutTest, "GuiyangMahjong.Table.AuthoritativeActionTimeout", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongActionTimeoutTest::RunTest(const FString& Parameters)
{
    TArray<FMahjongSeatInfo> Seats;
    Seats.SetNum(4);
    for (int32 Seat = 0; Seat < 4; ++Seat)
    {
        Seats[Seat].SeatIndex = Seat;
        Seats[Seat].PlayerId = FString::Printf(TEXT("timeout-p%d"), Seat);
        Seats[Seat].bOccupied = true;
    }
    const FGuiyangRuleSnapshot Rules = UGuiyangRuleSnapshotLibrary::CreateSnapshot(FMahjongRuleConfig());
    FString Error;
    UMahjongTableEngine* TurnEngine = NewObject<UMahjongTableEngine>();
    TestTrue(TEXT("Turn-timeout table starts"), TurnEngine->StartRound(Rules, Seats, 0, 106, Error));
    const FMahjongPublicTableState Initial = TurnEngine->GetPublicState();
    TestFalse(TEXT("Stale timeout token is rejected"), TurnEngine->ResolveActionTimeout(
        Initial.RoundId + 1, Initial.TurnId, Initial.Phase).bSuccess);
    TestEqual(TEXT("Stale timeout does not discard"), TurnEngine->GetPublicState().Discards.Num(), 0);
    TestTrue(TEXT("Current turn timeout auto-plays"), TurnEngine->ResolveActionTimeout(
        Initial.RoundId, Initial.TurnId, Initial.Phase).bSuccess);
    TestEqual(TEXT("Turn timeout creates exactly one discard"), TurnEngine->GetPublicState().Discards.Num(), 1);
    FMahjongPrivatePlayerState TimedOutPlayer;
    TurnEngine->GetPrivateState(0, TimedOutPlayer);
    TestEqual(TEXT("Private snapshot carries last accepted sequence"), TimedOutPlayer.LastAcceptedClientSequence, 0);

    UMahjongTableEngine* ReactionEngine = NewObject<UMahjongTableEngine>();
    TestTrue(TEXT("Reaction-timeout table starts"), ReactionEngine->StartRound(Rules, Seats, 0, 107, Error));
    ReactionEngine->SetHandForServerTest(0, MahjongTest::MakeHand({0,1,2,3,4,5,6,7,8,9,10,11,12,13}));
    ReactionEngine->SetHandForServerTest(1, MahjongTest::MakeHand({0,0,3,4,5,6,7,8,9,10,11,12,13}));
    const FMahjongHand NoHu = MahjongTest::MakeHand({6,8,10,12,14,16,18,20,22,24,26,5,7});
    ReactionEngine->SetHandForServerTest(2, NoHu);
    ReactionEngine->SetHandForServerTest(3, NoHu);
    FMahjongActionRequest Play;
    Play.Type = EMahjongActionType::Play;
    Play.RoundId = ReactionEngine->GetPublicState().RoundId;
    Play.TurnId = ReactionEngine->GetPublicState().TurnId;
    Play.TargetTileId = 0;
    Play.ClientSequence = 1;
    TestTrue(TEXT("Discard opens reaction timeout window"), ReactionEngine->SubmitPlayTile(0, Play).bSuccess);
    const FMahjongPublicTableState Waiting = ReactionEngine->GetPublicState();
    TestEqual(TEXT("Peng candidate keeps reaction window open"), Waiting.Phase, EMahjongTablePhase::WaitingForAction);
    TestEqual(TEXT("响应窗口期间公开轮转指示必须立即指向顺位下一家"), Waiting.CurrentTurnSeat, 1);
    TestTrue(TEXT("Reaction timeout auto-passes pending seats"), ReactionEngine->ResolveActionTimeout(
        Waiting.RoundId, Waiting.TurnId, Waiting.Phase).bSuccess);
    TestEqual(TEXT("Auto-pass advances to next player"), ReactionEngine->GetPublicState().CurrentTurnSeat, 1);
    TestEqual(TEXT("Auto-pass does not claim discard"), ReactionEngine->GetPublicState().Discards[0].bClaimed, false);
    TestFalse(TEXT("Expired reaction timeout cannot fire twice"), ReactionEngine->ResolveActionTimeout(
        Waiting.RoundId, Waiting.TurnId, Waiting.Phase).bSuccess);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongReactionPriorityTest, "GuiyangMahjong.Table.ReactionPriorityAndMultiHu", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongReactionPriorityTest::RunTest(const FString& Parameters)
{
    TArray<FMahjongSeatInfo> Seats;
    Seats.SetNum(4);
    for (int32 Index = 0; Index < 4; ++Index)
    {
        Seats[Index].SeatIndex = Index;
        Seats[Index].PlayerId = FString::Printf(TEXT("priority-p%d"), Index);
        Seats[Index].PlayerName = FString::Printf(TEXT("优先级玩家%d"), Index);
        Seats[Index].bOccupied = true;
    }
    UMahjongTableEngine* Engine = NewObject<UMahjongTableEngine>();
    const FGuiyangRuleSnapshot Rules = UGuiyangRuleSnapshotLibrary::CreateSnapshot(FMahjongRuleConfig());
    FString Error;
    TestTrue(TEXT("优先级测试牌桌开局成功"), Engine->StartRound(Rules, Seats, 0, 99, Error));

    FMahjongHand Discarder = MahjongTest::MakeHand({0,1,2,3,4,5,6,7,8,9,10,11,12,13});
    FMahjongHand PengPlayer = MahjongTest::MakeHand({0,0,3,4,5,6,7,8,9,10,11,12,13});
    const FMahjongHand WaitingHu = MahjongTest::MakeHand({1,2, 3,4,5, 9,10,11, 18,18,18, 31,31});
    TestTrue(TEXT("注入出牌者测试手牌"), Engine->SetHandForServerTest(0, Discarder));
    TestTrue(TEXT("注入碰牌候选手牌"), Engine->SetHandForServerTest(1, PengPlayer));
    TestTrue(TEXT("注入第一胡家手牌"), Engine->SetHandForServerTest(2, WaitingHu));
    TestTrue(TEXT("注入第二胡家手牌"), Engine->SetHandForServerTest(3, WaitingHu));

    FMahjongActionRequest Play;
    Play.Type = EMahjongActionType::Play;
    Play.RoundId = Engine->GetPublicState().RoundId;
    Play.TurnId = Engine->GetPublicState().TurnId;
    Play.TargetTileId = Discarder.Tiles[0].UniqueId;
    Play.ClientSequence = 1;
    TestTrue(TEXT("测试牌出牌成功"), Engine->SubmitPlayTile(0, Play).bSuccess);
    TestTrue(TEXT("座位1必须获得碰候选"), Engine->GetAvailableActions(1).ContainsByPredicate([](const FMahjongAction& A) { return A.Type == EMahjongActionType::Peng; }));
    TestTrue(TEXT("座位2必须获得胡候选"), Engine->GetAvailableActions(2).ContainsByPredicate([](const FMahjongAction& A) { return A.Type == EMahjongActionType::Hu; }));
    TestTrue(TEXT("座位3必须获得胡候选"), Engine->GetAvailableActions(3).ContainsByPredicate([](const FMahjongAction& A) { return A.Type == EMahjongActionType::Hu; }));

    for (const TPair<int32, EMahjongActionType> Response : {
        TPair<int32, EMahjongActionType>(1, EMahjongActionType::Peng),
        TPair<int32, EMahjongActionType>(2, EMahjongActionType::Hu),
        TPair<int32, EMahjongActionType>(3, EMahjongActionType::Hu) })
    {
        FMahjongActionRequest Reaction;
        Reaction.Type = Response.Value;
        Reaction.RoundId = Engine->GetPublicState().RoundId;
        Reaction.TurnId = Engine->GetPublicState().TurnId;
        Reaction.ClientSequence = 1;
        TestTrue(TEXT("声明动作必须成功记录"), Engine->SubmitReaction(Response.Key, Reaction).bSuccess);
    }
    TestEqual(TEXT("胡牌必须压过碰并进入结算"), Engine->GetPublicState().Phase, EMahjongTablePhase::Settlement);
    TestEqual(TEXT("默认一炮多响应保留两个胡家"), Engine->GetPublicState().WinningSeats.Num(), 2);
    TestTrue(TEXT("第一胡家在赢家列表"), Engine->GetPublicState().WinningSeats.Contains(2));
    TestTrue(TEXT("第二胡家在赢家列表"), Engine->GetPublicState().WinningSeats.Contains(3));
    TestFalse(TEXT("碰牌玩家不得成为赢家"), Engine->GetPublicState().WinningSeats.Contains(1));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongSelfDrawTest, "GuiyangMahjong.Table.AuthoritativeSelfDraw", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongSelfDrawTest::RunTest(const FString& Parameters)
{
    TArray<FMahjongSeatInfo> Seats;
    Seats.SetNum(4);
    for (int32 Index = 0; Index < 4; ++Index)
    {
        Seats[Index].SeatIndex = Index;
        Seats[Index].PlayerId = FString::Printf(TEXT("self-draw-p%d"), Index);
        Seats[Index].bOccupied = true;
        Seats[Index].Score = 100;
    }
    UMahjongTableEngine* Engine = NewObject<UMahjongTableEngine>();
    FString Error;
    TestTrue(TEXT("Self draw table starts"), Engine->StartRound(
        UGuiyangRuleSnapshotLibrary::CreateSnapshot(FMahjongRuleConfig()), Seats, 0, 101, Error));
    const FMahjongHand WinningHand = MahjongTest::MakeHand({0,1,2, 9,10,11, 18,18,18, 3,4,5, 13,13});
    TestTrue(TEXT("Inject winning hand"), Engine->SetHandForServerTest(0, WinningHand));
    TestTrue(TEXT("Server offers self draw"), Engine->GetAvailableActions(0).ContainsByPredicate(
        [](const FMahjongAction& Action) { return Action.Type == EMahjongActionType::Hu; }));

    FMahjongActionRequest Request;
    Request.Type = EMahjongActionType::Hu;
    Request.RoundId = Engine->GetPublicState().RoundId;
    Request.TurnId = Engine->GetPublicState().TurnId;
    Request.ClientSequence = 1;
    TestTrue(TEXT("Server accepts valid self draw"), Engine->SubmitTurnAction(0, Request).bSuccess);
    TestEqual(TEXT("Self draw enters settlement"), Engine->GetPublicState().Phase, EMahjongTablePhase::Settlement);
    FMahjongSettlementResult Settlement;
    TestTrue(TEXT("Self draw settlement is readable"), Engine->GetSettlementResult(Settlement));
    TestTrue(TEXT("Settlement marks self draw"), Settlement.bSelfDraw);
    TestEqual(TEXT("Dealer is self draw winner"), Settlement.WinnerSeat, 0);
    TestTrue(TEXT("Self draw winner gains base score"), Settlement.PlayerResults[0].BaseScoreDelta > 0);
    TestTrue(TEXT("Winning settlement flips a ji tile"), Settlement.FlippedJiTile.IsValid());
    TestEqual(TEXT("Settlement contains four ji counts"), Settlement.PlayerJiCounts.Num(), 4);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongConcealedGangTest, "GuiyangMahjong.Table.AuthoritativeConcealedGang", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongConcealedGangTest::RunTest(const FString& Parameters)
{
    TArray<FMahjongSeatInfo> Seats;
    Seats.SetNum(4);
    for (int32 Index = 0; Index < 4; ++Index)
    {
        Seats[Index].SeatIndex = Index;
        Seats[Index].PlayerId = FString::Printf(TEXT("gang-p%d"), Index);
        Seats[Index].bOccupied = true;
    }
    UMahjongTableEngine* Engine = NewObject<UMahjongTableEngine>();
    FString Error;
    TestTrue(TEXT("Concealed gang table starts"), Engine->StartRound(
        UGuiyangRuleSnapshotLibrary::CreateSnapshot(FMahjongRuleConfig()), Seats, 0, 102, Error));
    TestTrue(TEXT("Inject concealed gang hand"), Engine->SetHandForServerTest(
        0, MahjongTest::MakeHand({4,4,4,4, 0,1,2, 9,10,11, 18,19,20, 13})));
    const TArray<FMahjongAction> Actions = Engine->GetAvailableActions(0);
    const FMahjongAction* Gang = Actions.FindByPredicate(
        [](const FMahjongAction& Action) { return Action.Type == EMahjongActionType::AnGang; });
    TestNotNull(TEXT("Server offers concealed gang"), Gang);
    if (!Gang) return false;

    FMahjongActionRequest Request;
    Request.Type = EMahjongActionType::AnGang;
    Request.RoundId = Engine->GetPublicState().RoundId;
    Request.TurnId = Engine->GetPublicState().TurnId;
    Request.TargetTileId = Gang->TargetTile.UniqueId;
    Request.ClientSequence = 1;
    TestTrue(TEXT("Server accepts concealed gang"), Engine->SubmitTurnAction(0, Request).bSuccess);
    TestEqual(TEXT("Replacement tile keeps player turn"), Engine->GetPublicState().Phase, EMahjongTablePhase::PlayerTurn);
    TestEqual(TEXT("Concealed gang is in public meld list"), Engine->GetPublicState().PublicMelds.Num(), 1);
    TestFalse(TEXT("Public concealed gang hides every tile face"), Engine->GetPublicState().PublicMelds[0].Tiles.ContainsByPredicate(
        [](const FMahjongTile& Tile) { return Tile.IsValid(); }));
    TestEqual(TEXT("Replacement draw consumes one wall tile"), Engine->GetPublicState().RemainingTileCount, 54);
    TestEqual(TEXT("Four tiles become meld and one replacement remains"), Engine->GetPublicState().Seats[0].HandTileCount, 11);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongSupplementalGangTest, "GuiyangMahjong.Table.AuthoritativeSupplementalGang", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongSupplementalGangTest::RunTest(const FString& Parameters)
{
    TArray<FMahjongSeatInfo> Seats;
    Seats.SetNum(4);
    for (int32 Seat = 0; Seat < 4; ++Seat)
    {
        Seats[Seat].SeatIndex = Seat;
        Seats[Seat].PlayerId = FString::Printf(TEXT("bu-gang-p%d"), Seat);
        Seats[Seat].bOccupied = true;
    }
    FMahjongRuleConfig Config;
    Config.bEnableQiangGangHu = false;
    UMahjongTableEngine* Engine = NewObject<UMahjongTableEngine>();
    FString Error;
    TestTrue(TEXT("Supplemental gang table starts"), Engine->StartRound(
        UGuiyangRuleSnapshotLibrary::CreateSnapshot(Config), Seats, 0, 103, Error));

    FMahjongHand GangHand = MahjongTest::MakeHand({0,1,2,3,4,5,6,7,8,9,10});
    FMahjongMeld Peng;
    Peng.Type = EMahjongMeldType::Peng;
    Peng.FromSeat = 3;
    Peng.Tiles = { MahjongTest::MakeTile(0, 200), MahjongTest::MakeTile(0, 201), MahjongTest::MakeTile(0, 202) };
    GangHand.Melds.Add(Peng);
    TestTrue(TEXT("Inject supplemental gang hand"), Engine->SetHandForServerTest(0, GangHand));
    const FMahjongAction* Candidate = Engine->GetAvailableActions(0).FindByPredicate(
        [](const FMahjongAction& Action) { return Action.Type == EMahjongActionType::BuGang; });
    TestNotNull(TEXT("Server offers supplemental gang"), Candidate);
    if (!Candidate) return false;

    FMahjongActionRequest Request;
    Request.Type = EMahjongActionType::BuGang;
    Request.RoundId = Engine->GetPublicState().RoundId;
    Request.TurnId = Engine->GetPublicState().TurnId;
    Request.TargetTileId = Candidate->TargetTile.UniqueId;
    Request.ClientSequence = 1;
    TestTrue(TEXT("Server accepts supplemental gang"), Engine->SubmitTurnAction(0, Request).bSuccess);
    TestEqual(TEXT("Supplemental gang draws replacement"), Engine->GetPublicState().RemainingTileCount, 54);
    TestEqual(TEXT("Supplemental gang remains player turn"), Engine->GetPublicState().Phase, EMahjongTablePhase::PlayerTurn);
    FMahjongPrivatePlayerState PrivateState;
    Engine->GetPrivateState(0, PrivateState);
    TestEqual(TEXT("Private peng upgrades to supplemental gang"), PrivateState.Hand.Melds[0].Type, EMahjongMeldType::BuGang);
    TestEqual(TEXT("Public peng upgrades to supplemental gang"), Engine->GetPublicState().PublicMelds[0].Type, EMahjongMeldType::BuGang);
    TestEqual(TEXT("Public meld records owner seat"), Engine->GetPublicState().PublicMelds[0].OwnerSeat, 0);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongQiangGangHuTest, "GuiyangMahjong.Table.QiangGangHu", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongQiangGangHuTest::RunTest(const FString& Parameters)
{
    TArray<FMahjongSeatInfo> Seats;
    Seats.SetNum(4);
    for (int32 Seat = 0; Seat < 4; ++Seat)
    {
        Seats[Seat].SeatIndex = Seat;
        Seats[Seat].PlayerId = FString::Printf(TEXT("qiang-gang-p%d"), Seat);
        Seats[Seat].bOccupied = true;
    }
    UMahjongTableEngine* Engine = NewObject<UMahjongTableEngine>();
    FString Error;
    TestTrue(TEXT("Qiang gang table starts"), Engine->StartRound(
        UGuiyangRuleSnapshotLibrary::CreateSnapshot(FMahjongRuleConfig()), Seats, 0, 104, Error));

    FMahjongHand GangHand = MahjongTest::MakeHand({0,1,2,3,4,5,6,7,8,9,10});
    FMahjongMeld Peng;
    Peng.Type = EMahjongMeldType::Peng;
    Peng.FromSeat = 3;
    Peng.Tiles = { MahjongTest::MakeTile(0, 210), MahjongTest::MakeTile(0, 211), MahjongTest::MakeTile(0, 212) };
    GangHand.Melds.Add(Peng);
    const FMahjongHand WaitingHu = MahjongTest::MakeHand({1,2, 3,4,5, 9,10,11, 18,18,18, 13,13});
    const FMahjongHand NoHu = MahjongTest::MakeHand({6,8,10,12,14,16,18,20,22,24,26,5,7});
    Engine->SetHandForServerTest(0, GangHand);
    Engine->SetHandForServerTest(1, WaitingHu);
    Engine->SetHandForServerTest(2, NoHu);
    Engine->SetHandForServerTest(3, NoHu);
    const FMahjongAction* Candidate = Engine->GetAvailableActions(0).FindByPredicate(
        [](const FMahjongAction& Action) { return Action.Type == EMahjongActionType::BuGang; });
    TestNotNull(TEXT("Server offers robbable supplemental gang"), Candidate);
    if (!Candidate) return false;

    FMahjongActionRequest GangRequest;
    GangRequest.Type = EMahjongActionType::BuGang;
    GangRequest.RoundId = Engine->GetPublicState().RoundId;
    GangRequest.TurnId = Engine->GetPublicState().TurnId;
    GangRequest.TargetTileId = Candidate->TargetTile.UniqueId;
    GangRequest.ClientSequence = 1;
    TestTrue(TEXT("Supplemental gang declaration enters response"), Engine->SubmitTurnAction(0, GangRequest).bSuccess);
    TestEqual(TEXT("Qiang gang opens reaction window"), Engine->GetPublicState().Phase, EMahjongTablePhase::WaitingForAction);
    TestTrue(TEXT("Waiting player receives hu action"), Engine->GetAvailableActions(1).ContainsByPredicate(
        [](const FMahjongAction& Action) { return Action.Type == EMahjongActionType::Hu; }));

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
    FMahjongActionRequest Hu;
    Hu.Type = EMahjongActionType::Hu;
    Hu.RoundId = Engine->GetPublicState().RoundId;
    Hu.TurnId = Engine->GetPublicState().TurnId;
    Hu.ClientSequence = 1;
    TestTrue(TEXT("Server accepts qiang gang hu"), Engine->SubmitReaction(1, Hu).bSuccess);
    TestEqual(TEXT("Qiang gang hu enters settlement"), Engine->GetPublicState().Phase, EMahjongTablePhase::Settlement);
    FMahjongSettlementResult Settlement;
    Engine->GetSettlementResult(Settlement);
    TestEqual(TEXT("Gang declarer is loser"), Settlement.LoserSeat, 0);
    TestEqual(TEXT("Robbing player is winner"), Settlement.WinnerSeat, 1);
    TestTrue(TEXT("Server flips ji tile at settlement"), Settlement.FlippedJiTile.IsValid());
    TestEqual(TEXT("Public flip matches settlement"), Engine->GetPublicState().FlippedJiTile.UniqueId, Settlement.FlippedJiTile.UniqueId);
    TestEqual(TEXT("Settlement publishes four ji counts"), Settlement.PlayerJiCounts.Num(), 4);
    FMahjongPrivatePlayerState GangPrivate;
    Engine->GetPrivateState(0, GangPrivate);
    TestEqual(TEXT("Robbed meld remains peng"), GangPrivate.Hand.Melds[0].Type, EMahjongMeldType::Peng);
    TestFalse(TEXT("Robbed tile leaves declarer hand"), GangPrivate.Hand.Tiles.ContainsByPredicate(
        [](const FMahjongTile& Tile) { return Tile.UniqueId == 0; }));
    return true;
}


#endif
