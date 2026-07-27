#include "UI/MobileMahjongHUDWidget.h"
#include "UI/MobileActionButtonPanel.h"
#include "UI/MobileDiscardTileWidget.h"
#include "UI/MobileErrorToastWidget.h"
#include "UI/MobileHandTileWidget.h"
#include "UI/MobileSettlementWidget.h"
#include "Game/Mahjong3DTableActor.h"
#include "Game/GuiyangMahjongGameState.h"
#include "Game/GuiyangMahjongPlayerController.h"
#include "Blueprint/WidgetTree.h"
#include "Game/GuiyangMahjongPlayerState.h"
#include "Components/Button.h"
#include "Components/CanvasPanelSlot.h"
#include "Components/HorizontalBox.h"
#include "Components/HorizontalBoxSlot.h"
#include "Components/Image.h"
#include "Components/Overlay.h"
#include "Components/TextBlock.h"
#include "Components/VerticalBox.h"
#include "Components/VerticalBoxSlot.h"
#include "Components/Viewport.h"
#include "Components/Widget.h"
#include "Components/WrapBox.h"
#include "Engine/Texture2D.h"
#include "GuiyangMahjong.h"

namespace
{
    FString MeldTypeText(const EMahjongMeldType Type)
    {
        switch (Type)
        {
        case EMahjongMeldType::Chi: return TEXT("吃");
        case EMahjongMeldType::Peng: return TEXT("碰");
        case EMahjongMeldType::MingGang: return TEXT("明杠");
        case EMahjongMeldType::AnGang: return TEXT("暗杠");
        case EMahjongMeldType::BuGang: return TEXT("补杠");
        default: return TEXT("副露");
        }
    }
}

void UMobileMahjongHUDWidget::NativeConstruct()
{
    Super::NativeConstruct();
    const auto SetCanvasY = [](UWidget* Widget, const float Y)
    {
        if (Widget)
        {
            if (UCanvasPanelSlot* CanvasSlot = Cast<UCanvasPanelSlot>(Widget->Slot))
            {
                const FVector2D Position = CanvasSlot->GetPosition();
                CanvasSlot->SetPosition(FVector2D(Position.X, Y));
            }
        }
    };
    // Keep controls above the enlarged south hand, matching the mobile reference layout.
    SetCanvasY(ActionButtonPanel, 760.0f);
    SetCanvasY(Btn_Ready, 760.0f);
    SetCanvasY(Txt_ReadyStatus, 850.0f);
    ApplyPlaceholderAvatars();
    for (const FName LayerName : {FName(TEXT("Scale_BackgroundFill")),
        FName(TEXT("Background_ComponentSlot"))})
    {
        if (UWidget* LegacyLayer = WidgetTree ? WidgetTree->FindWidget(LayerName) : nullptr)
        {
            // The real room world is the background. Collapse the serialized parent scale layer
            // as well as its old green-gold brush so neither can tint/obscure the 3D scene.
            LegacyLayer->SetVisibility(ESlateVisibility::Collapsed);
            UE_LOG(LogMahjongUI, Log, TEXT("Collapsed legacy room HUD backing layer: %s (%s)"),
                *LayerName.ToString(), *LegacyLayer->GetClass()->GetName());
        }
    }
    if (Table3DViewport)
    {
        // The Mahjong table now renders in the real room world through a CineCameraActor.
        // Keep the legacy widget only for asset compatibility; it must not cover the level.
        Table3DViewport->SetVisibility(ESlateVisibility::Collapsed);
    }
    if (UWidget* LegacyTableCenter =
        WidgetTree ? WidgetTree->FindWidget(TEXT("Panel_TableCenter")) : nullptr)
    {
        // The physical table mesh owns the centre direction disc. Remove the old HUD copy so it
        // cannot cover the disc or render a second set of 北/东/南/西 labels.
        LegacyTableCenter->SetVisibility(ESlateVisibility::Collapsed);
    }
    if (AGuiyangMahjongPlayerController* PC = Cast<AGuiyangMahjongPlayerController>(GetOwningPlayer()))
    {
        Table3DActor = Cast<AMahjong3DTableActor>(PC->EnsureMahjongRoomPresentation());
        PC->OnPrivateHandUpdated.AddUniqueDynamic(this, &ThisClass::HandlePrivateHand);
        PC->OnAvailableActionsUpdated.AddUniqueDynamic(this, &ThisClass::HandleAvailableActions);
        PC->OnSettlementShown.AddUniqueDynamic(this, &ThisClass::HandleSettlement);
        PC->OnFinalSettlementShown.AddUniqueDynamic(this, &ThisClass::HandleFinalSettlement);
        PC->OnErrorShown.AddUniqueDynamic(this, &ThisClass::HandleError);
    }
    if (Btn_Ready)
    {
        Btn_Ready->OnClicked.AddUniqueDynamic(this, &ThisClass::HandleReady);
    }
    if (Btn_ReturnLobby)
    {
        Btn_ReturnLobby->OnClicked.AddUniqueDynamic(this, &ThisClass::HandleReturnLobby);
    }

    // The old 2D tiles remain only as the transparent local-hand hit target. All visuals are 3D.
    Panel_SelfHandTiles->SetRenderOpacity(0.0f);
    Panel_TopHandTiles->SetVisibility(ESlateVisibility::Collapsed);
    Panel_LeftHandTiles->SetVisibility(ESlateVisibility::Collapsed);
    Panel_RightHandTiles->SetVisibility(ESlateVisibility::Collapsed);
    Panel_SelfDiscards->SetVisibility(ESlateVisibility::Collapsed);
    Panel_TopDiscards->SetVisibility(ESlateVisibility::Collapsed);
    Panel_LeftDiscards->SetVisibility(ESlateVisibility::Collapsed);
    Panel_RightDiscards->SetVisibility(ESlateVisibility::Collapsed);
    Panel_SelfMelds->SetVisibility(ESlateVisibility::Collapsed);
    Panel_TopMelds->SetVisibility(ESlateVisibility::Collapsed);
    Panel_LeftMelds->SetVisibility(ESlateVisibility::Collapsed);
    Panel_RightMelds->SetVisibility(ESlateVisibility::Collapsed);
    if (AGuiyangMahjongGameState* GS = GetWorld()->GetGameState<AGuiyangMahjongGameState>())
    {
        GS->OnPublicTableStateUpdated.AddUniqueDynamic(this, &ThisClass::HandlePublicTableState);
        RefreshTableState(GS->PublicTableState);
        RefreshRoomState(GS->RoomState, ResolveLocalSeat());
    }
    UE_LOG(LogMahjongUI, Log, TEXT("牌局 HUD 创建并绑定私有手牌与操作事件"));
}

