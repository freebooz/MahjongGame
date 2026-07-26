#pragma once

#include "CoreMinimal.h"
#include "Blueprint/UserWidget.h"
#include "MobileLobbyWidget.generated.h"

class UButton; class UTextBlock;
class UMobileCreateRoomDialogWidget;
class UMobileJoinRoomDialogWidget;
class UMobileSettingsWidget;

/** 大厅页 C++ 基类。只发起房间请求，不保存或修改权威房间状态。 */
UCLASS(Abstract, BlueprintType)
class GUIYANGMAHJONGCLIENT_API UMobileLobbyWidget : public UUserWidget
{
    GENERATED_BODY()
protected:
    /** 绑定大厅操作并启动 Presence 周期刷新。 */
    virtual void NativeConstruct() override;
    virtual void NativeDestruct() override;
    /** 快速开始、创建、加入和设置入口。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_QuickStart;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_CreateRoom;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_JoinRoom;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Setting;
    /** 登录玩家与在线人数显示。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_PlayerName;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_PlayerId;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_OnlineCount;
    /** 按需创建并复用的大厅弹窗。 */
    UPROPERTY(Transient) TObjectPtr<UMobileCreateRoomDialogWidget> CreateRoomDialogInstance;
    UPROPERTY(Transient) TObjectPtr<UMobileJoinRoomDialogWidget> JoinRoomDialogInstance;
    UPROPERTY(Transient) TObjectPtr<UMobileSettingsWidget> SettingsDialogInstance;
    /** 在线 Presence 刷新定时器。 */
    FTimerHandle PresenceRefreshTimer;
    /** 按钮事件仅发起子系统请求或显示弹窗。 */
    UFUNCTION() void HandleQuickStart();
    UFUNCTION() void HandleCreateRoom();
    UFUNCTION() void HandleJoinRoom();
    UFUNCTION() void HandleSetting();
    /** 确保创建房间弹窗已同步加载，避免首次点击无反馈。 */
    bool EnsureCreateRoomDialog();
    void RefreshOnlinePresence();
public:
    /** 使用登录与 Presence 数据刷新大厅标题栏。 */
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void RefreshPlayerInfo(const FString& PlayerName, const FString& PlayerId, int32 OnlineCount);
};
