#include "Misc/AutomationTest.h"

#include "Table/MahjongDeckManager.h"
#include "Rules/MahjongHuChecker.h"
#include "Rules/MahjongGangChecker.h"
#include "Rules/MahjongChiPengChecker.h"
#include "Rules/GuiyangJiCalculator.h"
#include "Rules/MahjongScoreCalculator.h"
#include "Rules/GuiyangRuleSnapshot.h"
#include "Room/GuiyangRoomManager.h"
#include "Table/MahjongTableEngine.h"
#include "Auth/GuiyangLoginSaveGame.h"
#include "Auth/GuiyangLoginSubsystem.h"
#include "History/MahjongMatchHistorySaveGame.h"
#include "History/GuiyangMatchHistorySubsystem.h"
#include "Engine/GameInstance.h"
#include "Engine/StaticMesh.h"
#include "Kismet/GameplayStatics.h"
#include "Network/MahjongNetworkTypes.h"
#include "UI/MobileMahjongHUDWidget.h"
#include "UI/MobileRoomWidget.h"
#include "UI/MobileReconnectOverlayWidget.h"
#include "Network/GuiyangReconnectSubsystem.h"
#include "UI/MobileRuleSummaryWidget.h"
#include "UI/MahjongTileVisualLibrary.h"
#include "UI/MahjongLocalSettings.h"
#include "UI/MobileSettingsWidget.h"
#include "UI/MahjongResponsiveScaleBox.h"
#include "UI/MahjongUIScalingRule.h"
#include "Game/GuiyangMahjongPlayerController.h"
#include "Game/Mahjong3DTableActor.h"
#include "Game/MahjongRoomCameraActor.h"
#include "Game/MahjongRoomPresentationActor.h"
#include "Settings/MahjongRoomPresentationSettings.h"
#include "CineCameraComponent.h"
#include "Components/ChildActorComponent.h"
#include "Components/DirectionalLightComponent.h"
#include "Components/RectLightComponent.h"
#include "Components/SkyLightComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/DirectionalLight.h"
#include "Engine/Level.h"
#include "Engine/SkyLight.h"
#include "Engine/World.h"
#include "UObject/UnrealType.h"
#include "Sound/SoundBase.h"
#if WITH_EDITOR
#include "WidgetBlueprint.h"
#include "Blueprint/WidgetTree.h"
#include "Components/Button.h"
#include "Components/Border.h"
#include "Components/CanvasPanelSlot.h"
#include "Components/CheckBox.h"
#include "Components/Image.h"
#include "Components/Slider.h"
#include "Components/TextBlock.h"
#endif

#if WITH_DEV_AUTOMATION_TESTS

namespace MahjongTest
{
    static FMahjongTile MakeTile(const int32 RuleIndex, const int32 UniqueId)
    {
        FMahjongTile Tile;
        Tile.UniqueId = UniqueId;
        if (RuleIndex < 27)
        {
            Tile.Type = EMahjongTileType::Number;
            Tile.Suit = RuleIndex < 9 ? EMahjongSuit::Characters : RuleIndex < 18 ? EMahjongSuit::Bamboo : EMahjongSuit::Dots;
            Tile.Rank = RuleIndex % 9 + 1;
        }
        else
        {
            static const EMahjongTileType Honors[] = { EMahjongTileType::East, EMahjongTileType::South, EMahjongTileType::West,
                EMahjongTileType::North, EMahjongTileType::RedDragon, EMahjongTileType::GreenDragon, EMahjongTileType::WhiteDragon };
            Tile.Type = Honors[RuleIndex - 27];
            Tile.Suit = RuleIndex <= 30 ? EMahjongSuit::Winds : EMahjongSuit::Dragons;
        }
        return Tile;
    }