void UMobileMahjongHUDWidget::ApplyPlaceholderAvatars()
{
    PlaceholderAvatarA = LoadObject<UTexture2D>(nullptr,
        TEXT("/Game/UI/Textures/Avatars/T_PlayerAvatar_Placeholder_A."
             "T_PlayerAvatar_Placeholder_A"));
    PlaceholderAvatarB = LoadObject<UTexture2D>(nullptr,
        TEXT("/Game/UI/Textures/Avatars/T_PlayerAvatar_Placeholder_B."
             "T_PlayerAvatar_Placeholder_B"));
    if (!WidgetTree || !PlaceholderAvatarA || !PlaceholderAvatarB)
    {
        UE_LOG(LogMahjongUI, Warning, TEXT("Player avatar placeholder textures are unavailable"));
        return;
    }

    // The supplied PNGs are 3:2 canvases with a centred transparent portrait.
    // This UV window crops only empty transparency and keeps the complete gold frame.
    const FBox2D PortraitUV(FVector2D(0.25f, 0.125f), FVector2D(0.75f, 0.875f));
    const auto SetAvatar = [this, &PortraitUV](const FName WidgetName, UTexture2D* Texture)
    {
        if (UImage* Image = Cast<UImage>(WidgetTree->FindWidget(WidgetName)))
        {
            FSlateBrush Brush = Image->GetBrush();
            Brush.SetResourceObject(Texture);
            Brush.SetUVRegion(PortraitUV);
            Brush.ImageSize = FVector2D(145.0f, 145.0f);
            Image->SetBrush(Brush);
        }
    };

    SetAvatar(TEXT("Img_Seat_Self"), PlaceholderAvatarA);
    SetAvatar(TEXT("Img_Seat_Top"), PlaceholderAvatarA);
    SetAvatar(TEXT("Img_Seat_Left"), PlaceholderAvatarB);
    SetAvatar(TEXT("Img_Seat_Right"), PlaceholderAvatarB);
}

