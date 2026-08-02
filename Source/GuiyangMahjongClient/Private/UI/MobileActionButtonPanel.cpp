#include "UI/MobileActionButtonPanel.h"
#include "Game/GuiyangMahjongPlayerController.h"
#include "UI/MahjongUISoundLibrary.h"
#include "Blueprint/WidgetTree.h"
#include "Components/Button.h"
#include "Components/CanvasPanelSlot.h"
#include "Components/HorizontalBox.h"
#include "Components/HorizontalBoxSlot.h"
#include "Components/TextBlock.h"
#include "Engine/Texture2D.h"
#include "GuiyangMahjong.h"

namespace
{
    FSlateBrush MakePlayButtonBrush(const TCHAR* AssetPath)
    {
        FSlateBrush Brush;
        if (UTexture2D* Texture = LoadObject<UTexture2D>(nullptr, AssetPath))
        {
            Brush.SetResourceObject(Texture);
        }
        Brush.ImageSize = FVector2D(192.0f, 192.0f);
        Brush.DrawAs = ESlateBrushDrawType::Box;
        Brush.Margin = FMargin(0.1458f);
        return Brush;
    }

    void ConfigureRuntimePlayButton(UButton* Button, UTextBlock* Label)
    {
        if (!Button || !Label)
        {
            return;
        }

        FButtonStyle Style;
        Style.SetNormal(MakePlayButtonBrush(
            TEXT("/Game/UI/Textures/Buttons/T_Btn_PlayTile_Normal.T_Btn_PlayTile_Normal")));
        Style.SetHovered(MakePlayButtonBrush(
            TEXT("/Game/UI/Textures/Buttons/T_Btn_PlayTile_Hovered.T_Btn_PlayTile_Hovered")));
        Style.SetPressed(MakePlayButtonBrush(
            TEXT("/Game/UI/Textures/Buttons/T_Btn_PlayTile_Pressed.T_Btn_PlayTile_Pressed")));
        Style.SetDisabled(MakePlayButtonBrush(
            TEXT("/Game/UI/Textures/Buttons/T_Btn_PlayTile_Disabled.T_Btn_PlayTile_Disabled")));
        Button->SetStyle(Style);

        Label->SetText(FText::FromString(TEXT("出牌")));
        Label->SetJustification(ETextJustify::Center);
        Label->SetColorAndOpacity(FSlateColor(
            FLinearColor(1.0f, 0.97f, 0.78f, 1.0f)));
        FSlateFontInfo Font = Label->GetFont();
        Font.Size = 28;
        Label->SetFont(Font);
        Label->SetVisibility(ESlateVisibility::HitTestInvisible);
        Button->AddChild(Label);
    }
}

void UMobileActionButtonPanel::NativeConstruct()
{
    Super::NativeConstruct();
    if (!Btn_PlayTile && WidgetTree)
    {
        Btn_PlayTile = WidgetTree->ConstructWidget<UButton>(
            UButton::StaticClass(), TEXT("Btn_PlayTile_Runtime"));
        UTextBlock* Label = WidgetTree->ConstructWidget<UTextBlock>(
            UTextBlock::StaticClass(), TEXT("Btn_PlayTile_Runtime_Label"));
        ConfigureRuntimePlayButton(Btn_PlayTile, Label);
    }
    Btn_Hu->OnClicked.AddUniqueDynamic(this, &ThisClass::HandleHu);
    Btn_Gang->OnClicked.AddUniqueDynamic(this, &ThisClass::HandleGang);
    Btn_Peng->OnClicked.AddUniqueDynamic(this, &ThisClass::HandlePeng);
    Btn_Pass->OnClicked.AddUniqueDynamic(this, &ThisClass::HandlePass);
    if (Btn_PlayTile)
    {
        Btn_PlayTile->OnClicked.AddUniqueDynamic(
            this, &ThisClass::HandlePlayTile);
    }

    // Keep the supplied button brushes at their authored size. Rebuild only
    // their order so the response flow reads 过、碰、杠、胡 from left to right.
    if (Panel_Actions)
    {
        Panel_Actions->ClearChildren();
        Panel_Actions->AddChildToHorizontalBox(Btn_Pass);
        Panel_Actions->AddChildToHorizontalBox(Btn_Peng);
        Panel_Actions->AddChildToHorizontalBox(Btn_Gang);
        Panel_Actions->AddChildToHorizontalBox(Btn_Hu);
        if (Btn_PlayTile)
        {
            Panel_Actions->AddChildToHorizontalBox(Btn_PlayTile);
        }
        if (UCanvasPanelSlot* PanelSlot =
            Cast<UCanvasPanelSlot>(Panel_Actions->Slot))
        {
            PanelSlot->SetAnchors(FAnchors(0.5f, 0.5f));
            PanelSlot->SetAlignment(FVector2D(0.5f, 0.5f));
            PanelSlot->SetPosition(FVector2D::ZeroVector);
            PanelSlot->SetAutoSize(true);
        }
    }
    ShowActions({});
    SetPlayTileState(false, false, INDEX_NONE);
}

