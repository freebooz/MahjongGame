#pragma once

#include "CoreMinimal.h"
#include "Blueprint/UserWidget.h"
#include "MobileErrorToastWidget.generated.h"

class UBorder; class UTextBlock;

/** 中文错误 Toast。重复提示会刷新文字并重新开始两秒计时。 */
UCLASS(Abstract, BlueprintType)
class GUIYANGMAHJONGCLIENT_API UMobileErrorToastWidget : public UUserWidget
{
    GENERATED_BODY()
protected:
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UBorder> Border_Toast;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_Message;
    /** 当前自动隐藏计时器；新消息出现时会替换旧计时，避免旧回调提前关闭 Toast。 */
    FTimerHandle HideTimer;
    /** 计时结束后隐藏提示，不清除调用方持有的错误状态。 */
    UFUNCTION() void HideToast();
public:
    /** 显示已净化错误文本并重置隐藏计时；持续时间小于等于零时使用安全默认值。 */
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void ShowToast(const FString& Message, float DurationSeconds = 2.0f);
};
