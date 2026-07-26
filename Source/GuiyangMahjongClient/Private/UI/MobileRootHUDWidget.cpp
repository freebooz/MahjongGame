#include "UI/MobileRootHUDWidget.h"

#include "Auth/GuiyangLoginSubsystem.h"
#include "Blueprint/WidgetTree.h"
#include "Components/Overlay.h"
#include "Components/OverlaySlot.h"
#include "Components/Widget.h"
#include "Game/GuiyangMahjongGameState.h"
#include "Game/GuiyangMahjongPlayerController.h"
#include "Game/GuiyangMahjongPlayerState.h"
#include "GuiyangMahjong.h"
#include "Lobby/GuiyangLobbySubsystem.h"
#include "Network/GuiyangReconnectSubsystem.h"
#include "UI/MobileErrorToastWidget.h"
#include "UI/MahjongBackgroundMusicSubsystem.h"
#include "UI/MobileLobbyWidget.h"
#include "UI/MobileCreatingRoomWidget.h"
#include "UI/MobileMahjongHUDWidget.h"
#include "UI/MobileRoomWidget.h"
#include "UI/MobileReconnectOverlayWidget.h"
#include "UI/MobileSettlementWidget.h"
#include "Rules/GuiyangRuleSnapshot.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"

namespace
{
    bool IsAwaitingAllocatedRoomState(const UWorld* World)
    {
        return World && World->URL.HasOption(TEXT("JoinTicket"));
    }

    FMahjongTile MakeReviewTile(const EMahjongSuit Suit, const int32 Rank, const int32 UniqueId)
    {
        FMahjongTile Tile;
        Tile.Suit = Suit;
        Tile.Type = EMahjongTileType::Number;
        Tile.Rank = Rank;
        Tile.UniqueId = UniqueId;
        return Tile;
    }

    TArray<FMahjongSeatInfo> MakeReviewSeats()
    {
        const TCHAR* Names[] = {TEXT("黔小鸡"), TEXT("甲秀玩家"), TEXT("南明雀友"), TEXT("青岩牌友")};
        TArray<FMahjongSeatInfo> Seats;
        for (int32 SeatIndex = 0; SeatIndex < 4; ++SeatIndex)
        {
            FMahjongSeatInfo& Seat = Seats.AddDefaulted_GetRef();
            Seat.SeatIndex = SeatIndex;
            Seat.PlayerId = FString::Printf(TEXT("ui-review-%d"), SeatIndex);
            Seat.PlayerName = Names[SeatIndex];
            Seat.bOwner = SeatIndex == 0;
            Seat.bOccupied = true;
            Seat.bReady = true;
            Seat.bOnline = true;
            Seat.HandTileCount = SeatIndex == 0 ? 14 : 13;
            Seat.Score = (SeatIndex == 0 ? 18 : SeatIndex == 1 ? -6 : SeatIndex == 2 ? 3 : -15);
            Seat.PingMilliseconds = 22 + SeatIndex * 11;
        }
        return Seats;
    }

    FMahjongRoomState MakeReviewRoomState()
    {
        FMahjongRoomState State;
        State.RoomInfo.RoomId = TEXT("520001");
        State.RoomInfo.RuleSummary = TEXT("贵阳捉鸡 · 4局 · 冲锋鸡/责任鸡");
        State.RoomInfo.OwnerPlayerId = TEXT("ui-review-0");
        State.RoomInfo.RoundCount = 4;
        State.RoomInfo.CurrentRound = 2;
        State.RoomInfo.bPublicRoom = false;
        State.RoomInfo.bPasswordProtected = true;
        State.RuleSnapshot = UGuiyangRuleSnapshotLibrary::CreateSnapshot(FMahjongRuleConfig());
        State.Lifecycle = EMahjongRoomLifecycle::ReadyCheck;
        State.Seats = MakeReviewSeats();
        State.bGameStarting = true;
        State.StateSequence = 19;
        return State;
    }

