#pragma once

#include "CoreMinimal.h"
#include "Blueprint/UserWidget.h"
#include "Core/MahjongTypes.h"
#include "MobileDiscardTileWidget.generated.h"

class UBorder; class UTextBlock;

/** 只读弃牌组件，不响应点击；牌面始终朝向本地玩家，座位方向由四方分区表达。 */
UCLASS(Abstract, BlueprintType)
class GUIYANGMAHJONGCLIENT_API UMobileDiscardTileWidget : public UUserWidget
{
    GENERATED_BODY()
protected:
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UBorder> Border_Tile;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_TileName;
public:
    /** 用只读牌数据刷新牌面，并按 bLatest 标记最近弃牌；不保留牌对象引用。 */
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void SetDiscard(const FMahjongTile& Tile, bool bLatest);
};
