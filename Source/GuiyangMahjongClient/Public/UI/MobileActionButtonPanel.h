#pragma once

#include "CoreMinimal.h"
#include "Blueprint/UserWidget.h"
#include "Core/MahjongTypes.h"
#include "MobileActionButtonPanel.generated.h"

class UButton;
class UHorizontalBox;

/** 服务端可操作列表面板。服务端未下发的按钮始终隐藏，客户端不自行推导操作。 */
UCLASS(Abstract, BlueprintType)
class GUIYANGMAHJONGCLIENT_API UMobileActionButtonPanel : public UUserWidget
{
    GENERATED_BODY()
protected:
    /** 视图构造后绑定四个操作按钮；重复构造必须避免累加同一点击委托。 */
    virtual void NativeConstruct() override;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Hu;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Gang;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Peng;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Pass;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UHorizontalBox> Panel_Actions;
    /** 四个按钮仅把当前权威动作映射回统一请求；不存在对应动作时处理器不得发送 RPC。 */
    UFUNCTION() void HandleHu(); UFUNCTION() void HandleGang(); UFUNCTION() void HandlePeng(); UFUNCTION() void HandlePass();
    /** 查找当前动作列表中的匹配项并交给 PlayerController，失败时保持面板状态不变。 */
    void SendAction(EMahjongActionType Type);
    /** 根据实际可见按钮重新居中，避免隐藏按钮仍占据移动端触摸布局空间。 */
    void CentreVisibleButtons();
    /** 最近一次服务端下发的动作集合；仅在本控件生命周期内有效。 */
    TArray<FMahjongAction> CurrentActions;
public:
    /** 用新权威动作集合刷新按钮可见性；空数组会隐藏整个操作区。 */
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void ShowActions(const TArray<FMahjongAction>& Actions);
};
