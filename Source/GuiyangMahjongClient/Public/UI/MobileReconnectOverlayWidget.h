#pragma once

#include "CoreMinimal.h"
#include "Blueprint/UserWidget.h"
#include "MobileReconnectOverlayWidget.generated.h"

class UButton; class UTextBlock;

/** 断线重连遮罩。仅发起重连/返回请求，快照恢复由 PlayerController 与 GameState 完成。 */
UCLASS(Abstract, BlueprintType)
class GUIYANGMAHJONGCLIENT_API UMobileReconnectOverlayWidget : public UUserWidget
{
    GENERATED_BODY()
protected:
    /** 构造时绑定重试操作；Tick 仅在显示值变化时刷新倒计时，避免每帧重建文本。 */
    virtual void NativeConstruct() override;
    virtual void NativeTick(const FGeometry& MyGeometry, float InDeltaTime) override;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_ReconnectStatus;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_RemainingTime;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Reconnect;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_BackConnect;
    /** 重试最近路由或返回连接页；两个处理器都不在本地伪造重连成功状态。 */
    UFUNCTION() void HandleReconnect(); UFUNCTION() void HandleBackConnect();
public:
    /** 按服务端剩余窗口刷新遮罩和按钮状态；负数秒数会按已超时展示。 */
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void RefreshReconnectState(const FString& Status, int32 RemainingSeconds, bool bCanRetry);
    /** 把剩余秒数格式化为稳定中文倒计时文本，不读取或修改控件状态。 */
    UFUNCTION(BlueprintPure, Category="麻将|UI") static FString FormatRemainingTime(int32 RemainingSeconds);

private:
    /** 缓存最近渲染值以避免每帧重复写 UMG 属性，仅在控件实例生命周期内有效。 */
    FString LastDisplayedStatus;
    int32 LastDisplayedSeconds = INDEX_NONE;
    bool bLastCanRetry = false;
};
