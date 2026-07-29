#pragma once

#include "CoreMinimal.h"
#include "Blueprint/UserWidget.h"
#include "Core/MahjongTypes.h"
#include "MobileHandTileWidget.generated.h"

class UButton; class UTextBlock;
class UMobileHandTileWidget;

DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FMahjongHandTileSelected, UMobileHandTileWidget*, TileWidget);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FMahjongHandTileHovered, UMobileHandTileWidget*, TileWidget);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FMahjongHandTilePlayRequested, UMobileHandTileWidget*, TileWidget);

/** 可交互手牌组件：悬停高亮，首次点击上浮，再次点击恢复。 */
UCLASS(Abstract, BlueprintType)
class GUIYANGMAHJONGCLIENT_API UMobileHandTileWidget : public UUserWidget
{
    GENERATED_BODY()
protected:
    virtual void NativeConstruct() override;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Tile;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_TileName;
    UFUNCTION() void HandleTileClicked();
    UFUNCTION() void HandleTileHovered();
    UFUNCTION() void HandleTileUnhovered();
    UPROPERTY(BlueprintReadOnly) FMahjongTile TileData;
    UPROPERTY(BlueprintReadOnly) bool bSelected = false;
    UPROPERTY(BlueprintReadOnly) bool bCanPlay = false;
    /** Shared desktop double-click / mobile double-tap interval. */
    UPROPERTY(EditDefaultsOnly, Category="Mahjong|Input",
        meta=(ClampMin="0.20", ClampMax="0.60"))
    float DoubleClickIntervalSeconds = 0.35f;
    double LastClickTimeSeconds = -1.0;
public:
    UPROPERTY(BlueprintAssignable, Category="麻将|UI") FMahjongHandTileSelected OnTileSelected;
    UPROPERTY(BlueprintAssignable, Category="Mahjong|UI") FMahjongHandTilePlayRequested OnPlayRequested;
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void SetTile(const FMahjongTile& Tile, bool bInCanPlay);
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void SetSelected(bool bInSelected);
    void TriggerTableHitClick() { HandleTileClicked(); }
    UPROPERTY(BlueprintAssignable, Category="Mahjong|UI") FMahjongHandTileHovered OnTileHovered;
    UPROPERTY(BlueprintAssignable, Category="Mahjong|UI") FMahjongHandTileHovered OnTileUnhovered;
    const FMahjongTile& GetTileData() const { return TileData; }
};