void UMobileActionButtonPanel::CentreVisibleButtons()
{
    if (!Panel_Actions)
    {
        return;
    }

    TArray<UButton*> VisibleButtons;
    for (UButton* Button : {Btn_Pass.Get(), Btn_Peng.Get(),
        Btn_Gang.Get(), Btn_Hu.Get(), Btn_PlayTile.Get()})
    {
        if (Button && Button->GetVisibility() != ESlateVisibility::Collapsed)
        {
            VisibleButtons.Add(Button);
        }
    }

    for (int32 Index = 0; Index < VisibleButtons.Num(); ++Index)
    {
        if (UHorizontalBoxSlot* ButtonSlot =
            Cast<UHorizontalBoxSlot>(VisibleButtons[Index]->Slot))
        {
            ButtonSlot->SetSize(FSlateChildSize(ESlateSizeRule::Automatic));
            // Two neighbouring 10 px half-gaps make an exact 20 px gap.
            ButtonSlot->SetPadding(FMargin(
                Index > 0 ? 10.0f : 0.0f,
                0.0f,
                Index + 1 < VisibleButtons.Num() ? 10.0f : 0.0f,
                0.0f));
            ButtonSlot->SetHorizontalAlignment(HAlign_Center);
            ButtonSlot->SetVerticalAlignment(VAlign_Center);
        }
    }
    Panel_Actions->InvalidateLayoutAndVolatility();
}

void UMobileActionButtonPanel::ShowActions(const TArray<FMahjongAction>& Actions)
{
    CurrentActions = Actions;
    auto Has = [&Actions](const EMahjongActionType Type){ return Actions.ContainsByPredicate([Type](const FMahjongAction& A){ return A.Type == Type; }); };
    const bool bHasHu = Has(EMahjongActionType::Hu);
    const bool bHasGang = Has(EMahjongActionType::MingGang)
        || Has(EMahjongActionType::AnGang)
        || Has(EMahjongActionType::BuGang);
    const bool bHasPeng = Has(EMahjongActionType::Peng);
    const auto ApplyResponseButtonState = [](UButton* Button, const bool bVisible)
    {
        if (!Button)
        {
            return;
        }
        // 旧蓝图可能保存过禁用或透明状态；权威候选到达时必须显式恢复可见、可点击状态。
        Button->SetVisibility(bVisible ? ESlateVisibility::Visible : ESlateVisibility::Collapsed);
        Button->SetIsEnabled(bVisible);
        Button->SetRenderOpacity(1.0f);
    };
    SetRenderOpacity(1.0f);
    if (Panel_Actions)
    {
        Panel_Actions->SetVisibility(ESlateVisibility::Visible);
        Panel_Actions->SetRenderOpacity(1.0f);
    }
    ApplyResponseButtonState(Btn_Hu, bHasHu);
    ApplyResponseButtonState(Btn_Gang, bHasGang);
    ApplyResponseButtonState(Btn_Peng, bHasPeng);
    ApplyResponseButtonState(Btn_Pass, !Actions.IsEmpty());
    CentreVisibleButtons();
    ForceLayoutPrepass();
    UE_LOG(LogMahjongUI, Log,
        TEXT("操作按钮面板刷新：服务端下发 %d 项，碰=%s，杠=%s，胡=%s"),
        Actions.Num(), bHasPeng ? TEXT("是") : TEXT("否"),
        bHasGang ? TEXT("是") : TEXT("否"), bHasHu ? TEXT("是") : TEXT("否"));
}

