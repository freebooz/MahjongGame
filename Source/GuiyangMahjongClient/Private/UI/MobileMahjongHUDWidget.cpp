#include "UI/MobileMahjongHUDWidget.h"
#include "UI/MobileActionButtonPanel.h"
#include "UI/MobileDiscardTileWidget.h"
#include "UI/MobileErrorToastWidget.h"
#include "UI/MobileHandTileWidget.h"
#include "UI/MobileRuleSummaryWidget.h"
#include "UI/MobileSettingsWidget.h"
#include "UI/MobileSettlementWidget.h"
#include "Game/Mahjong3DTableActor.h"
#include "Game/GuiyangMahjongGameState.h"
#include "Game/GuiyangMahjongPlayerController.h"
#include "Blueprint/WidgetTree.h"
#include "Game/GuiyangMahjongPlayerState.h"
#include "Components/Button.h"
#include "Components/CanvasPanel.h"
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
#include "Input/Reply.h"
#include "InputCoreTypes.h"
#include "GuiyangMahjong.h"

namespace
{
    void SetAvatarRotationAroundCenter(UImage* Avatar, const float Angle)
    {
        if (!Avatar)
        {
            return;
        }

        // Replace the complete render transform so an authored translation,
        // scale or shear cannot make the portrait orbit around an offset
        // point. The pivot is the exact centre of the square avatar geometry.
        FWidgetTransform Transform;
        Transform.Translation = FVector2D::ZeroVector;
        Transform.Scale = FVector2D(1.0f, 1.0f);
        Transform.Shear = FVector2D::ZeroVector;
        Transform.Angle = Angle;
        Avatar->SetRenderTransformPivot(FVector2D(0.5f, 0.5f));
        Avatar->SetRenderTransform(Transform);
    }

