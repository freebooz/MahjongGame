#pragma once

#include "CoreMinimal.h"
#include "Blueprint/UserWidget.h"
#include "Core/MahjongTypes.h"
#include "MobileHandTileWidget.generated.h"

class UButton; class UTextBlock;
class UMobileHandTileWidget;

DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FMahjongHandTileSelected, UMobileHandTileWidget*, TileWidget);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FMahjongHandTileHovered, UMobileHandTileWidget*, TileWidget);

/** 可交互手牌组件：悬停高亮，首次点击上浮，再次点击恢复。 */
UCLASS(Abstract, BlueprintType)
class GUIYANGMAHJONGCLIENT_API UMobileHandTileWidget : public UUserWidget
{
    GENERATED_BODY()
protected:
    /** 视图构造后绑定点击与悬停事件，初始状态保持不可出牌直到权威数据到达。 */
    virtual void NativeConstruct() override;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Tile;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_TileName;
    /** 点击、悬停和离开事件只更新本地选择表现；真正出牌仍通过权威请求执行。 */
    UFUNCTION() void HandleTileClicked();
    UFUNCTION() void HandleTileHovered();
    UFUNCTION() void HandleTileUnhovered();
    /** 当前牌值及交互状态由 HUD 每次权威快照刷新，不在控件中持久化。 */
    UPROPERTY(BlueprintReadOnly) FMahjongTile TileData;
    UPROPERTY(BlueprintReadOnly) bool bSelected = false;
public:
    /** 选择与悬停事件由拥有该控件的 HUD 消费；手牌本身不再触发出牌。 */
    UPROPERTY(BlueprintAssignable, Category="麻将|UI") FMahjongHandTileSelected OnTileSelected;
    /** 使用权威牌数据刷新显示和可出状态；不会自动触发选择或出牌。 */
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void SetTile(const FMahjongTile& Tile);
    /** 切换纯视觉选择状态，用于 HUD 保证同一时刻最多一张牌上浮。 */
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void SetSelected(bool bInSelected);
    void TriggerTableHitClick() { HandleTileClicked(); }
    UPROPERTY(BlueprintAssignable, Category="Mahjong|UI") FMahjongHandTileHovered OnTileHovered;
    UPROPERTY(BlueprintAssignable, Category="Mahjong|UI") FMahjongHandTileHovered OnTileUnhovered;
    const FMahjongTile& GetTileData() const { return TileData; }
};