    static FMahjongHand MakeHand(std::initializer_list<int32> RuleIndices)
    {
        FMahjongHand Hand;
        int32 UniqueId = 0;
        for (const int32 Index : RuleIndices) Hand.Tiles.Add(MakeTile(Index, UniqueId++));
        return Hand;
    }
}

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

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongRuleSummaryTest, "GuiyangMahjong.UI.RuleSummaryConsistency", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongRuleSummaryTest::RunTest(const FString& Parameters)
{
    FMahjongRuleConfig Config;
    Config.TileSetMode = EMahjongTileSetMode::Standard136;
    Config.bEnableChongFengJi = false;
    Config.bEnableQiDui = false;
    Config.BaseScore = 3;
    Config.TurnTimeoutSeconds = 20;
    const FGuiyangRuleSnapshot Snapshot = UGuiyangRuleSnapshotLibrary::CreateSnapshot(Config);
    const FString Summary = UMobileRuleSummaryWidget::BuildSummaryText(Snapshot, 8, true);
    TestTrue(TEXT("贵阳规则摘要必须固定显示 108 张牌制"),
        Summary.Contains(TEXT("108")) && !Summary.Contains(TEXT("136")));
    TestTrue(TEXT("规则摘要显示局数和密码房"), Summary.Contains(TEXT("8 局")) && Summary.Contains(TEXT("密码房")));
    TestTrue(TEXT("规则摘要显示关闭的冲锋鸡"), Summary.Contains(TEXT("冲锋鸡关")));
    TestTrue(TEXT("规则摘要显示关闭的七对"), Summary.Contains(TEXT("七对关")));
    TestTrue(TEXT("规则摘要显示分数和超时"), Summary.Contains(TEXT("底分 3")) && Summary.Contains(TEXT("出牌 20 秒")));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongHudSeatMappingTest, "GuiyangMahjong.UI.HudSeatMapping", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongHudSeatMappingTest::RunTest(const FString& Parameters)
{
    TestEqual(TEXT("本地座位固定映射到底部"), UMobileMahjongHUDWidget::GetRelativeSeatIndex(2, 2), 0);
    TestEqual(TEXT("本地玩家下家映射到右侧"), UMobileMahjongHUDWidget::GetRelativeSeatIndex(3, 2), 1);
    TestEqual(TEXT("本地玩家对家映射到顶部"), UMobileMahjongHUDWidget::GetRelativeSeatIndex(0, 2), 2);
    TestEqual(TEXT("本地玩家上家映射到左侧"), UMobileMahjongHUDWidget::GetRelativeSeatIndex(1, 2), 3);
    TestEqual(TEXT("非法座位不会访问 UI 数组"), UMobileMahjongHUDWidget::GetRelativeSeatIndex(INDEX_NONE, 2), INDEX_NONE);
    TestEqual(TEXT("当前登录玩家必须映射到南方座位"),
        UMobileRoomWidget::GetAbsoluteSeatForRelativePosition(0, 2), 2);
    TestEqual(TEXT("南方玩家下家必须映射到东方座位"),
        UMobileRoomWidget::GetAbsoluteSeatForRelativePosition(1, 2), 3);
    TestEqual(TEXT("南方玩家对家必须映射到北方座位"),
        UMobileRoomWidget::GetAbsoluteSeatForRelativePosition(2, 2), 0);
    TestEqual(TEXT("南方玩家上家必须映射到西方座位"),
        UMobileRoomWidget::GetAbsoluteSeatForRelativePosition(3, 2), 1);
    TestEqual(TEXT("座位0看到绝对牌墙0位于南方"),
        AMahjong3DTableActor::GetRelativeWallSide(0, 0), 0);
    TestEqual(TEXT("座位1看到绝对牌墙0位于西方"),
        AMahjong3DTableActor::GetRelativeWallSide(0, 1), 3);
    TestEqual(TEXT("座位2看到绝对牌墙0位于北方"),
        AMahjong3DTableActor::GetRelativeWallSide(0, 2), 2);
    TestEqual(TEXT("座位3看到绝对牌墙0位于东方"),
        AMahjong3DTableActor::GetRelativeWallSide(0, 3), 1);
    TestEqual(TEXT("非法本地座位不得生成牌墙方位"),
        AMahjong3DTableActor::GetRelativeWallSide(0, INDEX_NONE), INDEX_NONE);
    constexpr int32 ReviewBreakSide = 1;
    constexpr int32 ReviewBreakStack = 6;
    constexpr int32 ReviewRemaining = 55;
    // Clockwise segments are South 28, West 26, North 28, East 26.
    constexpr int32 ReviewDrawStart = 28 + 26 + 28 + ReviewBreakStack * 2;
    int32 VisibleWallSlots = 0;
    for (int32 PhysicalIndex = 0; PhysicalIndex < 108; ++PhysicalIndex)
    {
        VisibleWallSlots += AMahjong3DTableActor::IsWallPhysicalSlotRemaining(
            PhysicalIndex, ReviewRemaining, ReviewBreakSide, ReviewBreakStack) ? 1 : 0;
    }
    TestEqual(TEXT("展示层可见牌墙槽位必须等于服务端剩余牌数"),
        VisibleWallSlots, ReviewRemaining);
    TestFalse(TEXT("开门处第一张已抓牌必须形成缺口"),
        AMahjong3DTableActor::IsWallPhysicalSlotRemaining(
            ReviewDrawStart % 108, ReviewRemaining, ReviewBreakSide, ReviewBreakStack));
    TestFalse(TEXT("顺时针第53张抓牌必须连续消耗"),
        AMahjong3DTableActor::IsWallPhysicalSlotRemaining(
            (ReviewDrawStart + 52) % 108, ReviewRemaining, ReviewBreakSide, ReviewBreakStack));
    TestTrue(TEXT("顺时针下一张未抓牌必须继续保留"),
        AMahjong3DTableActor::IsWallPhysicalSlotRemaining(
            (ReviewDrawStart + 53) % 108, ReviewRemaining, ReviewBreakSide, ReviewBreakStack));
    TestEqual(TEXT("牌局阶段显示中文"), UMobileMahjongHUDWidget::GetPhaseDisplayText(
        EMahjongTablePhase::WaitingForAction), FString(TEXT("等待碰杠胡")));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongTileVisualMappingTest, "GuiyangMahjong.UI.TileVisualMapping", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongTileVisualMappingTest::RunTest(const FString& Parameters)
{
    FMahjongTile Tile;
    Tile.Type = EMahjongTileType::Number;
    Tile.Rank = 1;
    Tile.UniqueId = 1;

    int32 Column = INDEX_NONE;
    int32 Row = INDEX_NONE;
    Tile.Suit = EMahjongSuit::Characters;
    TestTrue(TEXT("一万必须使用 Mahjong50 高清图集"),
        UMahjongTileVisualLibrary::GetFaceTexturePath(Tile).Contains(TEXT("T_Mahjong50_FaceAtlas_BaseColor")));
    TestTrue(TEXT("一万必须映射到图集格"),
        UMahjongTileVisualLibrary::GetFaceAtlasCell(Tile, Column, Row));
    TestEqual(TEXT("一万图集列"), Column, 0);
    TestEqual(TEXT("一万图集行"), Row, 0);
    Tile.Suit = EMahjongSuit::Bamboo;
    TestTrue(TEXT("一条必须映射到图集格"),
        UMahjongTileVisualLibrary::GetFaceAtlasCell(Tile, Column, Row));
    TestEqual(TEXT("一条图集列"), Column, 0);
    TestEqual(TEXT("一条图集行"), Row, 1);
    Tile.Suit = EMahjongSuit::Dots;
    TestTrue(TEXT("一筒必须映射到图集格"),
        UMahjongTileVisualLibrary::GetFaceAtlasCell(Tile, Column, Row));
    TestEqual(TEXT("一筒图集列"), Column, 0);
    TestEqual(TEXT("一筒图集行"), Row, 2);

    Tile.Type = EMahjongTileType::East;
    TestFalse(TEXT("贵阳捉鸡麻将客户端不得映射字牌图集"),
        UMahjongTileVisualLibrary::GetFaceAtlasCell(Tile, Column, Row));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongReconnectPresentationTest, "GuiyangMahjong.UI.ReconnectPresentation", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongReconnectPresentationTest::RunTest(const FString& Parameters)
{
    TestEqual(TEXT("重连时限不得低于服务端下限"),
        UGuiyangReconnectSubsystem::ClampReconnectTimeoutSeconds(0), 15);
    TestEqual(TEXT("重连时限不得超过服务端上限"),
        UGuiyangReconnectSubsystem::ClampReconnectTimeoutSeconds(9999), 600);
    TestEqual(TEXT("正常重连时限保持不变"),
        UGuiyangReconnectSubsystem::ClampReconnectTimeoutSeconds(120), 120);
    TestEqual(TEXT("倒计时不会显示负数"),
        UMobileReconnectOverlayWidget::FormatRemainingTime(-5), FString(TEXT("剩余 0 秒")));
    TestEqual(TEXT("倒计时显示中文秒数"),
        UMobileReconnectOverlayWidget::FormatRemainingTime(12), FString(TEXT("剩余 12 秒")));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongIntegrationHookSecurityTest, "GuiyangMahjong.Security.IntegrationHooksDisabledByDefault", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongIntegrationHookSecurityTest::RunTest(const FString& Parameters)
{
    UGameInstance* GameInstance = NewObject<UGameInstance>();
    UGuiyangLoginSubsystem* Login = NewObject<UGuiyangLoginSubsystem>(GameInstance);
    TestFalse(TEXT("未显式启用命令行开关时不得注入集成会话"), Login->LoginForIntegrationTest(
        TEXT("integration-client-test"), TEXT("测试玩家"), TEXT("integration-session-token-disabled")));
    TestFalse(TEXT("被拒绝的集成会话不得变为有效登录"), Login->IsSessionValid());
    return true;
}

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

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongGuestLoginTest, "GuiyangMahjong.Auth.GuestLoginLifecycle", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongGuestLoginTest::RunTest(const FString& Parameters)
{
    static const FString SlotName = TEXT("GuiyangLoginSettings");
    UGameplayStatics::DeleteGameInSlot(SlotName, 0);
    UGuiyangLoginSaveGame* SeedSettings = Cast<UGuiyangLoginSaveGame>(UGameplayStatics::CreateSaveGameObject(UGuiyangLoginSaveGame::StaticClass()));
    UGameplayStatics::SaveGameToSlot(SeedSettings, SlotName, 0);

    UGameInstance* GameInstance = NewObject<UGameInstance>();
    GameInstance->Init();
    UGuiyangLoginSubsystem* Login = GameInstance->GetSubsystem<UGuiyangLoginSubsystem>();
    TestNotNull(TEXT("登录子系统必须可由 GameInstance 创建"), Login);
    if (Login)
    {
        Login->LoginAsGuest();
        TestEqual(TEXT("游客登录后状态必须为已登录"), Login->GetLoginState(), EGuiyangLoginState::LoggedIn);
        TestTrue(TEXT("游客会话必须有效"), Login->IsSessionValid());
        TestEqual(TEXT("Provider 必须为游客"), Login->GetCurrentProfile().Provider, EGuiyangLoginProvider::Guest);
        TestTrue(TEXT("PlayerId 必须生成"), !Login->GetCurrentProfile().PlayerId.IsEmpty());
        TestTrue(TEXT("内存 SessionToken 必须生成"), !Login->GetSessionTokenForNetwork().IsEmpty());

        Login->Logout();
        TestEqual(TEXT("退出后状态必须为未登录"), Login->GetLoginState(), EGuiyangLoginState::LoggedOut);
        TestFalse(TEXT("退出后会话必须失效"), Login->IsSessionValid());
        TestTrue(TEXT("退出后内存 SessionToken 必须清空"), Login->GetSessionTokenForNetwork().IsEmpty());
    }
    GameInstance->Shutdown();
    UGameplayStatics::DeleteGameInSlot(SlotName, 0);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongMatchHistoryTest, "GuiyangMahjong.History.FinalSettlementPersistence", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongMatchHistoryTest::RunTest(const FString& Parameters)
{
    static const FString SlotName = TEXT("GuiyangMatchHistory");
    UGameplayStatics::DeleteGameInSlot(SlotName, 0);

    UGameInstance* FirstInstance = NewObject<UGameInstance>();
    FirstInstance->Init();
    UGuiyangMatchHistorySubsystem* FirstHistory = FirstInstance->GetSubsystem<UGuiyangMatchHistorySubsystem>();
    TestNotNull(TEXT("Match history subsystem is available"), FirstHistory);
    FMahjongFinalSettlementResult Result;
    Result.MatchId = TEXT("match-history-test-1");
    Result.RoomId = TEXT("654321");
    Result.CompletedRounds = 4;
    FMahjongFinalPlayerResult Player;
    Player.Rank = 1;
    Player.SeatIndex = 0;
    Player.PlayerName = TEXT("Winner");
    Player.TotalScore = 18;
    Result.Players.Add(Player);
    TestTrue(TEXT("Final settlement is persisted"), FirstHistory && FirstHistory->RecordFinalSettlement(Result));
    TestFalse(TEXT("Duplicate match id is ignored"), FirstHistory && FirstHistory->RecordFinalSettlement(Result));
    TestEqual(TEXT("Only one history record exists"), FirstHistory ? FirstHistory->GetRecords().Num() : 0, 1);
    FirstInstance->Shutdown();

    UGameInstance* SecondInstance = NewObject<UGameInstance>();
    SecondInstance->Init();
    UGuiyangMatchHistorySubsystem* SecondHistory = SecondInstance->GetSubsystem<UGuiyangMatchHistorySubsystem>();
    TestEqual(TEXT("History reloads from SaveGame"), SecondHistory ? SecondHistory->GetRecords().Num() : 0, 1);
    if (SecondHistory)
    {
        TestEqual(TEXT("Reloaded history keeps match id"), SecondHistory->GetRecords()[0].MatchId, Result.MatchId);
        SecondHistory->ClearHistory();
    }
    SecondInstance->Shutdown();
    UGameplayStatics::DeleteGameInSlot(SlotName, 0);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongLoginPersistenceSecurityTest, "GuiyangMahjong.Security.LoginSaveContainsNoSecrets", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongLoginPersistenceSecurityTest::RunTest(const FString& Parameters)
{
    bool bContainsSecretField = false;
    for (TFieldIterator<FProperty> It(UGuiyangLoginSaveGame::StaticClass(), EFieldIteratorFlags::ExcludeSuper); It; ++It)
    {
        const FString Name = It->GetName();
        bContainsSecretField |= Name.Contains(TEXT("Token"), ESearchCase::IgnoreCase)
            || Name.Contains(TEXT("Secret"), ESearchCase::IgnoreCase)
            || Name.Contains(TEXT("Password"), ESearchCase::IgnoreCase);
    }
    TestFalse(TEXT("登录 SaveGame 不得声明 Token、Secret 或 Password 字段"), bContainsSecretField);
    bool bHistoryContainsSecretField = false;
    for (TFieldIterator<FProperty> It(UMahjongMatchHistorySaveGame::StaticClass(), EFieldIteratorFlags::ExcludeSuper); It; ++It)
    {
        const FString Name = It->GetName();
        bHistoryContainsSecretField |= Name.Contains(TEXT("Token"), ESearchCase::IgnoreCase)
            || Name.Contains(TEXT("Secret"), ESearchCase::IgnoreCase)
            || Name.Contains(TEXT("Password"), ESearchCase::IgnoreCase)
            || Name.Contains(TEXT("Hand"), ESearchCase::IgnoreCase);
    }
    TestFalse(TEXT("Match history SaveGame must not declare secrets or hands"), bHistoryContainsSecretField);

    bool bPublicStateContainsHand = false;
    for (TFieldIterator<FProperty> It(FMahjongPublicTableState::StaticStruct(), EFieldIteratorFlags::IncludeSuper); It; ++It)
    {
        bPublicStateContainsHand |= It->GetName().Contains(TEXT("Hand"), ESearchCase::IgnoreCase);
    }
    TestFalse(TEXT("公共牌桌状态不得包含私有手牌字段"), bPublicStateContainsHand);
    bool bReconnectSnapshotContainsCredential = false;
    for (TFieldIterator<FProperty> It(FMahjongReconnectSnapshot::StaticStruct(), EFieldIteratorFlags::IncludeSuper); It; ++It)
    {
        const FString Name = It->GetName();
        bReconnectSnapshotContainsCredential |= Name.Contains(TEXT("Token"), ESearchCase::IgnoreCase)
            || Name.Contains(TEXT("Secret"), ESearchCase::IgnoreCase)
            || Name.Contains(TEXT("Password"), ESearchCase::IgnoreCase);
    }
    TestFalse(TEXT("Reconnect snapshot must not expose credentials"), bReconnectSnapshotContainsCredential);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongExplicitLoginRequiredTest, "GuiyangMahjong.Auth.ExplicitLoginRequired", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongExplicitLoginRequiredTest::RunTest(const FString& Parameters)
{
    static const FString SlotName = TEXT("GuiyangLoginSettings");
    UGameplayStatics::DeleteGameInSlot(SlotName, 0);
    UGuiyangLoginSaveGame* SeedSettings = Cast<UGuiyangLoginSaveGame>(UGameplayStatics::CreateSaveGameObject(UGuiyangLoginSaveGame::StaticClass()));
    UGameplayStatics::SaveGameToSlot(SeedSettings, SlotName, 0);

    UGameInstance* FirstInstance = NewObject<UGameInstance>();
    FirstInstance->Init();
    UGuiyangLoginSubsystem* FirstLogin = FirstInstance->GetSubsystem<UGuiyangLoginSubsystem>();
    FirstLogin->LoginWithWechat();
#if PLATFORM_WINDOWS
    TestEqual(TEXT("Windows 微信入口必须明确使用模拟 Provider"), FirstLogin->GetCurrentProfile().Provider, EGuiyangLoginProvider::SimulatedWechat);
    TestTrue(TEXT("模拟微信 PlayerId 必须生成"), !FirstLogin->GetCurrentProfile().PlayerId.IsEmpty());
    FirstInstance->Shutdown();

    UGameInstance* SecondInstance = NewObject<UGameInstance>();
    SecondInstance->Init();
    UGuiyangLoginSubsystem* SecondLogin = SecondInstance->GetSubsystem<UGuiyangLoginSubsystem>();
    TestFalse(TEXT("重新启动后必须保持未登录，等待用户明确选择登录方式"), SecondLogin->IsSessionValid());
    TestEqual(TEXT("重新启动后的登录状态必须为 LoggedOut"), SecondLogin->GetLoginState(), EGuiyangLoginState::LoggedOut);
    SecondLogin->Logout();
    SecondInstance->Shutdown();
#else
    TestEqual(TEXT("非 Windows 未配置正式微信时必须保持未登录"), FirstLogin->GetLoginState(), EGuiyangLoginState::LoggedOut);
    FirstInstance->Shutdown();
#endif
    UGameplayStatics::DeleteGameInSlot(SlotName, 0);
    return true;
}

#if WITH_EDITOR
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongUISoundAssetsTest, "GuiyangMahjong.UI.SoundAssetsAndButtonBinding", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongUISoundAssetsTest::RunTest(const FString& Parameters)
{
    static const TCHAR* SoundPaths[] = {
        TEXT("/Game/UI/Audio/SFX_UI_Click.SFX_UI_Click"),
        TEXT("/Game/UI/Audio/SFX_Tile_Select.SFX_Tile_Select"),
        TEXT("/Game/UI/Audio/SFX_Tile_Play.SFX_Tile_Play"),
        TEXT("/Game/UI/Audio/SFX_Peng.SFX_Peng"),
        TEXT("/Game/UI/Audio/SFX_Gang.SFX_Gang"),
        TEXT("/Game/UI/Audio/SFX_Hu.SFX_Hu"),
        TEXT("/Game/UI/Audio/SFX_Pass.SFX_Pass"),
        TEXT("/Game/UI/Audio/BGM_FirstLightParticles.BGM_FirstLightParticles")
    };
    for (const TCHAR* SoundPath : SoundPaths)
    {
        TestNotNull(FString::Printf(TEXT("音效必须可被 UE 加载：%s"), SoundPath),
            LoadObject<USoundBase>(nullptr, SoundPath));
    }

    UWidgetBlueprint* ActionPanel = LoadObject<UWidgetBlueprint>(nullptr,
        TEXT("/Game/UI/Components/WBP_ActionButtonPanel.WBP_ActionButtonPanel"));
    TestNotNull(TEXT("操作按钮面板必须存在"), ActionPanel);
    if (ActionPanel && ActionPanel->WidgetTree)
    {
        TArray<UWidget*> Widgets;
        ActionPanel->WidgetTree->GetAllWidgets(Widgets);
        int32 ButtonCount = 0;
        for (UWidget* Widget : Widgets)
        {
            if (const UButton* Button = Cast<UButton>(Widget))
            {
                ++ButtonCount;
                TestNull(FString::Printf(TEXT("操作按钮 %s 不应叠加通用点击声"), *Button->GetName()),
                    Cast<USoundBase>(Button->GetStyle().PressedSlateSound.GetResourceObject()));
            }
        }
        TestEqual(TEXT("碰杠胡过面板必须包含四个按钮"), ButtonCount, 4);
    }

    UWidgetBlueprint* Login = LoadObject<UWidgetBlueprint>(nullptr,
        TEXT("/Game/UI/Screens/WBP_Login.WBP_Login"));
    TestNotNull(TEXT("登录界面必须存在"), Login);
    if (Login && Login->WidgetTree)
    {
        TArray<UWidget*> Widgets;
        Login->WidgetTree->GetAllWidgets(Widgets);
        int32 ButtonCount = 0;
        for (UWidget* Widget : Widgets)
        {
            if (const UButton* Button = Cast<UButton>(Widget))
            {
                ++ButtonCount;
                TestNotNull(FString::Printf(TEXT("通用按钮 %s 必须绑定点击声"), *Button->GetName()),
                    Cast<USoundBase>(Button->GetStyle().PressedSlateSound.GetResourceObject()));
            }
        }
        TestTrue(TEXT("登录界面必须包含通用按钮"), ButtonCount > 0);
    }

    UWidgetBlueprint* HandTile = LoadObject<UWidgetBlueprint>(nullptr,
        TEXT("/Game/UI/Components/WBP_HandTile.WBP_HandTile"));
    TestNotNull(TEXT("手牌组件必须存在"), HandTile);
    if (HandTile && HandTile->WidgetTree)
    {
        const UButton* TileButton = Cast<UButton>(HandTile->WidgetTree->FindWidget(TEXT("Btn_Tile")));
        TestNotNull(TEXT("手牌点击按钮必须存在"), TileButton);
        if (TileButton)
        {
            TestNull(TEXT("手牌按钮不应叠加通用点击声"),
                Cast<USoundBase>(TileButton->GetStyle().PressedSlateSound.GetResourceObject()));
        }
    }
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongSettingsDialogTest, "GuiyangMahjong.UI.SettingsDialogAsset", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongSettingsDialogTest::RunTest(const FString& Parameters)
{
    FMahjongLocalSettings InvalidSettings;
    InvalidSettings.MusicVolume = -1.0f;
    InvalidSettings.SoundVolume = 2.0f;
    InvalidSettings.Sanitize();
    TestEqual(TEXT("音乐音量必须限制为 0"), InvalidSettings.MusicVolume, 0.0f);
    TestEqual(TEXT("音效音量必须限制为 1"), InvalidSettings.SoundVolume, 1.0f);

    UWidgetBlueprint* Settings = LoadObject<UWidgetBlueprint>(nullptr,
        TEXT("/Game/UI/Dialogs/WBP_Settings.WBP_Settings"));
    TestNotNull(TEXT("设置弹窗资产必须存在"), Settings);
    if (!Settings || !Settings->WidgetTree)
    {
        return false;
    }

    TestTrue(TEXT("设置弹窗必须继承 UMobileSettingsWidget"),
        Settings->GeneratedClass && Settings->GeneratedClass->IsChildOf(UMobileSettingsWidget::StaticClass()));
    TestNotNull(TEXT("音乐开关必须存在"), Cast<UCheckBox>(Settings->WidgetTree->FindWidget(TEXT("Chk_MusicEnabled"))));
    TestNotNull(TEXT("音效开关必须存在"), Cast<UCheckBox>(Settings->WidgetTree->FindWidget(TEXT("Chk_SoundEnabled"))));
    TestNotNull(TEXT("震动开关必须存在"), Cast<UCheckBox>(Settings->WidgetTree->FindWidget(TEXT("Chk_VibrationEnabled"))));
    TestNotNull(TEXT("音乐音量滑块必须存在"), Cast<USlider>(Settings->WidgetTree->FindWidget(TEXT("Slider_MusicVolume"))));
    TestNotNull(TEXT("音效音量滑块必须存在"), Cast<USlider>(Settings->WidgetTree->FindWidget(TEXT("Slider_SoundVolume"))));
    TestNotNull(TEXT("重置按钮必须存在"), Cast<UButton>(Settings->WidgetTree->FindWidget(TEXT("Btn_Reset"))));
    TestNotNull(TEXT("退出游戏按钮必须存在"), Cast<UButton>(Settings->WidgetTree->FindWidget(TEXT("Btn_ExitGame"))));
    TestNotNull(TEXT("关闭按钮必须存在"), Cast<UButton>(Settings->WidgetTree->FindWidget(TEXT("Btn_Close"))));

    UWidgetBlueprint* Lobby = LoadObject<UWidgetBlueprint>(nullptr,
        TEXT("/Game/UI/Screens/WBP_Lobby.WBP_Lobby"));
    TestNotNull(TEXT("大厅资产必须存在"), Lobby);
    if (Lobby && Lobby->WidgetTree)
    {
        TestNotNull(TEXT("大厅设置按钮必须存在"),
            Cast<UButton>(Lobby->WidgetTree->FindWidget(TEXT("Btn_Setting"))));
    }
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongCreateRoomDialogLayoutTest, "GuiyangMahjong.UI.CreateRoomDialogLayout", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongCreateRoomDialogLayoutTest::RunTest(const FString& Parameters)
{
    UWidgetBlueprint* CreateRoom = LoadObject<UWidgetBlueprint>(nullptr,
        TEXT("/Game/UI/Dialogs/WBP_CreateRoomDialog.WBP_CreateRoomDialog"));
    TestNotNull(TEXT("创建房间弹窗资源必须存在"), CreateRoom);
    if (!CreateRoom || !CreateRoom->WidgetTree)
    {
        return false;
    }

    const UBorder* Dialog = Cast<UBorder>(CreateRoom->WidgetTree->FindWidget(TEXT("Border_Dialog9Slice")));
    TestNotNull(TEXT("创建房间弹窗背景必须存在"), Dialog);
    const UCanvasPanelSlot* DialogSlot = Dialog ? Cast<UCanvasPanelSlot>(Dialog->Slot) : nullptr;
    TestNotNull(TEXT("创建房间弹窗必须使用画布布局"), DialogSlot);
    if (DialogSlot)
    {
        TestTrue(TEXT("1.5 倍缩放下弹窗宽度必须适配手机横屏"), DialogSlot->GetSize().X <= 1720.0f);
        TestTrue(TEXT("1.5 倍缩放下弹窗高度不得超过 700"), DialogSlot->GetSize().Y <= 700.0f);
    }

    for (const FName WidgetName : {FName(TEXT("RuleConfig")), FName(TEXT("RuleSummary")),
        FName(TEXT("Txt_Status")), FName(TEXT("Btn_Create")), FName(TEXT("Btn_Cancel"))})
    {
        const UWidget* Widget = CreateRoom->WidgetTree->FindWidget(WidgetName);
        TestNotNull(FString::Printf(TEXT("创建房间关键控件必须存在：%s"), *WidgetName.ToString()), Widget);
        const UCanvasPanelSlot* Slot = Widget ? Cast<UCanvasPanelSlot>(Widget->Slot) : nullptr;
        TestNotNull(FString::Printf(TEXT("创建房间关键控件必须使用画布槽：%s"), *WidgetName.ToString()), Slot);
        if (Slot)
        {
            const FVector2D FarEdge = Slot->GetPosition() + Slot->GetSize();
            TestTrue(FString::Printf(TEXT("控件不得超出弹窗右边界：%s"), *WidgetName.ToString()),
                FarEdge.X <= 1720.0f);
            TestTrue(FString::Printf(TEXT("控件不得超出弹窗下边界：%s"), *WidgetName.ToString()),
                FarEdge.Y <= 700.0f);
        }
    }
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongRoomReturnLobbyButtonTest, "GuiyangMahjong.UI.RoomReturnLobbyButton", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongRoomReturnLobbyButtonTest::RunTest(const FString& Parameters)
{
    UWidgetBlueprint* Room = LoadObject<UWidgetBlueprint>(nullptr,
        TEXT("/Game/UI/Screens/WBP_Room.WBP_Room"));
    TestNotNull(TEXT("游戏房间界面必须存在"), Room);
    if (!Room || !Room->WidgetTree)
    {
        return false;
    }

    const UButton* ReturnButton = Cast<UButton>(Room->WidgetTree->FindWidget(TEXT("Btn_ReturnLobby")));
    TestNotNull(TEXT("游戏房间必须提供返回大厅按钮"), ReturnButton);
    const UCanvasPanelSlot* ReturnSlot = ReturnButton ? Cast<UCanvasPanelSlot>(ReturnButton->Slot) : nullptr;
    TestNotNull(TEXT("返回大厅按钮必须使用画布槽"), ReturnSlot);
    if (ReturnSlot)
    {
        const FVector2D FarEdge = ReturnSlot->GetPosition() + ReturnSlot->GetSize();
        TestTrue(TEXT("返回大厅按钮不得超出房间界面右边界"), FarEdge.X <= 1920.0f);
        TestTrue(TEXT("返回大厅按钮必须位于移动端可视高度内"), FarEdge.Y <= 720.0f);
    }
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongThreeDTableLayoutTest, "GuiyangMahjong.UI.ThreeDTableLayout", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongThreeDTableLayoutTest::RunTest(const FString& Parameters)
{
    UWidgetBlueprint* GameHUD = LoadObject<UWidgetBlueprint>(nullptr,
        TEXT("/Game/UI/Screens/WBP_GameHUD.WBP_GameHUD"));
    TestNotNull(TEXT("三维牌桌 HUD 必须存在"), GameHUD);
    if (!GameHUD || !GameHUD->WidgetTree) return false;

    // The serialized legacy UViewport is ignored at runtime. Artists tune the client-only
    // presentation in MahjongRoomVisualPreviewMap; MahjongRoomMap stays target-neutral.
    const UWidget* LegacyBackground = GameHUD->WidgetTree->FindWidget(TEXT("Background_ComponentSlot"));
    TestTrue(TEXT("旧版绿色桌面背景若仍存在于序列化资源中，必须由 HUD 兼容绑定接管"),
        !LegacyBackground || FindFProperty<FObjectPropertyBase>(
            UMobileMahjongHUDWidget::StaticClass(), TEXT("Background_ComponentSlot")) != nullptr);
    TestNotNull(TEXT("等待阶段的准备按钮必须直接位于三维牌桌 HUD"),
        GameHUD->WidgetTree->FindWidget(TEXT("Btn_Ready")));
    TestNotNull(TEXT("三维牌桌 HUD 必须保留返回大厅按钮"),
        GameHUD->WidgetTree->FindWidget(TEXT("Btn_ReturnLobby")));
    TestNotNull(TEXT("准备状态提示必须直接位于三维牌桌 HUD"),
        GameHUD->WidgetTree->FindWidget(TEXT("Txt_ReadyStatus")));
    TestNotNull(TEXT("三维牌桌 Actor 类必须可加载"), AMahjong3DTableActor::StaticClass());
    const AGuiyangMahjongPlayerController* ControllerDefault =
        GetDefault<AGuiyangMahjongPlayerController>();
    TestNotNull(TEXT("房间玩家控制器必须可加载"), ControllerDefault);
    if (ControllerDefault)
    {
        TestFalse(TEXT("房间固定镜头不得被 Pawn 或旁观者自动接管"),
            ControllerDefault->bAutoManageActiveCameraTarget);
    }
    const AMahjongRoomCameraActor* CameraDefault = GetDefault<AMahjongRoomCameraActor>();
    TestNotNull(TEXT("房间电影摄像机预设类必须可加载"), CameraDefault);
    if (CameraDefault && CameraDefault->GetCineCameraComponent())
    {
        TestTrue(TEXT("房间摄像机必须带稳定标签"),
            CameraDefault->ActorHasTag(AMahjongRoomCameraActor::RoomCameraTag));
        TestEqual(TEXT("默认摄像机焦距必须为 30mm"),
            CameraDefault->GetCineCameraComponent()->CurrentFocalLength, 30.0f);
        TestFalse(TEXT("移动横屏摄像机不得强制黑边宽高比"),
            CameraDefault->GetCineCameraComponent()->bConstrainAspectRatio);
        const UCineCameraComponent* CameraComponent = CameraDefault->GetCineCameraComponent();
        const FPostProcessSettings& PostProcess = CameraComponent->PostProcessSettings;
        TestTrue(TEXT("Room camera must override motion blur"), PostProcess.bOverride_MotionBlurAmount);
        TestEqual(TEXT("Room camera motion blur must be disabled"), PostProcess.MotionBlurAmount, 0.0f);
        TestTrue(TEXT("Room camera must override depth of field"), PostProcess.bOverride_DepthOfFieldEnabled);
        TestFalse(TEXT("Room camera depth of field must be disabled"), PostProcess.DepthOfFieldEnabled);
        TestTrue(TEXT("Room camera must override sharpening"), PostProcess.bOverride_Sharpen);
        TestEqual(TEXT("Room camera sharpening must remain readable"), PostProcess.Sharpen, 0.5f);
        TestEqual(TEXT("Room camera focus must not blur Mahjong faces"),
            CameraComponent->FocusSettings.FocusMethod, ECameraFocusMethod::Disable);
    }

    const FRotator UprightRotation =
        AMahjong3DTableActor::ResolveTileMeshRotation(FRotator::ZeroRotator, true, true);
    const FVector UprightFaceNormal = UprightRotation.RotateVector(FVector::YAxisVector);
    TestTrue(TEXT("Local upright Mahjong50 face must point toward the south-side camera"),
        UprightFaceNormal.Y < -0.99f);
    TestTrue(TEXT("Local upright Mahjong50 glyph must remain right-side up"),
        UprightRotation.RotateVector(FVector::ZAxisVector).Z > 0.99f);
    const FRotator CameraFacingSouthRotation =
        AMahjong3DTableActor::ResolveTileMeshRotation(FRotator(0.0f, 0.0f, -30.0f), true, true);
    const FVector CameraFacingSouthNormal =
        CameraFacingSouthRotation.RotateVector(FVector::YAxisVector);
    TestTrue(TEXT("South hand face must tilt upward toward the elevated room camera"),
        CameraFacingSouthNormal.Y < -0.8f && CameraFacingSouthNormal.Z > 0.49f);
    const FRotator FlatFaceUpRotation =
        AMahjong3DTableActor::ResolveTileMeshRotation(FRotator::ZeroRotator, true, false);
    TestTrue(TEXT("Flat face-up Mahjong50 tile must point upward"),
        FlatFaceUpRotation.RotateVector(FVector::YAxisVector).Z > 0.99f);
    const FRotator FlatFaceDownRotation =
        AMahjong3DTableActor::ResolveTileMeshRotation(FRotator::ZeroRotator, false, false);
    TestTrue(TEXT("Flat face-down Mahjong50 tile must point downward"),
        FlatFaceDownRotation.RotateVector(FVector::YAxisVector).Z < -0.99f);

    UWorld* SharedRoomWorld = LoadObject<UWorld>(nullptr,
        TEXT("/Game/Maps/MahjongRoomMap.MahjongRoomMap"));
    TestNotNull(TEXT("共享麻将房间关卡必须可加载"), SharedRoomWorld);
    bool bSharedMapContainsClientPresentation = false;
    if (SharedRoomWorld && SharedRoomWorld->PersistentLevel)
    {
        for (const AActor* Actor : SharedRoomWorld->PersistentLevel->Actors)
        {
            bSharedMapContainsClientPresentation |= IsValid(Actor)
                && (Actor->IsA<AMahjongRoomPresentationActor>()
                    || Actor->IsA<AMahjong3DTableActor>()
                    || Actor->IsA<AMahjongRoomCameraActor>());
        }
    }
    TestFalse(TEXT("共享 MahjongRoomMap 不得序列化客户端展示类"),
        bSharedMapContainsClientPresentation);

    const UMahjongRoomPresentationSettings* PresentationSettings =
        GetDefault<UMahjongRoomPresentationSettings>();
    TestNotNull(TEXT("客户端房间展示设置必须存在"), PresentationSettings);
    UClass* ConfiguredPresentationClass = PresentationSettings
        ? PresentationSettings->PresentationClass.LoadSynchronous() : nullptr;
    TestNotNull(TEXT("客户端房间展示蓝图必须可加载"), ConfiguredPresentationClass);
    UWorld* PreviewWorld = LoadObject<UWorld>(nullptr,
        TEXT("/Game/Maps/MahjongRoomVisualPreviewMap.MahjongRoomVisualPreviewMap"));
    TestNotNull(TEXT("麻将房间视觉预览关卡必须可加载"), PreviewWorld);
    const AMahjongRoomPresentationActor* PresentationInstance = nullptr;
    if (PreviewWorld && PreviewWorld->PersistentLevel && ConfiguredPresentationClass)
    {
        for (const AActor* Actor : PreviewWorld->PersistentLevel->Actors)
        {
            if (IsValid(Actor) && Actor->GetClass() == ConfiguredPresentationClass)
            {
                PresentationInstance = Cast<AMahjongRoomPresentationActor>(Actor);
                break;
            }
        }
    }
    if (ConfiguredPresentationClass)
    {
        TestTrue(TEXT("配置的展示蓝图必须继承客户端 Presentation 基类"),
            ConfiguredPresentationClass->IsChildOf(AMahjongRoomPresentationActor::StaticClass()));
        TestEqual(TEXT("运行时必须配置设计师可编辑的 BP_MahjongRoomPresentation"),
            ConfiguredPresentationClass->GetPathName(),
            FString(TEXT("/Game/Client/Room/Presentation/BP_MahjongRoomPresentation."
                         "BP_MahjongRoomPresentation_C")));

        TestNotNull(TEXT("预览关卡必须包含房间展示蓝图实例"), PresentationInstance);
        if (PresentationInstance)
        {
            const UDirectionalLightComponent* Directional =
                PresentationInstance->FindComponentByClass<UDirectionalLightComponent>();
            TestNotNull(TEXT("运行时展示必须包含跨平台主方向光"), Directional);
            if (Directional)
            {
                TestEqual(TEXT("主方向光必须由展示蓝图拥有"),
                    Directional->CreationMethod, EComponentCreationMethod::SimpleConstructionScript);
                TestTrue(TEXT("主方向光必须处于手动曝光安全范围"),
                    Directional->Intensity > 0.0f && Directional->Intensity <= 50.0f);
                TestTrue(TEXT("夜间主方向光必须保留桌面接触阴影"), Directional->CastShadows);
            }
            const USkyLightComponent* Sky =
                PresentationInstance->FindComponentByClass<USkyLightComponent>();
            TestNotNull(TEXT("运行时展示必须包含环境补光"), Sky);
            if (Sky)
            {
                TestEqual(TEXT("天光必须由展示蓝图拥有"),
                    Sky->CreationMethod, EComponentCreationMethod::SimpleConstructionScript);
                TestFalse(TEXT("封闭麻将房不得启用实时天空捕获"),
                    Sky->IsRealTimeCaptureEnabled());
            }
            TArray<URectLightComponent*> RectLights;
            PresentationInstance->GetComponents<URectLightComponent>(RectLights);
            TestEqual(TEXT("运行时展示必须包含主光、补光、顶光与轮廓光"), RectLights.Num(), 4);
            for (const URectLightComponent* Rect : RectLights)
            {
                TestEqual(TEXT("矩形灯必须由展示蓝图拥有"),
                    Rect->CreationMethod, EComponentCreationMethod::SimpleConstructionScript);
                TestEqual(TEXT("矩形灯必须使用明确的流明单位"),
                    Rect->IntensityUnits, ELightUnits::Lumens);
                TestTrue(TEXT("减半后的矩形灯必须处于安全流明范围"),
                    Rect->Intensity > 0.0f && Rect->Intensity <= 100.0f);
            }
            const UCineCameraComponent* PresentationCamera =
                PresentationInstance->FindComponentByClass<UCineCameraComponent>();
            TestNotNull(TEXT("展示蓝图必须直接拥有可编辑电影摄像机"), PresentationCamera);
            if (PresentationCamera)
            {
                TestEqual(TEXT("电影摄像机必须由展示蓝图拥有"),
                    PresentationCamera->CreationMethod,
                    EComponentCreationMethod::SimpleConstructionScript);
            }
            TArray<UStaticMeshComponent*> StaticMeshes;
            PresentationInstance->GetComponents<UStaticMeshComponent>(StaticMeshes);
            const UStaticMeshComponent* TableMeshComponent = nullptr;
            for (const UStaticMeshComponent* Component : StaticMeshes)
            {
                if (Component && Component->GetStaticMesh())
                {
                    const FString GeometryIdentity =
                        Component->GetName() + TEXT(" ")
                        + Component->GetStaticMesh()->GetPathName();
                    TestFalse(TEXT("房间展示蓝图不得包含球体或天空穹顶几何体"),
                        GeometryIdentity.Contains(TEXT("Sphere"), ESearchCase::IgnoreCase)
                        || GeometryIdentity.Contains(TEXT("Dome"), ESearchCase::IgnoreCase)
                        || GeometryIdentity.Contains(TEXT("Hemisphere"), ESearchCase::IgnoreCase));
                }
                if (Component && Component->GetName() == TEXT("MahjongTableMesh"))
                {
                    TableMeshComponent = Component;
                    break;
                }
            }
            TestNotNull(TEXT("展示蓝图必须直接拥有可编辑麻将桌模型"), TableMeshComponent);
            if (TableMeshComponent)
            {
                TestEqual(TEXT("麻将桌模型必须由展示蓝图拥有"),
                    TableMeshComponent->CreationMethod,
                    EComponentCreationMethod::SimpleConstructionScript);
                TestTrue(TEXT("麻将桌模型组件必须配置静态网格"),
                    TableMeshComponent->GetStaticMesh() != nullptr);
                if (TableMeshComponent->GetStaticMesh())
                {
                    const FVector TableSize =
                        TableMeshComponent->GetStaticMesh()->GetBounds().BoxExtent
                        * 2.0f * TableMeshComponent->GetRelativeScale3D();
                    TestTrue(TEXT("Mahjong tabletop width must be 300 cm"),
                        FMath::IsNearlyEqual(TableSize.X, 300.0f, 0.5f));
                    TestTrue(TEXT("Mahjong tabletop depth must be 300 cm"),
                        FMath::IsNearlyEqual(TableSize.Y, 300.0f, 0.5f));
                }
            }
            TArray<UChildActorComponent*> ChildActors;
            PresentationInstance->GetComponents<UChildActorComponent>(ChildActors);
            TestEqual(TEXT("展示蓝图只保留一个动态麻将牌布局 Child Actor"),
                ChildActors.Num(), 1);
            if (ChildActors.Num() == 1)
            {
                TestEqual(TEXT("麻将牌布局 Child Actor 必须由展示蓝图拥有"),
                    ChildActors[0]->CreationMethod,
                    EComponentCreationMethod::SimpleConstructionScript);
                TestTrue(TEXT("麻将牌布局必须使用运行时 AMahjong3DTableActor"),
                    ChildActors[0]->GetChildActorClass() == AMahjong3DTableActor::StaticClass());
            }
        }
    }

    bool bHasPresentation = false;
    bool bHasIndependentKeyLight = false;
    if (PreviewWorld && PreviewWorld->PersistentLevel)
    {
        for (const AActor* Actor : PreviewWorld->PersistentLevel->Actors)
        {
            if (!IsValid(Actor)) continue;
            bHasPresentation |= Actor->GetClass() == ConfiguredPresentationClass;
            bHasIndependentKeyLight |= Actor->IsA<ADirectionalLight>() || Actor->IsA<ASkyLight>();
        }
    }
    TestTrue(TEXT("视觉预览关卡必须放置与运行时相同的展示蓝图"), bHasPresentation);
    TestFalse(TEXT("关键灯光必须属于展示蓝图，预览关卡不得保留独立实例"), bHasIndependentKeyLight);
    UStaticMesh* TileMesh = LoadObject<UStaticMesh>(nullptr,
        TEXT("/Game/Art/Mahjong/Mahjong50/Tiles/SM_Mahjong50_Characters_1.SM_Mahjong50_Characters_1"));
    TestNotNull(TEXT("Mahjong50 PBR 麻将牌静态网格必须已导入"), TileMesh);
    if (TileMesh)
    {
        const FVector Extent = TileMesh->GetBounds().BoxExtent;
        TestTrue(TEXT("麻将牌宽度必须约为 36mm"), FMath::IsNearlyEqual(Extent.X * 2.0f, 3.6f, 0.15f));
        TestTrue(TEXT("麻将牌厚度必须约为 26mm"), FMath::IsNearlyEqual(Extent.Y * 2.0f, 2.6f, 0.15f));
        TestTrue(TEXT("麻将牌高度必须约为 50mm"), FMath::IsNearlyEqual(Extent.Z * 2.0f, 5.0f, 0.15f));
        TestEqual(TEXT("Mahjong50 麻将牌必须使用牌身与牌面连续的单一材质槽"),
            TileMesh->GetStaticMaterials().Num(), 1);
    }
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongPhoneTabletScalingTest, "GuiyangMahjong.UI.PhoneTabletScaling", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongPhoneTabletScalingTest::RunTest(const FString& Parameters)
{
    const UMahjongUIScalingRule* Rule = GetDefault<UMahjongUIScalingRule>();
    const float PhoneScale = Rule->GetDPIScaleBasedOnSize(FIntPoint(2700, 1224));
    const float Tablet16x10Scale = Rule->GetDPIScaleBasedOnSize(FIntPoint(2560, 1600));
    const float Tablet4x3Scale = Rule->GetDPIScaleBasedOnSize(FIntPoint(2048, 1536));
    TestEqual(TEXT("20:9 手机必须使用 1.0 倍 UI"), PhoneScale, 1.0f);
    TestEqual(TEXT("16:10 平板必须使用 1.0 倍 UI"), Tablet16x10Scale, 1.0f);
    TestEqual(TEXT("4:3 平板必须使用 1.0 倍 UI"), Tablet4x3Scale, 1.0f);
    TestEqual(TEXT("手机宽屏前景必须保持比例"),
        UMahjongResponsiveScaleBox::ResolveStretchForViewport(FIntPoint(2700, 1224)), EStretch::ScaleToFit);
    TestEqual(TEXT("16:10 平板前景必须保持比例"),
        UMahjongResponsiveScaleBox::ResolveStretchForViewport(FIntPoint(2560, 1600)), EStretch::ScaleToFit);
    TestEqual(TEXT("4:3 平板前景必须保持比例"),
        UMahjongResponsiveScaleBox::ResolveStretchForViewport(FIntPoint(2048, 1536)), EStretch::ScaleToFit);

    UWidgetBlueprint* RootHUD = LoadObject<UWidgetBlueprint>(nullptr,
        TEXT("/Game/UI/Screens/WBP_RootHUD.WBP_RootHUD"));
    TestNotNull(TEXT("RootHUD 必须存在"), RootHUD);
    if (RootHUD && RootHUD->WidgetTree)
    {
        TestNotNull(TEXT("RootHUD 必须使用手机/平板响应式前景缩放框"),
            Cast<UMahjongResponsiveScaleBox>(RootHUD->WidgetTree->FindWidget(TEXT("Scale_Design1920x1080"))));
    }
    return true;
}
#endif

#endif
