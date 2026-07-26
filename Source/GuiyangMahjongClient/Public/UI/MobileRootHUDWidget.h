#pragma once

#include "CoreMinimal.h"
#include "Blueprint/UserWidget.h"
#include "Auth/GuiyangLoginTypes.h"
#include "Lobby/GuiyangLobbyTypes.h"
#include "Network/MahjongNetworkTypes.h"
#include "MobileRootHUDWidget.generated.h"

class UMobileErrorToastWidget;
class UMobileReconnectOverlayWidget;
class UOverlay;

/** 全局 UI 路由层，负责页面切换和弹层，不持有任何牌局权威状态。 */
UCLASS(Abstract, BlueprintType)
class GUIYANGMAHJONGCLIENT_API UMobileRootHUDWidget : public UUserWidget
{
    GENERATED_BODY()

protected:
    /** 建立登录、Lobby、房间与重连事件订阅。 */
    virtual void NativeConstruct() override;
    virtual void NativeDestruct() override;

    /** 互斥页面层与可叠加弹窗层。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UOverlay> ScreenLayer;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UOverlay> PopupLayer;
    /** 当前页面、错误提示和重连遮罩实例。 */
    UPROPERTY(Transient) TObjectPtr<UUserWidget> CurrentScreen;
    UPROPERTY(Transient) TObjectPtr<UMobileErrorToastWidget> ErrorToastInstance;
    UPROPERTY(Transient) TObjectPtr<UMobileReconnectOverlayWidget> ReconnectOverlayInstance;
    /** 当前页面类路径，用于阻止重复创建同一页面。 */
    FString CurrentScreenClassPath;

    /** 各子系统事件统一汇入根路由层。 */
    UFUNCTION() void HandleLoginStateChanged(EGuiyangLoginState State, const FGuiyangLoginProfile& Profile);
    UFUNCTION() void HandleLoginFailed(const FString& ChineseReason);
    UFUNCTION() void HandleRoomStateUpdated(const FMahjongRoomState& State);
    UFUNCTION() void HandleReconnectRestored(const FMahjongReconnectSnapshot& Snapshot);
    UFUNCTION() void HandleReconnectStateChanged(const FString& Status, int32 RemainingSeconds, bool bCanRetry);
    UFUNCTION() void HandleLobbyRequestFailed(const FString& RequestId,
        EGuiyangLobbyErrorCode ErrorCode, const FString& ChineseMessage);
    UFUNCTION() void HandleLobbyBootstrapUpdated(const FGuiyangLobbyBootstrap& Bootstrap);

public:
    /** 显式切换登录、连接、大厅、创建中、房间和游戏 HUD 页面。 */
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void ShowLogin();
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void ShowConnectServer();
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void ShowLobby();
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void ShowCreatingRoom();
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void UpdateCreatingRoomStage(const FString& ChineseStatus);
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void ShowRoom(const FMahjongRoomState& State, int32 LocalSeat);
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void ShowGameHUD();
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void ShowChineseError(const FString& ChineseReason);

    /** 仅供 -UIReviewScreenshot 本地可视化审查使用，不修改账号或服务端权威状态。 */
    bool ApplyVisualReviewScenario(const FString& ScenarioName);

private:
    /** 根据软类路径创建页面，并保证 ScreenLayer 同时只有一个页面。 */
    UUserWidget* ShowScreenByClassPath(const TCHAR* ClassPath);
    /** 显示/隐藏全局重连遮罩。 */
    void ShowReconnectOverlay(const FString& Status, int32 RemainingSeconds, bool bCanRetry);
    void HideReconnectOverlay();
    void RouteFromRoomState(const FMahjongRoomState& State);
    /** 从当前登录玩家 ID 查找绝对座位。 */
    int32 FindLocalSeat(const FMahjongRoomState& State) const;
};
