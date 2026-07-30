#pragma once

#include "CoreMinimal.h"
#include "Blueprint/UserWidget.h"
#include "Network/MahjongNetworkTypes.h"
#include "MobileRoomWidget.generated.h"

class UButton; class UTextBlock;
class UMobileRuleSummaryWidget;

/** 房间页 C++ 基类。房间显示数据来自 GameState 的 OnRep_RoomState。 */
UCLASS(Abstract, BlueprintType)
class GUIYANGMAHJONGCLIENT_API UMobileRoomWidget : public UUserWidget
{
    GENERATED_BODY()
protected:
    /** 视图构造后绑定准备与返回按钮，房间内容等待 GameState 复制事件后再刷新。 */
    virtual void NativeConstruct() override;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_RoomId;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_RuleSummary;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UMobileRuleSummaryWidget> RuleSummary;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Seat_Top;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Seat_Left;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Seat_Right;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Seat_Bottom;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Ready;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_ReturnLobby;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_StartTip;
    /** 将准备和返回意图交给 PlayerController；房间成员与生命周期仍由服务端校验。 */
    UFUNCTION() void HandleReady();
    UFUNCTION() void HandleReturnLobby();
public:
    /** 以一份完整房间快照刷新所有座位和提示，LocalSeat 用于计算屏幕相对方位。 */
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void RefreshRoomState(const FMahjongRoomState& State, int32 LocalSeat);
    /** 将屏幕相对方位（南、东、北、西）转换为服务端绝对座位号。 */
    static int32 GetAbsoluteSeatForRelativePosition(int32 RelativePosition, int32 LocalSeat);
};