    FMahjongPublicTableState MakeReviewPublicState()
    {
        FMahjongPublicTableState State;
        State.RoundId = 2;
        State.TurnId = 18;
        State.ServerActionSequence = 37;
        State.Phase = EMahjongTablePhase::WaitingForAction;
        State.CurrentTurnSeat = 0;
        State.RemainingTileCount = 56;
        State.ActionTimeoutSeconds = 12;
        State.Seats = MakeReviewSeats();
        int32 UniqueId = 100;
        int32 Sequence = 1;
        for (int32 SeatIndex = 0; SeatIndex < 4; ++SeatIndex)
        {
            for (int32 TileIndex = 0; TileIndex < 5; ++TileIndex)
            {
                FMahjongDiscardRecord& Record = State.Discards.AddDefaulted_GetRef();
                Record.SeatIndex = SeatIndex;
                Record.Tile = MakeReviewTile(static_cast<EMahjongSuit>((SeatIndex + TileIndex) % 3),
                    TileIndex + 1, UniqueId++);
                Record.Sequence = Sequence++;
            }
        }
        FMahjongMeld& Meld = State.PublicMelds.AddDefaulted_GetRef();
        Meld.Type = EMahjongMeldType::Peng;
        Meld.OwnerSeat = 1;
        Meld.FromSeat = 2;
        Meld.Tiles = {
            MakeReviewTile(EMahjongSuit::Dots, 8, UniqueId++),
            MakeReviewTile(EMahjongSuit::Dots, 8, UniqueId++),
            MakeReviewTile(EMahjongSuit::Dots, 8, UniqueId++)
        };
        State.LastDiscard = State.Discards.Last().Tile;
        State.FlippedJiTile = MakeReviewTile(EMahjongSuit::Characters, 1, UniqueId++);
        FMahjongJiEvent& JiEvent = State.JiEvents.AddDefaulted_GetRef();
        JiEvent.Type = EMahjongJiEventType::ChongFeng;
        JiEvent.ActorSeat = 2;
        JiEvent.Tile = MakeReviewTile(EMahjongSuit::Bamboo, 5, UniqueId++);
        JiEvent.ValueUnits = 2;
        State.StateSequence = 42;
        return State;
    }

    FMahjongPrivatePlayerState MakeReviewPrivateState()
    {
        FMahjongPrivatePlayerState State;
        State.RoundId = 2;
        State.TurnId = 18;
        State.SeatIndex = 0;
        int32 UniqueId = 300;
        for (int32 TileIndex = 0; TileIndex < 14; ++TileIndex)
        {
            State.Hand.Tiles.Add(MakeReviewTile(static_cast<EMahjongSuit>(TileIndex % 3),
                TileIndex % 9 + 1, UniqueId++));
        }
        State.Hand.Sort();
        State.StateSequence = 43;
        return State;
    }

    TArray<FMahjongAction> MakeReviewActions()
    {
        TArray<FMahjongAction> Actions;
        for (const EMahjongActionType Type : {EMahjongActionType::Hu, EMahjongActionType::MingGang,
            EMahjongActionType::Peng})
        {
            FMahjongAction& Action = Actions.AddDefaulted_GetRef();
            Action.Type = Type;
            Action.SourceSeat = 0;
            Action.TargetSeat = 3;
        }
        return Actions;
    }