void UMobileActionButtonPanel::SetPlayTileState(
    const bool bVisible, const bool bEnabled, const int32 TileUniqueId)
{
    if (!Btn_PlayTile)
    {
        return;
    }

    const int32 NewSelectedPlayTileId =
        bVisible && bEnabled ? TileUniqueId : INDEX_NONE;
    const ESlateVisibility NewVisibility =
        bVisible ? ESlateVisibility::Visible : ESlateVisibility::Collapsed;
    const bool bNewEnabled = bVisible && bEnabled
        && NewSelectedPlayTileId != INDEX_NONE;
    if (Btn_PlayTile->GetVisibility() == NewVisibility
        && Btn_PlayTile->GetIsEnabled() == bNewEnabled
        && SelectedPlayTileId == NewSelectedPlayTileId)
    {
        return;
    }

    SelectedPlayTileId = NewSelectedPlayTileId;
    Btn_PlayTile->SetVisibility(NewVisibility);
    Btn_PlayTile->SetIsEnabled(bNewEnabled);
    CentreVisibleButtons();
}

void UMobileActionButtonPanel::SendAction(const EMahjongActionType Type)
{
    const FMahjongAction* Offered = CurrentActions.FindByPredicate([Type](const FMahjongAction& A)
    {
        if (Type != EMahjongActionType::MingGang) return A.Type == Type;
        return A.Type == EMahjongActionType::MingGang || A.Type == EMahjongActionType::AnGang || A.Type == EMahjongActionType::BuGang;
    });
    if (!Offered && Type != EMahjongActionType::Pass) return;
    if (AGuiyangMahjongPlayerController* PC = Cast<AGuiyangMahjongPlayerController>(GetOwningPlayer()))
    {
        const EMahjongActionType RequestedType = Offered ? Offered->Type : EMahjongActionType::Pass;
        const EMahjongUISound SoundType = RequestedType == EMahjongActionType::Hu ? EMahjongUISound::Hu
            : RequestedType == EMahjongActionType::Peng ? EMahjongUISound::Peng
            : (RequestedType == EMahjongActionType::MingGang || RequestedType == EMahjongActionType::AnGang
                || RequestedType == EMahjongActionType::BuGang) ? EMahjongUISound::Gang
            : EMahjongUISound::Pass;
        UMahjongUISoundLibrary::PlayUISound(this, SoundType);
        PC->RequestTableAction(RequestedType,
            Offered ? Offered->TargetTile.UniqueId : INDEX_NONE);
    }
    ShowActions({});
}

void UMobileActionButtonPanel::HandleHu(){ SendAction(EMahjongActionType::Hu); }
void UMobileActionButtonPanel::HandleGang(){ SendAction(EMahjongActionType::MingGang); }
void UMobileActionButtonPanel::HandlePeng(){ SendAction(EMahjongActionType::Peng); }
void UMobileActionButtonPanel::HandlePass(){ SendAction(EMahjongActionType::Pass); }
void UMobileActionButtonPanel::HandlePlayTile()
{
    if (SelectedPlayTileId != INDEX_NONE)
    {
        OnPlayTileRequested.Broadcast(SelectedPlayTileId);
    }
}
