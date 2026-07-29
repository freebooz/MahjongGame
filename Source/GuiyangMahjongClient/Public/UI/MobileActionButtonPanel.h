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
    virtual void NativeConstruct() override;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Hu;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Gang;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Peng;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Pass;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UHorizontalBox> Panel_Actions;
    UFUNCTION() void HandleHu(); UFUNCTION() void HandleGang(); UFUNCTION() void HandlePeng(); UFUNCTION() void HandlePass();
    void SendAction(EMahjongActionType Type);
    void CentreVisibleButtons();
    TArray<FMahjongAction> CurrentActions;
public:
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void ShowActions(const TArray<FMahjongAction>& Actions);
};