    FMahjongSettlementResult MakeReviewSettlement()
    {
        FMahjongSettlementResult Result;
        Result.bSelfDraw = true;
        Result.WinnerSeat = 0;
        Result.WinningSeats = {0};
        Result.WinningTile = MakeReviewTile(EMahjongSuit::Characters, 9, 500);
        Result.FlippedJiTile = MakeReviewTile(EMahjongSuit::Characters, 1, 501);
        Result.PlayerJiCounts = {3, 1, 2, 0};
        const int32 Scores[] = {18, -6, 3, -15};
        for (int32 SeatIndex = 0; SeatIndex < 4; ++SeatIndex)
        {
            FMahjongPlayerScoreResult& Player = Result.PlayerResults.AddDefaulted_GetRef();
            Player.SeatIndex = SeatIndex;
            Player.BaseScoreDelta = SeatIndex == 0 ? 6 : -2;
            Player.JiScoreDelta = SeatIndex == 0 ? 6 : SeatIndex == 2 ? 1 : -3;
            Player.SpecialJiScoreDelta = SeatIndex == 0 ? 4 : -1;
            Player.GangScoreDelta = SeatIndex == 0 ? 2 : 0;
            Player.TotalDelta = Scores[SeatIndex];
            Player.TotalScore = 30 + Scores[SeatIndex];
        }
        return Result;
    }
}

void UMobileRootHUDWidget::NativeConstruct()
{
    // 根路由集中订阅所有跨页面事件，子页面不彼此引用。
    Super::NativeConstruct();
    if (UMahjongBackgroundMusicSubsystem* Music =
        GetGameInstance()->GetSubsystem<UMahjongBackgroundMusicSubsystem>())
    {
        Music->EnsurePlaying(this);
    }
    if (FParse::Param(FCommandLine::Get(), TEXT("UIReviewScreenshot")))
    {
        ShowLogin();
        UE_LOG(LogMahjongUI, Log, TEXT("UI 审查模式：已隔离登录、房间和大厅事件"));
        return;
    }
    if (UGuiyangLoginSubsystem* Login = GetGameInstance()->GetSubsystem<UGuiyangLoginSubsystem>())
    {
        Login->OnLoginStateChanged.AddUniqueDynamic(this, &ThisClass::HandleLoginStateChanged);
        Login->OnLoginFailed.AddUniqueDynamic(this, &ThisClass::HandleLoginFailed);
        if (Login->IsSessionValid())
        {
            if (IsAwaitingAllocatedRoomState(GetWorld())) ShowCreatingRoom(); else ShowLobby();
        }
        else
        {
            ShowLogin();
        }
    }
    else
    {
        ShowLogin();
    }
    if (AGuiyangMahjongPlayerController* PC = Cast<AGuiyangMahjongPlayerController>(GetOwningPlayer()))
    {
        PC->OnErrorShown.AddUniqueDynamic(this, &ThisClass::HandleLoginFailed);
        PC->OnReconnectRestored.AddUniqueDynamic(this, &ThisClass::HandleReconnectRestored);
    }
    if (AGuiyangMahjongGameState* GS = GetWorld() ? GetWorld()->GetGameState<AGuiyangMahjongGameState>() : nullptr)
    {
        GS->OnRoomStateUpdated.AddUniqueDynamic(this, &ThisClass::HandleRoomStateUpdated);
        RouteFromRoomState(GS->RoomState);
    }
    if (UGuiyangReconnectSubsystem* Reconnect = GetGameInstance()->GetSubsystem<UGuiyangReconnectSubsystem>())
    {
        Reconnect->OnReconnectStateChanged.AddUniqueDynamic(this, &ThisClass::HandleReconnectStateChanged);
        if (Reconnect->IsReconnectPending())
        {
            HandleReconnectStateChanged(Reconnect->GetStatus(), Reconnect->GetRemainingSeconds(), Reconnect->CanRetry());
        }
    }
    if (UGuiyangLobbySubsystem* Lobby = GetGameInstance()->GetSubsystem<UGuiyangLobbySubsystem>())
    {
        Lobby->OnRequestFailed.AddUniqueDynamic(this, &ThisClass::HandleLobbyRequestFailed);
        Lobby->OnBootstrapUpdated.AddUniqueDynamic(this, &ThisClass::HandleLobbyBootstrapUpdated);
    }
    UE_LOG(LogMahjongUI, Log, TEXT("全局 RootHUD 创建完成"));
}