void UMobileMahjongHUDWidget::NativeDestruct()
{
    // The table belongs to the room world and survives HUD screen transitions.
    Table3DActor = nullptr;
    if (AGuiyangMahjongPlayerController* PC = Cast<AGuiyangMahjongPlayerController>(GetOwningPlayer()))
    {
        PC->OnPrivateHandUpdated.RemoveDynamic(this, &ThisClass::HandlePrivateHand);
        PC->OnAvailableActionsUpdated.RemoveDynamic(this, &ThisClass::HandleAvailableActions);
        PC->OnSettlementShown.RemoveDynamic(this, &ThisClass::HandleSettlement);
        PC->OnFinalSettlementShown.RemoveDynamic(this, &ThisClass::HandleFinalSettlement);
        PC->OnErrorShown.RemoveDynamic(this, &ThisClass::HandleError);
    }
    if (Btn_Ready)
    {
        Btn_Ready->OnClicked.RemoveDynamic(this, &ThisClass::HandleReady);
    }
    if (Btn_ReturnLobby)
    {
        Btn_ReturnLobby->OnClicked.RemoveDynamic(this, &ThisClass::HandleReturnLobby);
    }
    if (AGuiyangMahjongGameState* GS = GetWorld()->GetGameState<AGuiyangMahjongGameState>())
    {
        GS->OnPublicTableStateUpdated.RemoveDynamic(this, &ThisClass::HandlePublicTableState);
    }
    Super::NativeDestruct();
}

void UMobileMahjongHUDWidget::HandleReady()
{
    if (Btn_Ready)
    {
        Btn_Ready->SetIsEnabled(false);
    }
    if (Btn_Ready_Label)
    {
        Btn_Ready_Label->SetText(FText::FromString(TEXT("提交中…")));
    }
    if (AGuiyangMahjongPlayerController* PC = Cast<AGuiyangMahjongPlayerController>(GetOwningPlayer()))
    {
        PC->Server_RequestReady();
    }
}

void UMobileMahjongHUDWidget::HandleReturnLobby()
{
    if (AGuiyangMahjongPlayerController* PC = Cast<AGuiyangMahjongPlayerController>(GetOwningPlayer()))
    {
        PC->ReturnToLobby();
    }
}

void UMobileMahjongHUDWidget::RefreshRoomState(const FMahjongRoomState& State, const int32 LocalSeat)
{
    CachedDealerSeat = State.RoomInfo.DealerSeat;
    Txt_RoomId->SetText(FText::FromString(FString::Printf(
        TEXT("|  房间号  %s"), *State.RoomInfo.RoomId)));
    if (UTextBlock* RoundInfo = Cast<UTextBlock>(WidgetTree->FindWidget(TEXT("Txt_RoundInfo"))))
    {
        RoundInfo->SetText(FText::FromString(FString::Printf(TEXT("|  局数  %d/%d局"),
            FMath::Max(1, State.RoomInfo.CurrentRound), State.RoomInfo.RoundCount)));
    }
    if (UTextBlock* BaseScore = Cast<UTextBlock>(WidgetTree->FindWidget(TEXT("Txt_BaseScore"))))
    {
        BaseScore->SetText(FText::FromString(FString::Printf(
            TEXT("|  底分  %d"), State.RoomInfo.BaseScore)));
    }
    if (UTextBlock* GameTitle = Cast<UTextBlock>(WidgetTree->FindWidget(TEXT("Txt_GameTitle"))))
    {
        GameTitle->SetText(FText::FromString(TEXT("贵州捉鸡麻将")));
    }

    const bool bReadyStage = State.Lifecycle == EMahjongRoomLifecycle::Creating
        || State.Lifecycle == EMahjongRoomLifecycle::WaitingForPlayers
        || State.Lifecycle == EMahjongRoomLifecycle::ReadyCheck;
    if (ActionButtonPanel)
    {
        ActionButtonPanel->SetVisibility(bReadyStage || State.Lifecycle == EMahjongRoomLifecycle::Starting
            ? ESlateVisibility::Collapsed : ESlateVisibility::SelfHitTestInvisible);
    }
    const FMahjongSeatInfo* LocalPlayerSeat = State.Seats.FindByPredicate([LocalSeat](const FMahjongSeatInfo& Seat)
    {
        return Seat.bOccupied && Seat.SeatIndex == LocalSeat;
    });
    const bool bLocalReady = LocalPlayerSeat && LocalPlayerSeat->bReady;

    if (Btn_Ready)
    {
        Btn_Ready->SetVisibility(bReadyStage ? ESlateVisibility::Visible : ESlateVisibility::Collapsed);
        Btn_Ready->SetIsEnabled(bReadyStage && LocalPlayerSeat && !bLocalReady);
    }
    if (Btn_Ready_Label)
    {
        Btn_Ready_Label->SetText(FText::FromString(bLocalReady ? TEXT("已准备") : TEXT("准备")));
    }
    if (Txt_ReadyStatus)
    {
        Txt_ReadyStatus->SetVisibility(bReadyStage || State.Lifecycle == EMahjongRoomLifecycle::Starting
            ? ESlateVisibility::HitTestInvisible : ESlateVisibility::Collapsed);
        Txt_ReadyStatus->SetText(FText::FromString(State.bGameStarting
            || State.Lifecycle == EMahjongRoomLifecycle::Starting
                ? TEXT("四人已就绪，即将开局")
                : bLocalReady ? TEXT("已准备，等待其他玩家") : TEXT("点击准备，满四人后自动开始")));
    }

    if (!bReadyStage && State.Lifecycle != EMahjongRoomLifecycle::Starting)
    {
        return;
    }

    UTextBlock* SeatWidgets[] = {Seat_Self, Seat_Right, Seat_Top, Seat_Left};
    for (int32 RelativeSeat = 0; RelativeSeat < 4; ++RelativeSeat)
    {
        const int32 AbsoluteSeat = LocalSeat >= 0 && LocalSeat < 4
            ? (LocalSeat + RelativeSeat) % 4 : RelativeSeat;
        const FMahjongSeatInfo* Seat = State.Seats.FindByPredicate([AbsoluteSeat](const FMahjongSeatInfo& Item)
        {
            return Item.bOccupied && Item.SeatIndex == AbsoluteSeat;
        });
        SeatWidgets[RelativeSeat]->SetText(FText::FromString(Seat
            ? FString::Printf(TEXT("%s\n      %s"), *Seat->PlayerName,
                Seat->bReady ? TEXT("已准备") : TEXT("未准备"))
            : TEXT("等待玩家")));
    }
}

