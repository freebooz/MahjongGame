#include "UI/MobileActionButtonPanel.h"
#include "Game/GuiyangMahjongPlayerController.h"
#include "UI/MahjongUISoundLibrary.h"
#include "Components/Button.h"
#include "Components/CanvasPanelSlot.h"
#include "Components/HorizontalBox.h"
#include "Components/HorizontalBoxSlot.h"
#include "GuiyangMahjong.h"

void UMobileActionButtonPanel::NativeConstruct()
{
    Super::NativeConstruct();
    Btn_Hu->OnClicked.AddUniqueDynamic(this, &ThisClass::HandleHu);
    Btn_Gang->OnClicked.AddUniqueDynamic(this, &ThisClass::HandleGang);
    Btn_Peng->OnClicked.AddUniqueDynamic(this, &ThisClass::HandlePeng);
    Btn_Pass->OnClicked.AddUniqueDynamic(this, &ThisClass::HandlePass);

    // Keep the supplied button brushes at their authored size. Rebuild only
    // their order so the response flow reads 过、碰、杠、胡 from left to right.
    if (Panel_Actions)
    {
        Panel_Actions->ClearChildren();
        Panel_Actions->AddChildToHorizontalBox(Btn_Pass);
        Panel_Actions->AddChildToHorizontalBox(Btn_Peng);
        Panel_Actions->AddChildToHorizontalBox(Btn_Gang);
        Panel_Actions->AddChildToHorizontalBox(Btn_Hu);
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
}

void UMobileActionButtonPanel::CentreVisibleButtons()
{
    if (!Panel_Actions)
    {
        return;
    }

    TArray<UButton*> VisibleButtons;
    for (UButton* Button : {Btn_Pass.Get(), Btn_Peng.Get(),
        Btn_Gang.Get(), Btn_Hu.Get()})
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
    Btn_Hu->SetVisibility(Has(EMahjongActionType::Hu) ? ESlateVisibility::Visible : ESlateVisibility::Collapsed);
    Btn_Gang->SetVisibility((Has(EMahjongActionType::MingGang) || Has(EMahjongActionType::AnGang) || Has(EMahjongActionType::BuGang)) ? ESlateVisibility::Visible : ESlateVisibility::Collapsed);
    Btn_Peng->SetVisibility(Has(EMahjongActionType::Peng) ? ESlateVisibility::Visible : ESlateVisibility::Collapsed);
    Btn_Pass->SetVisibility(Actions.IsEmpty() ? ESlateVisibility::Collapsed : ESlateVisibility::Visible);
    CentreVisibleButtons();
    UE_LOG(LogMahjongUI, Log, TEXT("操作按钮面板刷新：服务端下发 %d 项"), Actions.Num());
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