void UMobileRootHUDWidget::NativeDestruct()
{
    if (UGuiyangLoginSubsystem* Login = GetGameInstance()->GetSubsystem<UGuiyangLoginSubsystem>())
    {
        Login->OnLoginStateChanged.RemoveDynamic(this, &ThisClass::HandleLoginStateChanged);
        Login->OnLoginFailed.RemoveDynamic(this, &ThisClass::HandleLoginFailed);
    }
    if (AGuiyangMahjongPlayerController* PC = Cast<AGuiyangMahjongPlayerController>(GetOwningPlayer()))
    {
        PC->OnErrorShown.RemoveDynamic(this, &ThisClass::HandleLoginFailed);
        PC->OnReconnectRestored.RemoveDynamic(this, &ThisClass::HandleReconnectRestored);
    }
    if (AGuiyangMahjongGameState* GS = GetWorld() ? GetWorld()->GetGameState<AGuiyangMahjongGameState>() : nullptr)
    {
        GS->OnRoomStateUpdated.RemoveDynamic(this, &ThisClass::HandleRoomStateUpdated);
    }
    if (UGuiyangReconnectSubsystem* Reconnect = GetGameInstance()->GetSubsystem<UGuiyangReconnectSubsystem>())
    {
        Reconnect->OnReconnectStateChanged.RemoveDynamic(this, &ThisClass::HandleReconnectStateChanged);
    }
    if (UGuiyangLobbySubsystem* Lobby = GetGameInstance()->GetSubsystem<UGuiyangLobbySubsystem>())
    {
        Lobby->OnRequestFailed.RemoveDynamic(this, &ThisClass::HandleLobbyRequestFailed);
        Lobby->OnBootstrapUpdated.RemoveDynamic(this, &ThisClass::HandleLobbyBootstrapUpdated);
    }
    Super::NativeDestruct();
}

void UMobileRootHUDWidget::HandleLoginStateChanged(const EGuiyangLoginState State, const FGuiyangLoginProfile& Profile)
{
    // 登录成功先请求 Lobby Bootstrap；未认证状态始终回到登录页。
    if (State == EGuiyangLoginState::LoggedIn)
    {
        if (UGuiyangLoginSubsystem* Login = GetGameInstance()->GetSubsystem<UGuiyangLoginSubsystem>())
        {
            if (AGuiyangMahjongPlayerController* PC = Cast<AGuiyangMahjongPlayerController>(GetOwningPlayer()))
            {
                PC->Server_AuthenticateSession(Profile.PlayerId, Profile.DisplayName, Profile.Provider,
                    Login->GetSessionTokenForNetwork());
            }
        }
        if (IsAwaitingAllocatedRoomState(GetWorld())) ShowCreatingRoom(); else ShowLobby();
        if (UMobileLobbyWidget* Lobby = Cast<UMobileLobbyWidget>(CurrentScreen))
        {
            Lobby->RefreshPlayerInfo(Profile.DisplayName, Profile.PlayerId, 1);
        }
        if (const AGuiyangMahjongGameState* GS = GetWorld() ? GetWorld()->GetGameState<AGuiyangMahjongGameState>() : nullptr)
        {
            RouteFromRoomState(GS->RoomState);
        }
    }
    else if (State == EGuiyangLoginState::LoggedOut || State == EGuiyangLoginState::Expired)
    {
        ShowLogin();
    }
}

void UMobileRootHUDWidget::HandleLoginFailed(const FString& ChineseReason)
{
    ShowChineseError(ChineseReason);
}

void UMobileRootHUDWidget::ShowLogin()
{
    ShowScreenByClassPath(TEXT("/Game/UI/Screens/WBP_Login.WBP_Login_C"));
}

void UMobileRootHUDWidget::ShowConnectServer()
{
    HideReconnectOverlay();
    ShowScreenByClassPath(TEXT("/Game/UI/Screens/WBP_ConnectServer.WBP_ConnectServer_C"));
}

