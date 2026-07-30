#include "MahjongCoreTestSupport.h"

#if WITH_DEV_AUTOMATION_TESTS

/**
 * 覆盖密码房、快速开始、四人准备和多局房间生命周期。
 * 保持原自动化测试路径和断言不变，失败由 Unreal Automation Framework 汇总。
 */
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongPasswordRoomTest, "GuiyangMahjong.Room.PasswordSecurityAndLifecycle", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongPasswordRoomTest::RunTest(const FString& Parameters)
{
    UGuiyangRoomManager* Manager = NewObject<UGuiyangRoomManager>();
    FMahjongCreateRoomRequest CreateRequest;
    CreateRequest.bEnablePassword = true;
    CreateRequest.Password = TEXT("628628");
    FMahjongRoomState State;
    EMahjongRoomError Error = EMahjongRoomError::None;
    TestTrue(TEXT("合法密码房必须创建成功"), Manager->CreateRoom(TEXT("owner-1"), TEXT("房主"), CreateRequest, State, Error));
    TestEqual(TEXT("房间号固定为 6 位"), State.RoomInfo.RoomId.Len(), 6);
    TestTrue(TEXT("房间号只能包含数字"), State.RoomInfo.RoomId.IsNumeric());
    TestTrue(TEXT("公开状态只能暴露密码保护标志"), State.RoomInfo.bPasswordProtected);
    TestTrue(TEXT("房间必须锁定有效规则快照"), UGuiyangRuleSnapshotLibrary::VerifySnapshot(State.RuleSnapshot));

    FMahjongJoinRoomRequest WrongJoin;
    WrongJoin.RoomCode = State.RoomInfo.RoomId;
    WrongJoin.Password = TEXT("111111");
    for (int32 Attempt = 0; Attempt < 4; ++Attempt)
    {
        TestFalse(TEXT("错误密码不得加入房间"), Manager->JoinRoom(TEXT("attacker"), TEXT("测试玩家"), WrongJoin, State, Error));
        TestEqual(TEXT("前四次错误密码返回 WrongPassword"), Error, EMahjongRoomError::WrongPassword);
    }
    TestFalse(TEXT("第五次错误密码触发锁定"), Manager->JoinRoom(TEXT("attacker"), TEXT("测试玩家"), WrongJoin, State, Error));
    TestEqual(TEXT("密码爆破限制必须生效"), Error, EMahjongRoomError::TooManyPasswordAttempts);
    WrongJoin.Password = TEXT("628628");
    TestFalse(TEXT("锁定窗口内即使密码正确也不得加入"), Manager->JoinRoom(TEXT("attacker"), TEXT("测试玩家"), WrongJoin, State, Error));
    TestEqual(TEXT("锁定窗口返回 TooManyPasswordAttempts"), Error, EMahjongRoomError::TooManyPasswordAttempts);

    FMahjongJoinRoomRequest ValidJoin;
    ValidJoin.RoomCode = State.RoomInfo.RoomId;
    ValidJoin.Password = TEXT("628628");
    TestTrue(TEXT("另一账号使用正确密码可加入"), Manager->JoinRoom(TEXT("player-2"), TEXT("玩家二"), ValidJoin, State, Error));
    TestTrue(TEXT("房主离开前房主标识存在"), State.Seats[0].bOwner);
    TestTrue(TEXT("房主可在开局前离开"), Manager->LeaveRoom(TEXT("owner-1"), State, Error));
    TestEqual(TEXT("房主离开后所有权转移"), State.RoomInfo.OwnerPlayerId, FString(TEXT("player-2")));
    TestTrue(TEXT("最后玩家离开会销毁房间"), Manager->LeaveRoom(TEXT("player-2"), State, Error));
    TestEqual(TEXT("空房必须被清理"), Manager->GetRoomCount(), 0);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongQuickStartRoomTest, "GuiyangMahjong.Room.QuickStartMatchmaking", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongQuickStartRoomTest::RunTest(const FString& Parameters)
{
    UGuiyangRoomManager* Manager = NewObject<UGuiyangRoomManager>();
    FMahjongRoomState State;
    EMahjongRoomError Error = EMahjongRoomError::None;
    TestTrue(TEXT("首名快速开始玩家创建公开房"), Manager->QuickStart(TEXT("quick-p0"), TEXT("玩家0"), State, Error));
    const FString FirstRoomCode = State.RoomInfo.RoomId;
    TestEqual(TEXT("首个快速房仅有一名玩家"), State.Seats.FilterByPredicate(
        [](const FMahjongSeatInfo& Seat) { return Seat.bOccupied; }).Num(), 1);

    for (int32 Index = 1; Index < 4; ++Index)
    {
        TestTrue(TEXT("后续快速开始玩家加入现有公开房"), Manager->QuickStart(
            FString::Printf(TEXT("quick-p%d"), Index), FString::Printf(TEXT("玩家%d"), Index), State, Error));
        TestEqual(TEXT("快速开始必须复用同一房间"), State.RoomInfo.RoomId, FirstRoomCode);
    }
    TestEqual(TEXT("四名玩家匹配后房间已满"), State.Seats.FilterByPredicate(
        [](const FMahjongSeatInfo& Seat) { return Seat.bOccupied; }).Num(), 4);

    TestTrue(TEXT("满房后新玩家快速开始会创建新房"), Manager->QuickStart(
        TEXT("quick-p4"), TEXT("玩家4"), State, Error));
    TestNotEqual(TEXT("新快速房房间号不同"), State.RoomInfo.RoomId, FirstRoomCode);
    TestEqual(TEXT("快速匹配创建两个房间"), Manager->GetRoomCount(), 2);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongRoomReadyTest, "GuiyangMahjong.Room.FourPlayersReady", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongRoomReadyTest::RunTest(const FString& Parameters)
{
    UGuiyangRoomManager* Manager = NewObject<UGuiyangRoomManager>();
    FMahjongCreateRoomRequest CreateRequest;
    FMahjongRoomState State;
    EMahjongRoomError Error = EMahjongRoomError::None;
    TestTrue(TEXT("公开房创建成功"), Manager->CreateRoom(TEXT("p0"), TEXT("玩家0"), CreateRequest, State, Error));
    const FString RoomCode = State.RoomInfo.RoomId;
    for (int32 Index = 1; Index < 4; ++Index)
    {
        FMahjongJoinRoomRequest Join;
        Join.RoomCode = RoomCode;
        TestTrue(TEXT("四人房加入成功"), Manager->JoinRoom(FString::Printf(TEXT("p%d"), Index), FString::Printf(TEXT("玩家%d"), Index), Join, State, Error));
    }
    TestFalse(TEXT("满员但未准备不得启动"), State.bGameStarting);
    for (int32 Index = 0; Index < 4; ++Index)
    {
        TestTrue(TEXT("玩家切换准备成功"), Manager->ToggleReady(FString::Printf(TEXT("p%d"), Index), State, Error));
    }
    TestTrue(TEXT("四人全部准备后进入启动状态"), State.bGameStarting);
    TestEqual(TEXT("生命周期必须进入 Starting"), State.Lifecycle, EMahjongRoomLifecycle::Starting);
    TestFalse(TEXT("启动后不得取消准备"), Manager->ToggleReady(TEXT("p0"), State, Error));
    TestEqual(TEXT("启动后准备请求返回已开局"), Error, EMahjongRoomError::GameAlreadyStarted);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongMultiRoundRoomTest, "GuiyangMahjong.Room.MultiRoundLifecycle", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongMultiRoundRoomTest::RunTest(const FString& Parameters)
{
    UGuiyangRoomManager* Manager = NewObject<UGuiyangRoomManager>();
    FMahjongCreateRoomRequest Create;
    Create.RoundCount = 2;
    FMahjongRoomState State;
    EMahjongRoomError Error = EMahjongRoomError::None;
    TestTrue(TEXT("Multi-round room is created"), Manager->CreateRoom(TEXT("round-p0"), TEXT("P0"), Create, State, Error));
    const FString RoomCode = State.RoomInfo.RoomId;
    TestEqual(TEXT("Owner is first dealer"), State.RoomInfo.DealerSeat, 0);
    for (int32 Seat = 1; Seat < 4; ++Seat)
    {
        FMahjongJoinRoomRequest Join;
        Join.RoomCode = RoomCode;
        TestTrue(TEXT("Player joins multi-round room"), Manager->JoinRoom(
            FString::Printf(TEXT("round-p%d"), Seat), FString::Printf(TEXT("P%d"), Seat), Join, State, Error));
    }
    for (int32 Seat = 0; Seat < 4; ++Seat)
        TestTrue(TEXT("Player readies for first round"), Manager->ToggleReady(FString::Printf(TEXT("round-p%d"), Seat), State, Error));
    TestEqual(TEXT("Four ready players start first round"), State.Lifecycle, EMahjongRoomLifecycle::Starting);
    TestTrue(TEXT("Room enters first playing round"), Manager->BeginPlaying(RoomCode, State, Error));
    TestEqual(TEXT("Current round increments to one"), State.RoomInfo.CurrentRound, 1);
    TestTrue(TEXT("Active player can be marked disconnected"), Manager->MarkDisconnected(TEXT("round-p1"), State, Error));
    TestFalse(TEXT("Disconnected seat is retained but offline"), State.Seats[1].bOnline);
    FString RetainedRoomCode;
    TestTrue(TEXT("Disconnected player keeps room mapping"), Manager->GetPlayerRoomCode(TEXT("round-p1"), RetainedRoomCode));
    int32 RemainingReconnectSeconds = 0;
    TestTrue(TEXT("Player reconnects within retention window"), Manager->ReconnectPlayer(
        TEXT("round-p1"), State, RemainingReconnectSeconds, Error));
    TestTrue(TEXT("Reconnected seat becomes online"), State.Seats[1].bOnline);
    TestTrue(TEXT("Reconnect snapshot reports positive remaining time"), RemainingReconnectSeconds > 0);

    FMahjongSettlementResult Settlement;
    Settlement.WinnerSeat = 2;
    Settlement.WinningSeats = { 2 };
    Settlement.PlayerResults.SetNum(4);
    const int32 Deltas[] = { -2, -2, 6, -2 };
    for (int32 Seat = 0; Seat < 4; ++Seat)
    {
        Settlement.PlayerResults[Seat].SeatIndex = Seat;
        Settlement.PlayerResults[Seat].TotalDelta = Deltas[Seat];
    }
    FMahjongSettlementResult InvalidSettlement = Settlement;
    InvalidSettlement.PlayerResults[0].TotalDelta = 10;
    TestFalse(TEXT("Non-zero-sum settlement is rejected"), Manager->FinishRound(RoomCode, InvalidSettlement, State, Error));
    TestTrue(TEXT("First round settlement is committed"), Manager->FinishRound(RoomCode, Settlement, State, Error));
    TestEqual(TEXT("Non-final round waits for next-round confirmation"), State.Lifecycle, EMahjongRoomLifecycle::WaitingNextRound);
    TestEqual(TEXT("Winner becomes next dealer"), State.RoomInfo.DealerSeat, 2);
    TestEqual(TEXT("Winner accumulated score is stored"), State.Seats[2].Score, 6);
    TestEqual(TEXT("Loser accumulated score is stored"), State.Seats[0].Score, -2);
    TestFalse(TEXT("Same round cannot be settled twice"), Manager->FinishRound(RoomCode, Settlement, State, Error));

    for (int32 Seat = 0; Seat < 4; ++Seat)
    {
        TestTrue(TEXT("Player confirms next round"), Manager->RequestNextRound(
            FString::Printf(TEXT("round-p%d"), Seat), State, Error));
    }
    TestEqual(TEXT("Four confirmations enter starting"), State.Lifecycle, EMahjongRoomLifecycle::Starting);
    TestTrue(TEXT("Room enters second playing round"), Manager->BeginPlaying(RoomCode, State, Error));
    TestEqual(TEXT("Current round increments to two"), State.RoomInfo.CurrentRound, 2);

    FMahjongSettlementResult DrawSettlement;
    DrawSettlement.bDrawGame = true;
    DrawSettlement.PlayerResults.SetNum(4);
    for (int32 Seat = 0; Seat < 4; ++Seat) DrawSettlement.PlayerResults[Seat].SeatIndex = Seat;
    TestTrue(TEXT("Final draw settlement is committed"), Manager->FinishRound(RoomCode, DrawSettlement, State, Error));
    TestEqual(TEXT("Configured rounds end in final settlement"), State.Lifecycle, EMahjongRoomLifecycle::Settlement);
    TestEqual(TEXT("Draw keeps dealer by default"), State.RoomInfo.DealerSeat, 2);
    TestEqual(TEXT("Accumulated score survives final round"), State.Seats[2].Score, 6);
    const FMahjongFinalSettlementResult FinalResult = UGuiyangRoomManager::BuildFinalSettlement(State);
    TestTrue(TEXT("Final settlement has stable match id"), !FinalResult.MatchId.IsEmpty());
    TestEqual(TEXT("Final settlement contains completed rounds"), FinalResult.CompletedRounds, 2);
    TestEqual(TEXT("Final settlement contains four ranked players"), FinalResult.Players.Num(), 4);
    TestEqual(TEXT("Highest score player ranks first"), FinalResult.Players[0].SeatIndex, 2);
    TestEqual(TEXT("Equal scores use seat order for stable ranking"), FinalResult.Players[1].SeatIndex, 0);
    TestEqual(TEXT("First result has rank one"), FinalResult.Players[0].Rank, 1);
    TestFalse(TEXT("Finished room rejects another round"), Manager->RequestNextRound(TEXT("round-p0"), State, Error));
    return true;
}


#endif

