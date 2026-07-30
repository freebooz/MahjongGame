#include "MahjongCoreTestSupport.h"

#if WITH_DEV_AUTOMATION_TESTS

/**
 * 覆盖客户端登录生命周期、战绩持久化和本地存储脱敏边界。
 * 测试会创建并清理固定 SaveGame 槽位，不得与人工验证存档并发运行。
 */
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
        bPublicStateContainsHand |= It->GetName().Contains(TEXT("Hand"), ESearchCase::IgnoreCase);
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

#endif