void UMobileRootHUDWidget::ShowLobby()
{
    if (UMobileLobbyWidget* Lobby = Cast<UMobileLobbyWidget>(
        ShowScreenByClassPath(TEXT("/Game/UI/Screens/WBP_Lobby.WBP_Lobby_C"))))
    {
        if (const UGuiyangLoginSubsystem* Login = GetGameInstance()->GetSubsystem<UGuiyangLoginSubsystem>())
        {
            const FGuiyangLoginProfile& Profile = Login->GetCurrentProfile();
            Lobby->RefreshPlayerInfo(Profile.DisplayName, Profile.PlayerId, 1);
        }
    }
    if (UGuiyangLobbySubsystem* LobbySubsystem = GetGameInstance()->GetSubsystem<UGuiyangLobbySubsystem>();
        LobbySubsystem && LobbySubsystem->GetBackendMode() == EGuiyangLobbyBackendMode::RemoteLobby)
    {
        LobbySubsystem->RequestBootstrap(GetOwningPlayer());
    }
}

void UMobileRootHUDWidget::ShowCreatingRoom()
{
    HideReconnectOverlay();
    ShowScreenByClassPath(TEXT("/Game/UI/Screens/WBP_CreatingRoom.WBP_CreatingRoom_C"));
    UE_LOG(LogMahjongUI, Log, TEXT("创建房间加载界面已显示"));
}

void UMobileRootHUDWidget::UpdateCreatingRoomStage(const FString& ChineseStatus)
{
    if (UMobileCreatingRoomWidget* Loading = Cast<UMobileCreatingRoomWidget>(CurrentScreen))
    {
        Loading->SetConnectionStage(ChineseStatus);
    }
}

void UMobileRootHUDWidget::ShowRoom(const FMahjongRoomState& State, const int32 LocalSeat)
{
    if (UMobileMahjongHUDWidget* HUD = Cast<UMobileMahjongHUDWidget>(
        ShowScreenByClassPath(TEXT("/Game/UI/Screens/WBP_GameHUD.WBP_GameHUD_C"))))
    {
        HUD->RefreshRoomState(State, LocalSeat);
    }
}

void UMobileRootHUDWidget::ShowGameHUD()
{
    if (UMobileMahjongHUDWidget* HUD = Cast<UMobileMahjongHUDWidget>(
        ShowScreenByClassPath(TEXT("/Game/UI/Screens/WBP_GameHUD.WBP_GameHUD_C"))))
    {
        if (const AGuiyangMahjongGameState* GS = GetWorld()
            ? GetWorld()->GetGameState<AGuiyangMahjongGameState>() : nullptr)
        {
            HUD->RefreshRoomState(GS->RoomState, FindLocalSeat(GS->RoomState));
        }
    }
}

UUserWidget* UMobileRootHUDWidget::ShowScreenByClassPath(const TCHAR* ClassPath)
{
    // 同类页面直接复用；切换时只移除 ScreenLayer，不影响 PopupLayer 上的全局弹层。
    const bool bShowRoomWorld = FCString::Strifind(ClassPath, TEXT("WBP_GameHUD")) != nullptr;
    if (UWidget* RootBackingLayer = WidgetTree
        ? WidgetTree->FindWidget(TEXT("Scale_BackgroundFill")) : nullptr)
    {
        RootBackingLayer->SetVisibility(
            bShowRoomWorld ? ESlateVisibility::Collapsed : ESlateVisibility::HitTestInvisible);
        UE_LOG(LogMahjongUI, Log, TEXT("Root HUD backing layer %s for screen %s"),
            bShowRoomWorld ? TEXT("collapsed") : TEXT("restored"), ClassPath);
    }
    if (CurrentScreen && CurrentScreenClassPath == ClassPath)
    {
        return CurrentScreen;
    }
    UClass* ScreenClass = LoadClass<UUserWidget>(nullptr, ClassPath);
    if (!ScreenClass)
    {
        UE_LOG(LogMahjongUI, Error, TEXT("页面类加载失败：%s"), ClassPath);
        return nullptr;
    }
    ScreenLayer->ClearChildren();
    CurrentScreen = CreateWidget<UUserWidget>(GetOwningPlayer(), ScreenClass);
    if (!CurrentScreen)
    {
        UE_LOG(LogMahjongUI, Error, TEXT("页面实例创建失败：%s"), ClassPath);
        CurrentScreenClassPath.Reset();
        return nullptr;
    }
    CurrentScreenClassPath = ClassPath;
    if (UOverlaySlot* ScreenSlot = ScreenLayer->AddChildToOverlay(CurrentScreen))
    {
        ScreenSlot->SetHorizontalAlignment(HAlign_Fill);
        ScreenSlot->SetVerticalAlignment(VAlign_Fill);
    }
    return CurrentScreen;
}