void UMobileMahjongHUDWidget::NativeTick(const FGeometry& MyGeometry, const float InDeltaTime)
{
    Super::NativeTick(MyGeometry, InDeltaTime);
    if (!Table3DActor)
    {
        // The client presentation Blueprint is loaded asynchronously. Public/private state may
        // already be cached by the HUD before its ChildActor table exists, so acquire it here and
        // immediately replay the latest snapshot instead of waiting for another network update.
        if (AGuiyangMahjongPlayerController* PC =
            Cast<AGuiyangMahjongPlayerController>(GetOwningPlayer()))
        {
            Table3DActor = Cast<AMahjong3DTableActor>(PC->EnsureMahjongRoomPresentation());
            if (Table3DActor)
            {
                Refresh3DTable();
                UE_LOG(LogMahjongUI, Display,
                    TEXT("Applied cached table state after async room presentation became ready"));
            }
        }
    }
    if (bVisualReviewMode)
    {
        Txt_Countdown->SetText(FText::AsNumber(12));
        return;
    }
    const AGuiyangMahjongGameState* GS = GetWorld() ? GetWorld()->GetGameState<AGuiyangMahjongGameState>() : nullptr;
    if (!GS || GS->PublicTableState.ActionDeadlineServerTimeSeconds <= 0.0)
    {
        Txt_Countdown->SetText(FText::FromString(TEXT("--")));
        return;
    }
    const int32 RemainingSeconds = FMath::Max(0, FMath::CeilToInt(
        GS->PublicTableState.ActionDeadlineServerTimeSeconds - GS->GetServerWorldTimeSeconds()));
    Txt_Countdown->SetText(FText::AsNumber(RemainingSeconds));
}

