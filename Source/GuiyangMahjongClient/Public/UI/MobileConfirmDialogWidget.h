#pragma once

#include "CoreMinimal.h"
#include "Blueprint/UserWidget.h"
#include "MobileConfirmDialogWidget.generated.h"

class UButton;
class UTextBlock;

DECLARE_DYNAMIC_MULTICAST_DELEGATE(FMahjongConfirmAccepted);
DECLARE_DYNAMIC_MULTICAST_DELEGATE(FMahjongConfirmCancelled);

/** 通用中文确认弹窗，只产生确认/取消事件，不直接执行房间或牌局操作。 */
UCLASS(Abstract, BlueprintType)
class GUIYANGMAHJONGCLIENT_API UMobileConfirmDialogWidget : public UUserWidget
{
    GENERATED_BODY()

protected:
    /** 视图构造完成后绑定确认与取消按钮，业务动作仍由外部订阅者执行。 */
    virtual void NativeConstruct() override;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_Title;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_Message;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Confirm;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Cancel;
    /** 转发一次确认或取消事件并关闭弹窗；实际高危操作必须由调用方再次执行权限校验。 */
    UFUNCTION() void HandleConfirm();
    UFUNCTION() void HandleCancel();

public:
    /** 弹窗结果事件只表达用户意图，不携带管理权限或业务执行结果。 */
    UPROPERTY(BlueprintAssignable, Category="麻将|UI") FMahjongConfirmAccepted OnConfirmed;
    UPROPERTY(BlueprintAssignable, Category="麻将|UI") FMahjongConfirmCancelled OnCancelled;
    /** 写入本次确认标题和正文；调用方负责提供已脱敏的用户可见文本。 */
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void Configure(const FString& Title, const FString& Message);
};
