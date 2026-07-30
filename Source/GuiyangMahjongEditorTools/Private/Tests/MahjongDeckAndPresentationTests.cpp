#include "MahjongCoreTestSupport.h"

#if WITH_DEV_AUTOMATION_TESTS

/**
 * 覆盖牌墙、规则快照、HUD 座位映射、牌面视觉、重连展示和集成钩子默认安全性。
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


#endif