void UMobileMahjongHUDWidget::RefreshTableState(const FMahjongPublicTableState& State)
{
    CachedPublicState = State;
    const int32 LocalSeat = ResolveLocalSeat();
    if (const AGuiyangMahjongGameState* GS = GetWorld() ? GetWorld()->GetGameState<AGuiyangMahjongGameState>() : nullptr)
    {
        Txt_RoomId->SetText(FText::FromString(FString::Printf(
            TEXT("|  房间号  %s"), *GS->RoomState.RoomInfo.RoomId)));
        if (UTextBlock* RoundInfo = Cast<UTextBlock>(WidgetTree->FindWidget(TEXT("Txt_RoundInfo"))))
        {
            RoundInfo->SetText(FText::FromString(FString::Printf(TEXT("|  局数  %d/%d局"),
                FMath::Max(1, GS->RoomState.RoomInfo.CurrentRound),
                GS->RoomState.RoomInfo.RoundCount)));
        }
        if (UTextBlock* BaseScore = Cast<UTextBlock>(WidgetTree->FindWidget(TEXT("Txt_BaseScore"))))
        {
            BaseScore->SetText(FText::FromString(FString::Printf(
                TEXT("|  底分  %d"), GS->RoomState.RoomInfo.BaseScore)));
        }
    }
    Txt_RemainingTileCount->SetText(FText::FromString(FString::Printf(TEXT("剩余：%d"), State.RemainingTileCount)));
    Txt_CurrentPhase->SetText(FText::FromString(FString::Printf(TEXT("阶段：%s"), *GetPhaseDisplayText(State.Phase))));

    const FMahjongSeatInfo* TurnSeat = State.Seats.FindByPredicate([&State](const FMahjongSeatInfo& Seat)
    {
        return Seat.SeatIndex == State.CurrentTurnSeat;
    });
    Txt_CurrentTurnPlayer->SetText(FText::FromString(TurnSeat
        ? FString::Printf(TEXT("当前：%s"), *TurnSeat->PlayerName)
        : TEXT("当前：--")));

    UTextBlock* SeatWidgets[] = {Seat_Self, Seat_Right, Seat_Top, Seat_Left};
    for (int32 RelativeSeat = 0; RelativeSeat < 4; ++RelativeSeat)
    {
        SeatWidgets[RelativeSeat]->SetText(FText::FromString(TEXT("等待玩家")));
    }
    for (const FMahjongSeatInfo& Seat : State.Seats)
    {
        const int32 RelativeSeat = GetRelativeSeatIndex(Seat.SeatIndex, LocalSeat);
        if (RelativeSeat == INDEX_NONE) continue;
        FString StatusText;
        if (Seat.SeatIndex == CachedDealerSeat)
        {
            StatusText += TEXT("  [庄]");
        }
        if (Seat.SeatIndex == State.CurrentTurnSeat)
        {
            StatusText += TEXT("  ▶ 当前出牌");
        }
        const FString ScoreText = FMath::Abs(Seat.Score) >= 10000
            ? FString::Printf(TEXT("%.2f万"), static_cast<double>(Seat.Score) / 10000.0)
            : FString::Printf(TEXT("%d"), Seat.Score);
        SeatWidgets[RelativeSeat]->SetText(FText::FromString(FString::Printf(
            TEXT("%s%s\n      %s"), *Seat.PlayerName, *StatusText, *ScoreText)));
    }
    RefreshOpponentHands(LocalSeat);
    RefreshDiscards(LocalSeat);
    RefreshMelds(LocalSeat);
    RefreshJiDisplay();
    if (bHasPrivateState) RebuildPrivateHand();
    Refresh3DTable();
    UE_LOG(LogMahjongUI, Verbose, TEXT("公共牌桌 UI 刷新：序号=%d"), State.StateSequence);
}

void UMobileMahjongHUDWidget::RefreshPrivateHand(const FMahjongPrivatePlayerState& State)
{
    const int32 PreviousLocalSeat = ResolveLocalSeat();
    CachedPrivateState = State;
    bHasPrivateState = State.SeatIndex != INDEX_NONE;
    if (ResolveLocalSeat() != PreviousLocalSeat)
    {
        RefreshTableState(CachedPublicState);
    }
    else
    {
        RebuildPrivateHand();
    }
    Refresh3DTable();
}

void UMobileMahjongHUDWidget::ApplyVisualReviewState(const FMahjongPublicTableState& PublicState,
    const FMahjongPrivatePlayerState& PrivateState, const TArray<FMahjongAction>& Actions)
{
#if !UE_BUILD_SHIPPING
    bVisualReviewMode = true;
    RefreshPrivateHand(PrivateState);
    RefreshTableState(PublicState);
    ActionButtonPanel->ShowActions(Actions);
    Txt_Countdown->SetText(FText::AsNumber(12));
#endif
}

int32 UMobileMahjongHUDWidget::GetRelativeSeatIndex(const int32 AbsoluteSeat, const int32 LocalSeat)
{
    if (AbsoluteSeat < 0 || AbsoluteSeat >= 4 || LocalSeat < 0 || LocalSeat >= 4)
    {
        return INDEX_NONE;
    }
    return (AbsoluteSeat - LocalSeat + 4) % 4;
}