int32 UMobileRootHUDWidget::FindLocalSeat(const FMahjongRoomState& State) const
{
    FString PlayerId;
    if (const UGuiyangLoginSubsystem* Login = GetGameInstance()->GetSubsystem<UGuiyangLoginSubsystem>())
    {
        PlayerId = Login->GetCurrentProfile().PlayerId;
    }
    if (!PlayerId.IsEmpty())
    {
        if (const FMahjongSeatInfo* Seat = State.Seats.FindByPredicate([&PlayerId](const FMahjongSeatInfo& Item)
        {
            return Item.bOccupied && Item.PlayerId == PlayerId;
        }))
        {
            return Seat->SeatIndex;
        }
    }
    if (const AGuiyangMahjongPlayerState* PlayerState = GetOwningPlayer()
        ? GetOwningPlayer()->GetPlayerState<AGuiyangMahjongPlayerState>() : nullptr)
    {
        if (!PlayerState->RoomCode.IsEmpty() && PlayerState->RoomCode == State.RoomInfo.RoomId)
        {
            return PlayerState->SeatIndex;
        }
    }
    return INDEX_NONE;
}

void UMobileRootHUDWidget::RouteFromRoomState(const FMahjongRoomState& State)
{
    // 根据权威房间生命周期选择大厅、等待房间或直接覆盖在三维关卡上的游戏 HUD。
    const UGuiyangLoginSubsystem* Login = GetGameInstance()->GetSubsystem<UGuiyangLoginSubsystem>();
    if (!Login || !Login->IsSessionValid())
    {
        ShowLogin();
        return;
    }

    const int32 LocalSeat = FindLocalSeat(State);
    if (LocalSeat == INDEX_NONE)
    {
        if (IsAwaitingAllocatedRoomState(GetWorld()))
        {
            if (CurrentScreenClassPath != TEXT("/Game/UI/Screens/WBP_CreatingRoom.WBP_CreatingRoom_C"))
            {
                ShowCreatingRoom();
            }
            return;
        }
        ShowLobby();
        return;
    }

    switch (State.Lifecycle)
    {
    case EMahjongRoomLifecycle::Creating:
    case EMahjongRoomLifecycle::WaitingForPlayers:
    case EMahjongRoomLifecycle::ReadyCheck:
    case EMahjongRoomLifecycle::Starting:
        ShowRoom(State, LocalSeat);
        break;
    case EMahjongRoomLifecycle::Playing:
    case EMahjongRoomLifecycle::Settlement:
    case EMahjongRoomLifecycle::WaitingNextRound:
        ShowGameHUD();
        break;
    case EMahjongRoomLifecycle::Closing:
    case EMahjongRoomLifecycle::Closed:
    default:
        ShowLobby();
        break;
    }
}

void UMobileRootHUDWidget::HandleRoomStateUpdated(const FMahjongRoomState& State)
{
    RouteFromRoomState(State);
}

