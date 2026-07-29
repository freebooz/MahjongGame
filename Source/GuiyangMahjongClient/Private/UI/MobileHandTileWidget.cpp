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
    LastClickTimeSeconds = -1.0;
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
    // neighbouring tile; AMahjong3DTableActor performs the visible 2.5 cm lift.
    SetRenderTranslation(FVector2D::ZeroVector);
}

void UMobileHandTileWidget::HandleTileClicked()
{
    const double NowSeconds = FPlatformTime::Seconds();
    const bool bDoubleClick = LastClickTimeSeconds >= 0.0
        && NowSeconds - LastClickTimeSeconds
            <= static_cast<double>(DoubleClickIntervalSeconds);
    LastClickTimeSeconds = bDoubleClick ? -1.0 : NowSeconds;

    // A double-click is a play gesture only when this exact tile is already
    // selected (and therefore visibly raised) and it is the local player's
    // turn. An unselected tile can never be played by the double-click itself.
    // For every other player both clicks remain ordinary selection toggles.
    if (bDoubleClick && bCanPlay && bSelected)
    {
        UMahjongUISoundLibrary::PlayUISound(this, EMahjongUISound::TilePlay);
        OnPlayRequested.Broadcast(this);
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

    // Single click/tap on the already-selected south hand tile restores it.
    SetSelected(false);
    OnTileSelected.Broadcast(nullptr);
}

void UMobileHandTileWidget::HandleTileHovered()
{
    OnTileHovered.Broadcast(this);
}

void UMobileHandTileWidget::HandleTileUnhovered()
{
    OnTileUnhovered.Broadcast(this);
}