FString UMobileMahjongHUDWidget::GetPhaseDisplayText(const EMahjongTablePhase Phase)
{
    switch (Phase)
    {
    case EMahjongTablePhase::WaitingForPlayers: return TEXT("等待玩家");
    case EMahjongTablePhase::PreparingGame: return TEXT("准备开局");
    case EMahjongTablePhase::Dealing: return TEXT("发牌");
    case EMahjongTablePhase::PlayerTurn: return TEXT("玩家回合");
    case EMahjongTablePhase::WaitingForAction: return TEXT("等待碰杠胡");
    case EMahjongTablePhase::ResolvingAction: return TEXT("结算操作");
    case EMahjongTablePhase::Settlement: return TEXT("单局结算");
    case EMahjongTablePhase::GameOver: return TEXT("牌局结束");
    case EMahjongTablePhase::Restarting: return TEXT("下一局准备");
    default: return TEXT("未知阶段");
    }
}

int32 UMobileMahjongHUDWidget::ResolveLocalSeat() const
{
    if (bHasPrivateState && CachedPrivateState.SeatIndex >= 0 && CachedPrivateState.SeatIndex < 4)
    {
        return CachedPrivateState.SeatIndex;
    }
    if (const AGuiyangMahjongPlayerState* PlayerState = GetOwningPlayer()
        ? GetOwningPlayer()->GetPlayerState<AGuiyangMahjongPlayerState>() : nullptr)
    {
        if (PlayerState->SeatIndex >= 0 && PlayerState->SeatIndex < 4)
        {
            return PlayerState->SeatIndex;
        }
    }
    return 0;
}

void UMobileMahjongHUDWidget::RebuildPrivateHand()
{
    Panel_SelfHandTiles->ClearChildren();
    SelectedHandTile = nullptr;
    if (Table3DActor)
    {
        Table3DActor->SetSelectedTile(INDEX_NONE);
    }
    if (!bHasPrivateState) return;
    UClass* TileWidgetClass = LoadClass<UMobileHandTileWidget>(nullptr, TEXT("/Game/UI/Components/WBP_HandTile.WBP_HandTile_C"));
    if (!TileWidgetClass)
    {
        UE_LOG(LogMahjongUI, Warning, TEXT("尚未找到 WBP_HandTile，私有手牌暂不生成可视组件"));
        return;
    }
    const bool bCanPlay = CachedPublicState.Phase == EMahjongTablePhase::PlayerTurn
        && CachedPublicState.CurrentTurnSeat == CachedPrivateState.SeatIndex;
    for (int32 TileIndex = 0; TileIndex < CachedPrivateState.Hand.Tiles.Num(); ++TileIndex)
    {
        const FMahjongTile& Tile = CachedPrivateState.Hand.Tiles[TileIndex];
        if (UMobileHandTileWidget* TileWidget = CreateWidget<UMobileHandTileWidget>(GetOwningPlayer(), TileWidgetClass))
        {
            TileWidget->SetTile(Tile, bCanPlay);
            TileWidget->OnTileSelected.AddUniqueDynamic(this, &ThisClass::HandleTileSelected);
            if (UHorizontalBoxSlot* HandSlot = Panel_SelfHandTiles->AddChildToHorizontalBox(TileWidget))
            {
                // 十四张时将最后一张视作摸牌区，参照桌面麻将常见布局留出可辨识间隔。
                if (CachedPrivateState.Hand.Tiles.Num() == 14 && TileIndex == 13)
                {
                    HandSlot->SetPadding(FMargin(20.0f, 0.0f, 0.0f, 0.0f));
                }
            }
        }
    }
    UE_LOG(LogMahjongUI, Log, TEXT("私有手牌 UI 刷新：%d 张，可出牌=%s"),
        CachedPrivateState.Hand.Tiles.Num(), bCanPlay ? TEXT("是") : TEXT("否"));
}