void UMobileRootHUDWidget::HandleReconnectRestored(const FMahjongReconnectSnapshot& Snapshot)
{
    HideReconnectOverlay();
    RouteFromRoomState(Snapshot.RoomState);
    if (UMobileMahjongHUDWidget* HUD = Cast<UMobileMahjongHUDWidget>(CurrentScreen))
    {
        HUD->RefreshTableState(Snapshot.TableState);
        HUD->RefreshPrivateHand(Snapshot.PrivateState);
    }
}

void UMobileRootHUDWidget::HandleReconnectStateChanged(const FString& Status, const int32 RemainingSeconds,
    const bool bCanRetry)
{
    // 重连成功立即隐藏遮罩，其余状态只更新同一个覆盖层实例。
    if (Status.IsEmpty())
    {
        HideReconnectOverlay();
        return;
    }
    ShowReconnectOverlay(Status, RemainingSeconds, bCanRetry);
}

void UMobileRootHUDWidget::HandleLobbyRequestFailed(const FString& RequestId,
    const EGuiyangLobbyErrorCode ErrorCode, const FString& ChineseMessage)
{
    if (CurrentScreenClassPath == TEXT("/Game/UI/Screens/WBP_CreatingRoom.WBP_CreatingRoom_C"))
    {
        ShowLobby();
    }
    ShowChineseError(ChineseMessage.IsEmpty() ? TEXT("大厅请求失败，请稍后重试") : ChineseMessage);
}

void UMobileRootHUDWidget::HandleLobbyBootstrapUpdated(const FGuiyangLobbyBootstrap& Bootstrap)
{
    if (UMobileLobbyWidget* Lobby = Cast<UMobileLobbyWidget>(CurrentScreen))
        Lobby->RefreshPlayerInfo(Bootstrap.DisplayName, Bootstrap.PlayerId, Bootstrap.OnlinePlayerCount);
}

void UMobileRootHUDWidget::ShowReconnectOverlay(const FString& Status, const int32 RemainingSeconds,
    const bool bCanRetry)
{
    if (!ReconnectOverlayInstance)
    {
        UClass* ReconnectClass = LoadClass<UMobileReconnectOverlayWidget>(nullptr,
            TEXT("/Game/UI/Dialogs/WBP_ReconnectOverlay.WBP_ReconnectOverlay_C"));
        if (ReconnectClass)
        {
            ReconnectOverlayInstance = CreateWidget<UMobileReconnectOverlayWidget>(GetOwningPlayer(), ReconnectClass);
            PopupLayer->AddChildToOverlay(ReconnectOverlayInstance);
        }
    }
    if (ReconnectOverlayInstance)
    {
        ReconnectOverlayInstance->SetVisibility(ESlateVisibility::Visible);
        ReconnectOverlayInstance->RefreshReconnectState(Status, RemainingSeconds, bCanRetry);
    }
}

void UMobileRootHUDWidget::HideReconnectOverlay()
{
    if (ReconnectOverlayInstance)
    {
        ReconnectOverlayInstance->RemoveFromParent();
        ReconnectOverlayInstance = nullptr;
    }
}

void UMobileRootHUDWidget::ShowChineseError(const FString& ChineseReason)
{
    if (!ErrorToastInstance)
    {
        UClass* ErrorClass = LoadClass<UMobileErrorToastWidget>(nullptr, TEXT("/Game/UI/Components/WBP_ErrorToast.WBP_ErrorToast_C"));
        if (ErrorClass)
        {
            ErrorToastInstance = CreateWidget<UMobileErrorToastWidget>(GetOwningPlayer(), ErrorClass);
            PopupLayer->AddChildToOverlay(ErrorToastInstance);
        }
    }
    if (ErrorToastInstance) ErrorToastInstance->ShowToast(ChineseReason, 3.0f);
}