    FString BuildRoomHeaderText(const FMahjongRoomState& State)
    {
        const FMahjongSeatInfo* OwnerSeat =
            State.Seats.FindByPredicate([&State](const FMahjongSeatInfo& Seat)
            {
                return Seat.bOccupied && (Seat.bOwner
                    || (!State.RoomInfo.OwnerPlayerId.IsEmpty()
                        && Seat.PlayerId == State.RoomInfo.OwnerPlayerId));
            });
        const FString OwnerName = OwnerSeat && !OwnerSeat->PlayerName.IsEmpty()
            ? OwnerSeat->PlayerName : TEXT("等待房主");
        return FString::Printf(TEXT("房号  %s  |  房主  %s"),
            *State.RoomInfo.RoomId, *OwnerName);
    }

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
    if (ActionButtonPanel)
    {
        ActionButtonPanel->OnPlayTileRequested.AddUniqueDynamic(
            this, &ThisClass::HandlePlayTileButtonRequested);
        if (UCanvasPanelSlot* ActionSlot =
            Cast<UCanvasPanelSlot>(ActionButtonPanel->Slot))
        {
            ActionSlot->SetAnchors(FAnchors(0.5f, 0.0f));
            ActionSlot->SetAlignment(FVector2D(0.5f, 0.0f));
            ActionSlot->SetPosition(FVector2D(0.0f, 760.0f));
            // A wide transparent host prevents authored-size button images
            // from being clipped while the inner row remains centred.
            ActionSlot->SetSize(FVector2D(1200.0f, 160.0f));
        }
    }
    SetCanvasY(Btn_Ready, 760.0f);
    SetCanvasY(Txt_ReadyStatus, 850.0f);
    const auto PlaceTopRight = [this](const FName WidgetName, const FVector2D Position,
        const FVector2D Size, const FVector2D Alignment)
    {
        if (UWidget* Widget = WidgetTree ? WidgetTree->FindWidget(WidgetName) : nullptr)
        {
            if (UCanvasPanelSlot* Slot = Cast<UCanvasPanelSlot>(Widget->Slot))
            {
                Slot->SetAnchors(FAnchors(1.0f, 0.0f));
                Slot->SetAlignment(Alignment);
                Slot->SetPosition(Position);
                Slot->SetSize(Size);
                Slot->SetZOrder(110);
            }
        }
    };
    const FName MenuImages[] = {
        TEXT("Img_Menu_Rules"), TEXT("Img_Menu_Settings"),
        TEXT("Img_Menu_Trustee"), TEXT("Img_Menu_Exit")
    };
    const FName MenuLabels[] = {
        TEXT("Txt_Menu_Rules"), TEXT("Txt_Menu_Settings"),
        TEXT("Txt_Menu_Trustee"), TEXT("Txt_Menu_Exit")
    };
    for (int32 MenuIndex = 0; MenuIndex < 4; ++MenuIndex)
    {
        const float CenterX = -350.0f + MenuIndex * 105.0f;
        PlaceTopRight(MenuImages[MenuIndex], FVector2D(CenterX, 12.0f),
            FVector2D(64.0f, 64.0f), FVector2D(0.5f, 0.0f));
        PlaceTopRight(MenuLabels[MenuIndex], FVector2D(CenterX, 76.0f),
            FVector2D(92.0f, 38.0f), FVector2D(0.5f, 0.0f));
    }
    PlaceTopRight(TEXT("Btn_ReturnLobby"), FVector2D(-35.0f, 8.0f),
        FVector2D(96.0f, 112.0f), FVector2D(0.5f, 0.0f));
    EnsureTopRightInteractionButtons();
    ApplyPlaceholderAvatars();
    EnsureSeatIndicators();
    if (Txt_RoomId)
    {
        if (UCanvasPanelSlot* RoomSlot = Cast<UCanvasPanelSlot>(Txt_RoomId->Slot))
        {
            RoomSlot->SetAnchors(FAnchors(0.0f, 0.0f));
            RoomSlot->SetAlignment(FVector2D::ZeroVector);
            RoomSlot->SetPosition(FVector2D(20.0f, 20.0f));
            RoomSlot->SetSize(FVector2D(720.0f, 56.0f));
            RoomSlot->SetZOrder(100);
        }
    }
    // Keep the upper-left room header deliberately minimal. Older serialized
    // widget assets may still carry these labels, so collapse them at runtime.
    for (const FName HeaderWidgetName : {
        FName(TEXT("Txt_GameTitle")),
        FName(TEXT("Txt_RoundInfo")),
        FName(TEXT("Txt_BaseScore"))})
    {
        if (UWidget* HeaderWidget =
            WidgetTree ? WidgetTree->FindWidget(HeaderWidgetName) : nullptr)
        {
            HeaderWidget->SetVisibility(ESlateVisibility::Collapsed);
        }
    }
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
    if (Txt_CurrentTurnPlayer)
    {
        Txt_CurrentTurnPlayer->SetVisibility(ESlateVisibility::Collapsed);
    }
    for (const FName LegacyCallWidgetName : {
        FName(TEXT("Border_ReferenceTing")),
        FName(TEXT("Txt_ReferenceTing"))})
    {
        if (UWidget* LegacyCallWidget =
            WidgetTree ? WidgetTree->FindWidget(LegacyCallWidgetName) : nullptr)
        {
            LegacyCallWidget->SetVisibility(ESlateVisibility::Collapsed);
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
        PC->OnTrusteeStateChanged.AddUniqueDynamic(
            this, &ThisClass::HandleTrusteeStateChanged);
        HandleAvailableActions(PC->GetLastAvailableActions());
    }
    if (Btn_Ready)
    {
        Btn_Ready->OnClicked.AddUniqueDynamic(this, &ThisClass::HandleReady);
    }
    if (Btn_ReturnLobby)
    {
        Btn_ReturnLobby->OnClicked.AddUniqueDynamic(this, &ThisClass::HandleReturnLobby);
    }
    if (Btn_MenuRules)
    {
        Btn_MenuRules->OnClicked.AddUniqueDynamic(this, &ThisClass::HandleRules);
    }
    if (Btn_MenuSettings)
    {
        Btn_MenuSettings->OnClicked.AddUniqueDynamic(this, &ThisClass::HandleSettings);
    }
    if (Btn_MenuTrustee)
    {
        Btn_MenuTrustee->OnClicked.AddUniqueDynamic(this, &ThisClass::HandleTrustee);
    }
    UpdateTrusteeMenuLabel();

    // The old 2D tiles remain only as transparent local-hand hit targets. The
    // panel itself must not intercept input, but its tile-button children must
    // stay hit-testable so preview mouse/touch events reach this HUD and can be
    // resolved against the exact projected 3D south-hand mesh.
    Panel_SelfHandTiles->SetRenderOpacity(0.0f);
    Panel_SelfHandTiles->SetVisibility(ESlateVisibility::SelfHitTestInvisible);
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

void UMobileMahjongHUDWidget::EnsureSeatIndicators()
{
    if (!WidgetTree || SeatAvatarImages.Num() == 4)
    {
        return;
    }

    const FName AvatarNames[] = {
        TEXT("Img_Seat_Self"), TEXT("Img_Seat_Right"),
        TEXT("Img_Seat_Top"), TEXT("Img_Seat_Left")
    };
    for (int32 RelativeSeat = 0; RelativeSeat < 4; ++RelativeSeat)
    {
        UImage* Avatar = Cast<UImage>(WidgetTree->FindWidget(AvatarNames[RelativeSeat]));
        UCanvasPanel* ParentCanvas = Avatar ? Cast<UCanvasPanel>(Avatar->GetParent()) : nullptr;
        UCanvasPanelSlot* AvatarSlot = Avatar ? Cast<UCanvasPanelSlot>(Avatar->Slot) : nullptr;
        if (!ParentCanvas || !AvatarSlot)
        {
            SeatAvatarImages.Add(nullptr);
            DealerBadges.Add(nullptr);
            continue;
        }

        // The current-turn cue is the portrait itself. Do not construct the
        // former rotating outer-ring widget.
        SetAvatarRotationAroundCenter(Avatar, 0.0f);
        SeatAvatarImages.Add(Avatar);

        UTextBlock* DealerBadge = WidgetTree->ConstructWidget<UTextBlock>(
            UTextBlock::StaticClass(),
            *FString::Printf(TEXT("Txt_DealerBadge_%d"), RelativeSeat));
        DealerBadge->SetText(FText::FromString(TEXT("庄")));
        DealerBadge->SetColorAndOpacity(FSlateColor(
            FLinearColor(1.0f, 0.72f, 0.18f, 1.0f)));
        DealerBadge->SetJustification(ETextJustify::Center);
        FSlateFontInfo DealerFont = DealerBadge->GetFont();
        DealerFont.Size = 24;
        DealerFont.OutlineSettings.OutlineSize = 2;
        DealerBadge->SetFont(DealerFont);
        DealerBadge->SetVisibility(ESlateVisibility::Collapsed);
        UCanvasPanelSlot* DealerSlot = ParentCanvas->AddChildToCanvas(DealerBadge);
        DealerSlot->SetAnchors(AvatarSlot->GetAnchors());
        DealerSlot->SetAlignment(AvatarSlot->GetAlignment());
        const FVector2D AvatarPosition = AvatarSlot->GetPosition();
        const FVector2D AvatarSize = AvatarSlot->GetSize();
        DealerSlot->SetPosition(FVector2D(
            AvatarPosition.X + (AvatarSize.X - 48.0f) * 0.5f,
            AvatarPosition.Y - 34.0f));
        DealerSlot->SetSize(FVector2D(48.0f, 34.0f));
        DealerSlot->SetZOrder(AvatarSlot->GetZOrder() + 3);
        DealerBadges.Add(DealerBadge);
    }
}

void UMobileMahjongHUDWidget::RefreshSeatIndicators(
    const int32 CurrentTurnSeat, const int32 LocalSeat)
{
    EnsureSeatIndicators();
    const int32 CurrentRelativeSeat =
        GetRelativeSeatIndex(CurrentTurnSeat, LocalSeat);
    const int32 DealerRelativeSeat =
        GetRelativeSeatIndex(CachedDealerSeat, LocalSeat);
    CurrentTurnAvatarIndex =
        CurrentRelativeSeat >= 0 && CurrentRelativeSeat < 4
            ? CurrentRelativeSeat : INDEX_NONE;
    for (int32 RelativeSeat = 0; RelativeSeat < 4; ++RelativeSeat)
    {
        if (SeatAvatarImages.IsValidIndex(RelativeSeat)
            && SeatAvatarImages[RelativeSeat])
        {
            // Non-current portraits must return to their authored orientation.
            SetAvatarRotationAroundCenter(
                SeatAvatarImages[RelativeSeat],
                RelativeSeat == CurrentTurnAvatarIndex
                    ? TurnIndicatorAngle : 0.0f);
        }
        if (DealerBadges.IsValidIndex(RelativeSeat)
            && DealerBadges[RelativeSeat])
        {
            DealerBadges[RelativeSeat]->SetVisibility(
                RelativeSeat == DealerRelativeSeat
                    ? ESlateVisibility::HitTestInvisible
                    : ESlateVisibility::Collapsed);
        }
    }
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
        PC->OnTrusteeStateChanged.RemoveDynamic(
            this, &ThisClass::HandleTrusteeStateChanged);
    }
    if (Btn_Ready)
    {
        Btn_Ready->OnClicked.RemoveDynamic(this, &ThisClass::HandleReady);
    }
    if (Btn_ReturnLobby)
    {
        Btn_ReturnLobby->OnClicked.RemoveDynamic(this, &ThisClass::HandleReturnLobby);
    }
    if (Btn_MenuRules)
    {
        Btn_MenuRules->OnClicked.RemoveDynamic(this, &ThisClass::HandleRules);
    }
    if (Btn_MenuSettings)
    {
        Btn_MenuSettings->OnClicked.RemoveDynamic(this, &ThisClass::HandleSettings);
    }
    if (Btn_MenuTrustee)
    {
        Btn_MenuTrustee->OnClicked.RemoveDynamic(this, &ThisClass::HandleTrustee);
    }
    if (RuleSummaryInstance)
    {
        RuleSummaryInstance->RemoveFromParent();
        RuleSummaryInstance = nullptr;
    }
    if (SettingsInstance)
    {
        SettingsInstance->RemoveFromParent();
        SettingsInstance = nullptr;
    }
    if (ActionButtonPanel)
    {
        ActionButtonPanel->OnPlayTileRequested.RemoveDynamic(
            this, &ThisClass::HandlePlayTileButtonRequested);
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
    if (bExitRequestInFlight)
    {
        return;
    }
    bExitRequestInFlight = true;
    SetTopRightButtonsEnabled(false);
    if (AGuiyangMahjongPlayerController* PC = Cast<AGuiyangMahjongPlayerController>(GetOwningPlayer()))
    {
        PC->ReturnToLobby();
        return;
    }
    bExitRequestInFlight = false;
    SetTopRightButtonsEnabled(true);
}

void UMobileMahjongHUDWidget::HandleRules()
{
    if (RuleSummaryInstance && RuleSummaryInstance->IsInViewport())
    {
        RuleSummaryInstance->RemoveFromParent();
        return;
    }

    if (!RuleSummaryInstance)
    {
        UClass* RuleSummaryClass = LoadClass<UMobileRuleSummaryWidget>(nullptr,
            TEXT("/Game/UI/Components/WBP_RuleSummary.WBP_RuleSummary_C"));
        if (!RuleSummaryClass)
        {
            HandleError(TEXT("无法加载房间规则界面"));
            return;
        }
        RuleSummaryInstance = CreateWidget<UMobileRuleSummaryWidget>(
            GetOwningPlayer(), RuleSummaryClass);
    }
    if (!RuleSummaryInstance)
    {
        HandleError(TEXT("无法创建房间规则界面"));
        return;
    }

    RuleSummaryInstance->SetRuleSnapshot(
        CachedRoomState.RuleSnapshot,
        CachedRoomState.RoomInfo.RoundCount,
        CachedRoomState.RoomInfo.bPasswordProtected);
    RuleSummaryInstance->SetDesiredSizeInViewport(FVector2D(560.0f, 440.0f));
    RuleSummaryInstance->SetPositionInViewport(FVector2D(680.0f, 260.0f), false);
    RuleSummaryInstance->AddToViewport(230);
}

void UMobileMahjongHUDWidget::HandleSettings()
{
    if (SettingsInstance && SettingsInstance->IsInViewport())
    {
        return;
    }
    if (!SettingsInstance)
    {
        UClass* SettingsClass = LoadClass<UMobileSettingsWidget>(nullptr,
            TEXT("/Game/UI/Dialogs/WBP_Settings.WBP_Settings_C"));
        if (!SettingsClass)
        {
            HandleError(TEXT("无法加载本地设置界面"));
            return;
        }
        SettingsInstance = CreateWidget<UMobileSettingsWidget>(
            GetOwningPlayer(), SettingsClass);
    }
    if (!SettingsInstance)
    {
        HandleError(TEXT("无法创建本地设置界面"));
        return;
    }
    SettingsInstance->AddToViewport(240);
}

void UMobileMahjongHUDWidget::HandleTrustee()
{
    if (bTrusteeRequestInFlight)
    {
        return;
    }
    AGuiyangMahjongPlayerController* PC =
        Cast<AGuiyangMahjongPlayerController>(GetOwningPlayer());
    if (!PC)
    {
        return;
    }
    bTrusteeRequestInFlight = true;
    if (Btn_MenuTrustee)
    {
        Btn_MenuTrustee->SetIsEnabled(false);
    }
    PC->Server_RequestSetTrustee(!bLocalTrusteeEnabled);
}

void UMobileMahjongHUDWidget::HandleTrusteeStateChanged(const bool bEnabled)
{
    bLocalTrusteeEnabled = bEnabled;
    bTrusteeRequestInFlight = false;
    UpdateTrusteeMenuLabel();
    if (Btn_MenuTrustee)
    {
        const bool bPlaying =
            CachedRoomState.Lifecycle == EMahjongRoomLifecycle::Playing
            || CachedRoomState.Lifecycle == EMahjongRoomLifecycle::Starting
            || CachedRoomState.Lifecycle == EMahjongRoomLifecycle::WaitingNextRound;
        Btn_MenuTrustee->SetIsEnabled(bPlaying && !bExitRequestInFlight);
    }
}

void UMobileMahjongHUDWidget::EnsureTopRightInteractionButtons()
{
    if (!WidgetTree)
    {
        return;
    }
    UCanvasPanel* RootCanvas = Cast<UCanvasPanel>(WidgetTree->RootWidget);
    if (!RootCanvas)
    {
        UE_LOG(LogMahjongUI, Error,
            TEXT("游戏房间根控件不是画布，无法创建右上角交互按钮"));
        return;
    }

    const auto MakeTransparent = [](UButton* Button)
    {
        if (!Button)
        {
            return;
        }
        FButtonStyle Style = Button->GetStyle();
        Style.Normal.DrawAs = ESlateBrushDrawType::NoDrawType;
        Style.Hovered.DrawAs = ESlateBrushDrawType::NoDrawType;
        Style.Pressed.DrawAs = ESlateBrushDrawType::NoDrawType;
        Style.Disabled.DrawAs = ESlateBrushDrawType::NoDrawType;
        Button->SetStyle(Style);
        Button->SetBackgroundColor(FLinearColor::Transparent);
    };
    const auto AddHitTarget =
        [this, RootCanvas, &MakeTransparent](
            const FName Name, const float CenterX) -> UButton*
    {
        UButton* Button = WidgetTree->ConstructWidget<UButton>(
            UButton::StaticClass(), Name);
        MakeTransparent(Button);
        UCanvasPanelSlot* Slot = RootCanvas->AddChildToCanvas(Button);
        Slot->SetAnchors(FAnchors(1.0f, 0.0f));
        Slot->SetAlignment(FVector2D(0.5f, 0.0f));
        Slot->SetPosition(FVector2D(CenterX, 8.0f));
        Slot->SetSize(FVector2D(96.0f, 112.0f));
        Slot->SetZOrder(120);
        return Button;
    };

    Btn_MenuRules = AddHitTarget(TEXT("Btn_MenuRules_Runtime"), -350.0f);
    Btn_MenuSettings = AddHitTarget(TEXT("Btn_MenuSettings_Runtime"), -245.0f);
    Btn_MenuTrustee = AddHitTarget(TEXT("Btn_MenuTrustee_Runtime"), -140.0f);
    MakeTransparent(Btn_ReturnLobby);
    if (Btn_ReturnLobby && Btn_ReturnLobby->GetContent())
    {
        // The authored icon and label remain visible behind this hit target.
        // Collapse the legacy text child so it cannot duplicate the menu label.
        Btn_ReturnLobby->GetContent()->SetVisibility(ESlateVisibility::Collapsed);
    }
}

void UMobileMahjongHUDWidget::UpdateTrusteeMenuLabel()
{
    if (UTextBlock* TrusteeLabel = WidgetTree
        ? Cast<UTextBlock>(WidgetTree->FindWidget(TEXT("Txt_Menu_Trustee")))
        : nullptr)
    {
        TrusteeLabel->SetText(FText::FromString(
            bLocalTrusteeEnabled ? TEXT("取消托管") : TEXT("托管")));
        TrusteeLabel->SetColorAndOpacity(FSlateColor(
            bLocalTrusteeEnabled
                ? FLinearColor(0.25f, 0.85f, 1.0f, 1.0f)
                : FLinearColor(1.0f, 0.78f, 0.32f, 1.0f)));
    }
}

void UMobileMahjongHUDWidget::SetTopRightButtonsEnabled(const bool bEnabled)
{
    for (UButton* Button :
        {Btn_MenuRules.Get(), Btn_MenuSettings.Get(),
         Btn_MenuTrustee.Get(), Btn_ReturnLobby.Get()})
    {
        if (Button)
        {
            Button->SetIsEnabled(bEnabled);
        }
    }
}

void UMobileMahjongHUDWidget::RefreshRoomState(const FMahjongRoomState& State, const int32 LocalSeat)
{
    CachedRoomState = State;
    CachedDealerSeat = State.RoomInfo.DealerSeat;
    Txt_RoomId->SetText(FText::FromString(BuildRoomHeaderText(State)));
    RefreshSeatIndicators(CachedPublicState.CurrentTurnSeat, LocalSeat);
    if (SettlementInstance
        && (State.Lifecycle == EMahjongRoomLifecycle::Starting
            || State.Lifecycle == EMahjongRoomLifecycle::Playing))
    {
        SettlementInstance->DismissRoundSettlement();
    }

    const bool bReadyStage = State.Lifecycle == EMahjongRoomLifecycle::Creating
        || State.Lifecycle == EMahjongRoomLifecycle::WaitingForPlayers
        || State.Lifecycle == EMahjongRoomLifecycle::ReadyCheck;
    if (Btn_MenuTrustee)
    {
        const bool bTrusteeAllowed =
            State.Lifecycle == EMahjongRoomLifecycle::Starting
            || State.Lifecycle == EMahjongRoomLifecycle::Playing
            || State.Lifecycle == EMahjongRoomLifecycle::WaitingNextRound;
        Btn_MenuTrustee->SetIsEnabled(
            bTrusteeAllowed && !bTrusteeRequestInFlight
            && !bExitRequestInFlight);
    }
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
    TurnIndicatorAngle = FMath::Fmod(
        TurnIndicatorAngle + InDeltaTime * 90.0f, 360.0f);
    for (int32 RelativeSeat = 0;
         RelativeSeat < SeatAvatarImages.Num(); ++RelativeSeat)
    {
        if (UImage* Avatar = SeatAvatarImages[RelativeSeat])
        {
            SetAvatarRotationAroundCenter(
                Avatar,
                RelativeSeat == CurrentTurnAvatarIndex
                    ? TurnIndicatorAngle : 0.0f);
        }
    }
    if (Table3DActor)
    {
        if (APlayerController* PlayerController = GetOwningPlayer())
        {
            Table3DActor->SetHoveredTile(
                Table3DActor->GetLocalHandTileUnderCursor(PlayerController));
        }
    }
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
    RefreshPlayTileButtonState();
    if (bVisualReviewMode)
    {
        Txt_Countdown->SetVisibility(ESlateVisibility::HitTestInvisible);
        Txt_Countdown->SetText(FText::AsNumber(12));
        return;
    }
    const AGuiyangMahjongGameState* GS = GetWorld() ? GetWorld()->GetGameState<AGuiyangMahjongGameState>() : nullptr;
    if (!GS || GS->PublicTableState.ActionDeadlineServerTimeSeconds <= 0.0)
    {
        Txt_Countdown->SetVisibility(ESlateVisibility::Collapsed);
        return;
    }
    const double RemainingTimeSeconds =
        GS->PublicTableState.ActionDeadlineServerTimeSeconds - GS->GetServerWorldTimeSeconds();
    const int32 VisibleCountdownSeconds =
        FMath::Max(0, GS->PublicTableState.ActionTimeoutSeconds);
    if (RemainingTimeSeconds > static_cast<double>(VisibleCountdownSeconds))
    {
        // The server has armed the complete 45-second turn window, but the
        // stopwatch becomes visible only after the first 15 seconds expire.
        Txt_Countdown->SetVisibility(ESlateVisibility::Collapsed);
        return;
    }
    Txt_Countdown->SetVisibility(ESlateVisibility::HitTestInvisible);
    const int32 RemainingSeconds = FMath::Max(0, FMath::CeilToInt(RemainingTimeSeconds));
    Txt_Countdown->SetText(FText::AsNumber(RemainingSeconds));
}

FReply UMobileMahjongHUDWidget::NativeOnPreviewMouseButtonDown(
    const FGeometry& InGeometry, const FPointerEvent& InMouseEvent)
{
    if (InMouseEvent.GetEffectingButton() == EKeys::LeftMouseButton
        && Table3DActor)
    {
        const int32 HitTileId =
            Table3DActor->GetLocalHandTileUnderCursor(GetOwningPlayer());
        if (HitTileId != INDEX_NONE)
        {
            // Lock hover and click to the same screen-space resolved tile ID.
            // This prevents a stale physics hit or previous-frame hover from
            // highlighting one tile while another tile is selected.
            Table3DActor->SetHoveredTile(HitTileId);
            for (int32 ChildIndex = 0;
                 ChildIndex < Panel_SelfHandTiles->GetChildrenCount();
                 ++ChildIndex)
            {
                if (UMobileHandTileWidget* TileWidget =
                    Cast<UMobileHandTileWidget>(
                        Panel_SelfHandTiles->GetChildAt(ChildIndex)))
                {
                    if (TileWidget->GetTileData().UniqueId == HitTileId)
                    {
                        UE_LOG(LogMahjongUI, Verbose,
                            TEXT("Local hand screen hit resolved UniqueId=%d child=%d"),
                            HitTileId, ChildIndex);
                        TileWidget->TriggerTableHitClick();
                        return FReply::Handled();
                    }
                }
            }
        }
    }
    return Super::NativeOnPreviewMouseButtonDown(InGeometry, InMouseEvent);
}

void UMobileMahjongHUDWidget::RefreshTableState(const FMahjongPublicTableState& State)
{
    CachedPublicState = State;
    const int32 LocalSeat = ResolveLocalSeat();
    if (const AGuiyangMahjongGameState* GS = GetWorld() ? GetWorld()->GetGameState<AGuiyangMahjongGameState>() : nullptr)
    {
        Txt_RoomId->SetText(FText::FromString(
            BuildRoomHeaderText(GS->RoomState)));
    }
    Txt_RemainingTileCount->SetText(FText::FromString(FString::Printf(TEXT("剩余：%d"), State.RemainingTileCount)));
    Txt_CurrentPhase->SetText(FText::FromString(FString::Printf(TEXT("阶段：%s"), *GetPhaseDisplayText(State.Phase))));

    RefreshSeatIndicators(State.CurrentTurnSeat, LocalSeat);

    UTextBlock* SeatWidgets[] = {Seat_Self, Seat_Right, Seat_Top, Seat_Left};
    for (int32 RelativeSeat = 0; RelativeSeat < 4; ++RelativeSeat)
    {
        SeatWidgets[RelativeSeat]->SetText(FText::FromString(TEXT("等待玩家")));
    }
    for (const FMahjongSeatInfo& Seat : State.Seats)
    {
        const int32 RelativeSeat = GetRelativeSeatIndex(Seat.SeatIndex, LocalSeat);
        if (RelativeSeat == INDEX_NONE) continue;
        const FString ScoreText = FMath::Abs(Seat.Score) >= 10000
            ? FString::Printf(TEXT("%.2f万"), static_cast<double>(Seat.Score) / 10000.0)
            : FString::Printf(TEXT("%d"), Seat.Score);
        SeatWidgets[RelativeSeat]->SetText(FText::FromString(FString::Printf(
            TEXT("%s\n      %s"), *Seat.PlayerName, *ScoreText)));
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
    int32 DrawnTileId = INDEX_NONE;
    if (bHasPrivateState
        && CachedPrivateState.RoundId == State.RoundId
        && !CachedPrivateState.Hand.Tiles.IsEmpty())
    {
        TSet<int32> PreviousTileIds;
        for (const FMahjongTile& PreviousTile : CachedPrivateState.Hand.Tiles)
        {
            PreviousTileIds.Add(PreviousTile.UniqueId);
        }
        for (const FMahjongTile& NewTile : State.Hand.Tiles)
        {
            if (!PreviousTileIds.Contains(NewTile.UniqueId))
            {
                // A normal draw or replacement draw introduces exactly one
                // new physical tile ID. Automatically make it the current
                // selection so its lift and glow are immediately visible.
                DrawnTileId = NewTile.UniqueId;
                break;
            }
        }
    }
    CachedPrivateState = State;
    bHasPrivateState = State.SeatIndex != INDEX_NONE;
    if (DrawnTileId != INDEX_NONE)
    {
        SelectedHandTileId = DrawnTileId;
    }
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
    const bool bSelectedTileStillExists = CachedPrivateState.Hand.Tiles.ContainsByPredicate(
        [this](const FMahjongTile& Tile)
        {
            return Tile.UniqueId == SelectedHandTileId;
        });
    if (!bSelectedTileStillExists)
    {
        SelectedHandTileId = INDEX_NONE;
    }
    if (!bHasPrivateState) return;
    UClass* TileWidgetClass = LoadClass<UMobileHandTileWidget>(nullptr, TEXT("/Game/UI/Components/WBP_HandTile.WBP_HandTile_C"));
    if (!TileWidgetClass)
    {
        UE_LOG(LogMahjongUI, Warning, TEXT("尚未找到 WBP_HandTile，私有手牌暂不生成可视组件"));
        return;
    }
    const bool bCanPlay =
        CachedPublicState.Phase == EMahjongTablePhase::PlayerTurn
        && CachedPublicState.CurrentTurnSeat == CachedPrivateState.SeatIndex;
    for (int32 TileIndex = 0; TileIndex < CachedPrivateState.Hand.Tiles.Num(); ++TileIndex)
    {
        const FMahjongTile& Tile = CachedPrivateState.Hand.Tiles[TileIndex];
        if (UMobileHandTileWidget* TileWidget = CreateWidget<UMobileHandTileWidget>(GetOwningPlayer(), TileWidgetClass))
        {
            TileWidget->SetTile(Tile);
            TileWidget->OnTileSelected.AddUniqueDynamic(this, &ThisClass::HandleTileSelected);
            TileWidget->OnTileHovered.AddUniqueDynamic(this, &ThisClass::HandleTileHovered);
            TileWidget->OnTileUnhovered.AddUniqueDynamic(this, &ThisClass::HandleTileUnhovered);
            if (Tile.UniqueId == SelectedHandTileId)
            {
                TileWidget->SetSelected(true);
                SelectedHandTile = TileWidget;
            }
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
    if (Table3DActor)
    {
        Table3DActor->SetSelectedTile(SelectedHandTileId);
    }
    RefreshPlayTileButtonState();
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

void UMobileMahjongHUDWidget::RefreshPlayTileButtonState()
{
    if (!ActionButtonPanel)
    {
        return;
    }

    const bool bIsCurrentPlayer = bHasPrivateState
        && CachedPublicState.Phase == EMahjongTablePhase::PlayerTurn
        && CachedPublicState.CurrentTurnSeat == CachedPrivateState.SeatIndex;
    const bool bHasRaisedSelectedTile = bIsCurrentPlayer
        && SelectedHandTile
        && SelectedHandTileId != INDEX_NONE
        && Table3DActor
        && Table3DActor->IsLocalHandTileRaised(SelectedHandTileId);
    if (bIsCurrentPlayer)
    {
        ActionButtonPanel->SetVisibility(
            ESlateVisibility::SelfHitTestInvisible);
    }
    ActionButtonPanel->SetPlayTileState(
        bIsCurrentPlayer, bHasRaisedSelectedTile, SelectedHandTileId);
}

void UMobileMahjongHUDWidget::HandleTileSelected(UMobileHandTileWidget* TileWidget)
{
    const int32 NewSelectedTileId = TileWidget
        ? TileWidget->GetTileData().UniqueId
        : INDEX_NONE;
    const bool bBelongsToLocalSouthHand = TileWidget == nullptr
        || CachedPrivateState.Hand.Tiles.ContainsByPredicate(
            [NewSelectedTileId](const FMahjongTile& Tile)
            {
                return Tile.UniqueId == NewSelectedTileId;
            });
    if (!bBelongsToLocalSouthHand)
    {
        UE_LOG(LogMahjongUI, Warning,
            TEXT("Ignored stale/non-local hand selection UniqueId=%d"),
            NewSelectedTileId);
        return;
    }

    // Restore the previously selected physical tile before applying the new
    // selection. This guarantees that exactly the latest clicked south-hand
    // tile is raised, while clicking the same tile again restores every tile.
    if (Table3DActor)
    {
        Table3DActor->SetSelectedTile(INDEX_NONE);
    }
    SelectedHandTile = TileWidget;
    SelectedHandTileId = NewSelectedTileId;
    for (int32 ChildIndex = 0; ChildIndex < Panel_SelfHandTiles->GetChildrenCount(); ++ChildIndex)
    {
        if (UMobileHandTileWidget* Child = Cast<UMobileHandTileWidget>(Panel_SelfHandTiles->GetChildAt(ChildIndex)))
        {
            Child->SetSelected(Child == TileWidget);
        }
    }
    if (Table3DActor && TileWidget)
    {
        Table3DActor->SetSelectedTile(NewSelectedTileId);
    }
    RefreshPlayTileButtonState();
}

void UMobileMahjongHUDWidget::HandlePlayTileButtonRequested(
    const int32 TileUniqueId)
{
    const bool bIsCurrentPlayer = bHasPrivateState
        && CachedPublicState.Phase == EMahjongTablePhase::PlayerTurn
        && CachedPublicState.CurrentTurnSeat == CachedPrivateState.SeatIndex;
    const bool bIsSelectedAndRaised = SelectedHandTile
        && SelectedHandTileId == TileUniqueId
        && Table3DActor
        && Table3DActor->IsLocalHandTileRaised(TileUniqueId);
    if (!bIsCurrentPlayer || !bIsSelectedAndRaised)
    {
        return;
    }
    const int32 PlayedTileId = TileUniqueId;
    SelectedHandTile = nullptr;
    SelectedHandTileId = INDEX_NONE;
    for (int32 ChildIndex = 0;
         ChildIndex < Panel_SelfHandTiles->GetChildrenCount();
         ++ChildIndex)
    {
        if (UMobileHandTileWidget* Child =
            Cast<UMobileHandTileWidget>(
                Panel_SelfHandTiles->GetChildAt(ChildIndex)))
        {
            Child->SetSelected(false);
        }
    }
    if (Table3DActor)
    {
        // Clear immediately rather than waiting for replicated hand/discard
        // snapshots, so the played tile cannot carry lift/glow into a discard.
        Table3DActor->SetHoveredTile(INDEX_NONE);
        Table3DActor->SetSelectedTile(INDEX_NONE);
    }
    if (AGuiyangMahjongPlayerController* PC =
        Cast<AGuiyangMahjongPlayerController>(GetOwningPlayer()))
    {
        PC->RequestTableAction(EMahjongActionType::Play, PlayedTileId);
    }
    RefreshPlayTileButtonState();
}

void UMobileMahjongHUDWidget::HandleTileHovered(UMobileHandTileWidget* TileWidget)
{
    if (Table3DActor && TileWidget)
    {
        Table3DActor->SetHoveredTile(TileWidget->GetTileData().UniqueId);
    }
}

void UMobileMahjongHUDWidget::HandleTileUnhovered(UMobileHandTileWidget* TileWidget)
{
    if (Table3DActor)
    {
        Table3DActor->SetHoveredTile(INDEX_NONE);
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
void UMobileMahjongHUDWidget::HandleAvailableActions(const TArray<FMahjongAction>& Actions)
{
    if (!ActionButtonPanel)
    {
        return;
    }
    if (!Actions.IsEmpty())
    {
        ActionButtonPanel->SetVisibility(ESlateVisibility::SelfHitTestInvisible);
    }
    ActionButtonPanel->ShowActions(Actions);
}

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
    if (bExitRequestInFlight)
    {
        bExitRequestInFlight = false;
        SetTopRightButtonsEnabled(true);
        const bool bTrusteeAllowed =
            CachedRoomState.Lifecycle == EMahjongRoomLifecycle::Starting
            || CachedRoomState.Lifecycle == EMahjongRoomLifecycle::Playing
            || CachedRoomState.Lifecycle == EMahjongRoomLifecycle::WaitingNextRound;
        if (Btn_MenuTrustee)
        {
            Btn_MenuTrustee->SetIsEnabled(
                bTrusteeAllowed && !bTrusteeRequestInFlight);
        }
    }
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