void UMobileMahjongHUDWidget::RefreshOpponentHands(const int32 LocalSeat)
{
    Panel_TopHandTiles->ClearChildren();
    Panel_LeftHandTiles->ClearChildren();
    Panel_RightHandTiles->ClearChildren();

    UTexture2D* BackTexture = LoadObject<UTexture2D>(nullptr,
        TEXT("/Game/UI/Textures/Tiles/T_Tile_Back.T_Tile_Back"));
    if (!BackTexture)
    {
        UE_LOG(LogMahjongUI, Warning, TEXT("未找到对手牌背纹理，跳过暗手展示"));
        return;
    }

    int32 HandCounts[4] = {};
    for (const FMahjongSeatInfo& Seat : CachedPublicState.Seats)
    {
        const int32 RelativeSeat = GetRelativeSeatIndex(Seat.SeatIndex, LocalSeat);
        if (RelativeSeat != INDEX_NONE)
        {
            HandCounts[RelativeSeat] = FMath::Clamp(Seat.HandTileCount, 0, 14);
        }
    }

    FSlateBrush BackBrush;
    BackBrush.SetResourceObject(BackTexture);
    BackBrush.ImageSize = FVector2D(BackTexture->GetSizeX(), BackTexture->GetSizeY());
    BackBrush.DrawAs = ESlateBrushDrawType::Image;

    for (int32 Index = 0; Index < HandCounts[2]; ++Index)
    {
        UImage* TileBack = NewObject<UImage>(this);
        TileBack->SetBrush(BackBrush);
        TileBack->SetDesiredSizeOverride(FVector2D(44.0f, 60.0f));
        if (UHorizontalBoxSlot* HandSlot = Panel_TopHandTiles->AddChildToHorizontalBox(TileBack))
        {
            HandSlot->SetPadding(FMargin(0.0f, 0.0f, -14.0f, 0.0f));
        }
    }

    auto FillVerticalHand = [this, &BackBrush](UVerticalBox* Panel, const int32 Count)
    {
        for (int32 Index = 0; Index < Count; ++Index)
        {
            UImage* TileBack = NewObject<UImage>(this);
            TileBack->SetBrush(BackBrush);
            TileBack->SetDesiredSizeOverride(FVector2D(44.0f, 60.0f));
            if (UVerticalBoxSlot* HandSlot = Panel->AddChildToVerticalBox(TileBack))
            {
                HandSlot->SetPadding(FMargin(0.0f, 0.0f, 0.0f, -34.0f));
            }
        }
    };
    FillVerticalHand(Panel_RightHandTiles, HandCounts[1]);
    FillVerticalHand(Panel_LeftHandTiles, HandCounts[3]);
}

void UMobileMahjongHUDWidget::RefreshDiscards(const int32 LocalSeat)
{
    UWrapBox* DiscardPanels[] = {Panel_SelfDiscards, Panel_RightDiscards, Panel_TopDiscards, Panel_LeftDiscards};
    for (UWrapBox* Panel : DiscardPanels) Panel->ClearChildren();

    UClass* DiscardClass = LoadClass<UMobileDiscardTileWidget>(nullptr,
        TEXT("/Game/UI/Components/WBP_DiscardTile.WBP_DiscardTile_C"));
    if (!DiscardClass) return;

    int32 LatestSequence = INDEX_NONE;
    for (const FMahjongDiscardRecord& Record : CachedPublicState.Discards)
    {
        if (!Record.bClaimed) LatestSequence = FMath::Max(LatestSequence, Record.Sequence);
    }
    for (const FMahjongDiscardRecord& Record : CachedPublicState.Discards)
    {
        if (Record.bClaimed) continue;
        const int32 RelativeSeat = GetRelativeSeatIndex(Record.SeatIndex, LocalSeat);
        if (RelativeSeat == INDEX_NONE) continue;
        if (UMobileDiscardTileWidget* TileWidget = CreateWidget<UMobileDiscardTileWidget>(GetOwningPlayer(), DiscardClass))
        {
            TileWidget->SetDiscard(Record.Tile, Record.Sequence == LatestSequence);
            DiscardPanels[RelativeSeat]->AddChildToWrapBox(TileWidget);
        }
    }
}

void UMobileMahjongHUDWidget::RefreshMelds(const int32 LocalSeat)
{
    UVerticalBox* MeldPanels[] = {Panel_SelfMelds, Panel_RightMelds, Panel_TopMelds, Panel_LeftMelds};
    for (UVerticalBox* Panel : MeldPanels) Panel->ClearChildren();

    UClass* TileClass = LoadClass<UMobileDiscardTileWidget>(nullptr,
        TEXT("/Game/UI/Components/WBP_DiscardTile.WBP_DiscardTile_C"));
    if (!TileClass) return;

    for (const FMahjongMeld& Meld : CachedPublicState.PublicMelds)
    {
        const int32 RelativeSeat = GetRelativeSeatIndex(Meld.OwnerSeat, LocalSeat);
        if (RelativeSeat == INDEX_NONE) continue;
        UHorizontalBox* Row = NewObject<UHorizontalBox>(this);
        UTextBlock* TypeLabel = NewObject<UTextBlock>(this);
        TypeLabel->SetText(FText::FromString(MeldTypeText(Meld.Type)));
        Row->AddChildToHorizontalBox(TypeLabel);
        for (const FMahjongTile& Tile : Meld.Tiles)
        {
            if (UMobileDiscardTileWidget* TileWidget = CreateWidget<UMobileDiscardTileWidget>(GetOwningPlayer(), TileClass))
            {
                TileWidget->SetDiscard(Tile, false);
                Row->AddChildToHorizontalBox(TileWidget);
            }
        }
        MeldPanels[RelativeSeat]->AddChildToVerticalBox(Row);
    }
}

