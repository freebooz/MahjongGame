#include "UI/MobileHandTileWidget.h"
#include "UI/MahjongTileVisualLibrary.h"
#include "UI/MahjongUISoundLibrary.h"
#include "Components/Button.h"
#include "Components/TextBlock.h"
#include "Engine/Texture2D.h"
#include "GuiyangMahjong.h"

void UMobileHandTileWidget::NativeConstruct()
{
    Super::NativeConstruct();
    Btn_Tile->OnClicked.AddUniqueDynamic(this, &ThisClass::HandleTileClicked);
    Btn_Tile->OnHovered.AddUniqueDynamic(this, &ThisClass::HandleTileHovered);
    Btn_Tile->OnUnhovered.AddUniqueDynamic(this, &ThisClass::HandleTileUnhovered);
}

void UMobileHandTileWidget::SetTile(const FMahjongTile& Tile, const bool bInCanPlay)
{
    TileData = Tile;
    bCanPlay = bInCanPlay;
    SetSelected(false);
    FSlateBrush NormalBrush;
    if (UMahjongTileVisualLibrary::ConfigureFaceBrush(Tile, NormalBrush))
    {
        auto MakeFaceBrush = [&Tile](const FLinearColor& Tint)
        {
            FSlateBrush Brush;
            UMahjongTileVisualLibrary::ConfigureFaceBrush(Tile, Brush, Tint);
            return Brush;
        };
        FButtonStyle Style = Btn_Tile->GetStyle();
        Style.SetNormal(NormalBrush);
        Style.SetHovered(MakeFaceBrush(FLinearColor(1.0f, 0.96f, 0.74f, 1.0f)));
        Style.SetPressed(MakeFaceBrush(FLinearColor(0.86f, 0.76f, 0.52f, 1.0f)));
        Style.SetDisabled(MakeFaceBrush(FLinearColor(0.48f, 0.52f, 0.50f, 0.88f)));
        Btn_Tile->SetStyle(Style);
        Txt_TileName->SetVisibility(ESlateVisibility::Collapsed);
    }
    else
    {
        Txt_TileName->SetText(FText::FromString(Tile.ToDebugString()));
        Txt_TileName->SetVisibility(ESlateVisibility::HitTestInvisible);
    }
    // Selection is available at all times; playing is a separate, turn-gated action.
    Btn_Tile->SetIsEnabled(Tile.IsValid());
}

void UMobileHandTileWidget::SetSelected(const bool bInSelected)
{
    bSelected = bInSelected;
    // The visible hand is the 3D table model. Keep this transparent UMG hit
    // target fixed so selecting a tile does not move the clickable area onto a
    // neighbouring tile; AMahjong3DTableActor performs the visible 5 cm lift.
    SetRenderTranslation(FVector2D::ZeroVector);
}

void UMobileHandTileWidget::HandleTileClicked()
{
    if (bSelected)
    {
        if (bCanPlay)
        {
            UMahjongUISoundLibrary::PlayUISound(this, EMahjongUISound::TilePlay);
            OnPlayRequested.Broadcast(this);
        }
        else
        {
            SetSelected(false);
            OnTileSelected.Broadcast(nullptr);
        }
        return;
    }

    if (!bSelected)
    {
        UMahjongUISoundLibrary::PlayUISound(this, EMahjongUISound::TileSelect);
        SetSelected(true);
        OnTileSelected.Broadcast(this);
        UE_LOG(LogMahjongUI, Log, TEXT("选中手牌：%s"), *TileData.ToDebugString());
        return;
    }
}

void UMobileHandTileWidget::HandleTileHovered()
{
    OnTileHovered.Broadcast(this);
}

void UMobileHandTileWidget::HandleTileUnhovered()
{
    OnTileUnhovered.Broadcast(this);
}