bool UMobileRootHUDWidget::ApplyVisualReviewScenario(const FString& ScenarioName)
{
#if UE_BUILD_SHIPPING
    return false;
#else
    if (!FParse::Param(FCommandLine::Get(), TEXT("UIReviewScreenshot")))
    {
        return false;
    }

    if (ScenarioName.Equals(TEXT("Login"), ESearchCase::IgnoreCase))
    {
        ShowLogin();
    }
    else if (ScenarioName.Equals(TEXT("Lobby"), ESearchCase::IgnoreCase))
    {
        ShowLobby();
        if (UMobileLobbyWidget* Lobby = Cast<UMobileLobbyWidget>(CurrentScreen))
        {
            Lobby->RefreshPlayerInfo(TEXT("黔小鸡"), TEXT("GY-520001"), 128);
        }
    }
    else if (ScenarioName.Equals(TEXT("CreatingRoom"), ESearchCase::IgnoreCase))
    {
        ShowCreatingRoom();
    }
    else if (ScenarioName.Equals(TEXT("CreateRoomDialog"), ESearchCase::IgnoreCase))
    {
        ShowScreenByClassPath(TEXT("/Game/UI/Dialogs/WBP_CreateRoomDialog.WBP_CreateRoomDialog_C"));
    }
    else if (ScenarioName.Equals(TEXT("JoinRoomDialog"), ESearchCase::IgnoreCase))
    {
        ShowScreenByClassPath(TEXT("/Game/UI/Dialogs/WBP_JoinRoomDialog.WBP_JoinRoomDialog_C"));
    }
    else if (ScenarioName.Equals(TEXT("Room"), ESearchCase::IgnoreCase))
    {
        ShowRoom(MakeReviewRoomState(), 0);
    }
    else if (ScenarioName.Equals(TEXT("RuleConfig"), ESearchCase::IgnoreCase))
    {
        ShowScreenByClassPath(TEXT("/Game/UI/Components/WBP_RuleConfig.WBP_RuleConfig_C"));
    }
    else if (ScenarioName.Equals(TEXT("GameHUD"), ESearchCase::IgnoreCase))
    {
        ShowGameHUD();
        if (UMobileMahjongHUDWidget* HUD = Cast<UMobileMahjongHUDWidget>(CurrentScreen))
        {
            HUD->ApplyVisualReviewState(MakeReviewPublicState(), MakeReviewPrivateState(), MakeReviewActions());
        }
    }
    else if (ScenarioName.Equals(TEXT("Settlement"), ESearchCase::IgnoreCase))
    {
        if (UMobileSettlementWidget* Settlement = Cast<UMobileSettlementWidget>(
            ShowScreenByClassPath(TEXT("/Game/UI/Dialogs/WBP_Settlement.WBP_Settlement_C"))))
        {
            Settlement->SetSettlementResult(MakeReviewSettlement());
        }
    }
    else if (ScenarioName.Equals(TEXT("ErrorToast"), ESearchCase::IgnoreCase))
    {
        if (UMobileErrorToastWidget* Toast = Cast<UMobileErrorToastWidget>(
            ShowScreenByClassPath(TEXT("/Game/UI/Components/WBP_ErrorToast.WBP_ErrorToast_C"))))
        {
            Toast->ShowToast(TEXT("网络波动，请检查连接后重试"), 30.0f);
        }
    }
    else if (ScenarioName.Equals(TEXT("ReconnectOverlay"), ESearchCase::IgnoreCase))
    {
        if (UMobileReconnectOverlayWidget* Overlay = Cast<UMobileReconnectOverlayWidget>(
            ShowScreenByClassPath(TEXT("/Game/UI/Dialogs/WBP_ReconnectOverlay.WBP_ReconnectOverlay_C"))))
        {
            Overlay->RefreshReconnectState(TEXT("网络连接已断开，正在恢复牌局"), 87, true);
        }
    }
    else
    {
        UE_LOG(LogMahjongUI, Error, TEXT("未知 UI 审查场景：%s"), *ScenarioName);
        return false;
    }

    UE_LOG(LogMahjongUI, Display, TEXT("MAHJONG_UI_REVIEW_SCENARIO_READY Screen=%s"), *ScenarioName);
    return true;
#endif
}