void UMobileMahjongHUDWidget::RefreshJiDisplay()
{
    Txt_FlippedJiTile->SetText(FText::FromString(CachedPublicState.FlippedJiTile.IsValid()
        ? FString::Printf(TEXT("翻鸡：%s"), *CachedPublicState.FlippedJiTile.ToDebugString())
        : TEXT("翻鸡：尚未翻牌")));
    if (CachedPublicState.JiEvents.IsEmpty())
    {
        Txt_JiEvents->SetText(FText::FromString(TEXT("特殊鸡事件：无")));
        return;
    }
    TArray<FString> Lines;
    for (const FMahjongJiEvent& Event : CachedPublicState.JiEvents)
    {
        Lines.Add(Event.Type == EMahjongJiEventType::ChongFeng
            ? FString::Printf(TEXT("冲锋鸡：座位%d · %s · %d单位"),
                Event.ActorSeat, *Event.Tile.ToDebugString(), Event.ValueUnits)
            : FString::Printf(TEXT("责任鸡：座位%d → 座位%d · %d单位"),
                Event.ActorSeat, Event.TargetSeat, Event.ValueUnits));
    }
    Txt_JiEvents->SetText(FText::FromString(FString::Join(Lines, TEXT("\n"))));
}

void UMobileMahjongHUDWidget::HandleTileSelected(UMobileHandTileWidget* TileWidget)
{
    SelectedHandTile = TileWidget;
    for (int32 ChildIndex = 0; ChildIndex < Panel_SelfHandTiles->GetChildrenCount(); ++ChildIndex)
    {
        if (UMobileHandTileWidget* Child = Cast<UMobileHandTileWidget>(Panel_SelfHandTiles->GetChildAt(ChildIndex)))
        {
            Child->SetSelected(Child == TileWidget);
        }
    }
    if (Table3DActor && TileWidget)
    {
        Table3DActor->SetSelectedTile(TileWidget->GetTileData().UniqueId);
    }
}

void UMobileMahjongHUDWidget::Refresh3DTable()
{
    if (Table3DActor)
    {
        Table3DActor->UpdateLayout(CachedPublicState, CachedPrivateState,
            bHasPrivateState, ResolveLocalSeat());
    }
}

void UMobileMahjongHUDWidget::HandlePublicTableState(const FMahjongPublicTableState& State){ RefreshTableState(State); }
void UMobileMahjongHUDWidget::HandlePrivateHand(const FMahjongPrivatePlayerState& State){ RefreshPrivateHand(State); }
void UMobileMahjongHUDWidget::HandleAvailableActions(const TArray<FMahjongAction>& Actions){ ActionButtonPanel->ShowActions(Actions); }

void UMobileMahjongHUDWidget::HandleSettlement(const FMahjongSettlementResult& Result)
{
    if (!SettlementInstance)
    {
        UClass* SettlementClass = LoadClass<UMobileSettlementWidget>(nullptr, TEXT("/Game/UI/Dialogs/WBP_Settlement.WBP_Settlement_C"));
        if (SettlementClass)
        {
            SettlementInstance = CreateWidget<UMobileSettlementWidget>(GetOwningPlayer(), SettlementClass);
            PopupLayer->AddChildToOverlay(SettlementInstance);
        }
    }
    if (SettlementInstance)
    {
        SettlementInstance->SetVisibility(ESlateVisibility::Visible);
        SettlementInstance->SetSettlementResult(Result);
    }
}

void UMobileMahjongHUDWidget::HandleFinalSettlement(const FMahjongFinalSettlementResult& Result)
{
    if (!SettlementInstance)
    {
        UClass* SettlementClass = LoadClass<UMobileSettlementWidget>(nullptr,
            TEXT("/Game/UI/Dialogs/WBP_Settlement.WBP_Settlement_C"));
        if (SettlementClass)
        {
            SettlementInstance = CreateWidget<UMobileSettlementWidget>(GetOwningPlayer(), SettlementClass);
            PopupLayer->AddChildToOverlay(SettlementInstance);
        }
    }
    if (SettlementInstance)
    {
        SettlementInstance->SetVisibility(ESlateVisibility::Visible);
        SettlementInstance->SetFinalSettlementResult(Result);
    }
}

void UMobileMahjongHUDWidget::HandleError(const FString& Message)
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
    if (ErrorToastInstance) ErrorToastInstance->ShowToast(Message);
}
