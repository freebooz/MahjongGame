#pragma once

#include "CoreMinimal.h"
#include "Game/GuiyangClientControllerBridge.h"
#include "GuiyangClientControllerBridgeImpl.generated.h"

class AGuiyangMahjongPlayerController;
class AMahjong3DTableActor;
class AMahjongRoomCameraActor;
class AMahjongRoomPresentationActor;
struct FStreamableHandle;
class UMobileRootHUDWidget;

/** 共享 PlayerController 在客户端目标中的 UI、旅行和三维表现实现。 */
UCLASS(Transient)
class UGuiyangClientControllerBridgeImpl final : public UObject, public IGuiyangClientControllerBridge
{
    GENERATED_BODY()

public:
    /** 销毁时取消异步加载和定时器，避免回调访问失效 Controller。 */
    virtual void BeginDestroy() override;
    /** 由所属 PlayerController 提供当前 World。 */
    virtual UWorld* GetWorld() const override;
    /** 绑定控制器并启动 HUD/房间表现初始化。 */
    virtual void InitializeClient(AGuiyangMahjongPlayerController& InController) override;
    /** 创建或复用本地房间表现、牌桌和摄像机。 */
    virtual AActor* EnsureRoomPresentation() override;
    /** 普通连接、Allocator 路由连接及重试入口。 */
    virtual void ConnectToServer(const FString& ServerIP, int32 Port, const FString& PlayerName) override;
    virtual void ConnectToAllocatedServer(const FGuiyangGameServerRoute& Route) override;
    virtual void RetryLastConnection() override;
    /** 切换客户端界面并执行返回连接页/大厅的旅行。 */
    virtual void ReturnToConnectScreen() override;
    virtual void ReturnToLobby() override;
    /** 立即展示创建房间进度，避免远程调用期间界面无反馈。 */
    virtual void ShowCreatingRoomLoading() override;
    virtual void RequestCreateRoomWithLoading(const FMahjongCreateRoomRequest& Request) override;
    virtual void CompleteRemoteReturnToLobby() override;
    /** 将网络恢复和结算数据同步给根 HUD。 */
    virtual void NotifyReconnectRestored(const FMahjongReconnectSnapshot& Snapshot) override;
    virtual void NotifyFinalSettlement(const FMahjongFinalSettlementResult& Result) override;
    virtual void HandleIntegrationPrivateState(const FMahjongPrivatePlayerState& PrivateState) override;

private:
    /** 桥接生命周期内的控制器和根 HUD 强引用。 */
    UPROPERTY(Transient) TObjectPtr<AGuiyangMahjongPlayerController> Controller;
    UPROPERTY(Transient) TObjectPtr<UMobileRootHUDWidget> RootHUDInstance;
    /** 当前房间的本地表现对象；均不在服务端复制。 */
    UPROPERTY(Transient) TObjectPtr<AMahjongRoomPresentationActor> RoomPresentationActor;
    UPROPERTY(Transient) TObjectPtr<AMahjong3DTableActor> RoomTableActor;
    UPROPERTY(Transient) TObjectPtr<AMahjongRoomCameraActor> RoomCameraActor;
    /** 等待最短加载展示时间后使用的远程游戏服路由。 */
    UPROPERTY(Transient) FGuiyangGameServerRoute PendingAllocatedRoute;
    /** 延迟旅行定时器和异步表现蓝图加载句柄。 */
    FTimerHandle CreatingRoomTravelDelayTimer;
    TSharedPtr<FStreamableHandle> PresentationLoadHandle;
    /** 加载层出现时间和表现资源失败状态。 */
    double CreatingRoomLoadingShownAtSeconds = 0.0;
    bool bPresentationLoadFailed = false;

    /** 异步加载配置中的表现蓝图，并在完成后生成 Actor。 */
    void RequestRoomPresentationClassLoad();
    void HandleRoomPresentationClassLoaded();
    AMahjongRoomPresentationActor* SpawnRoomPresentation(UClass& PresentationClass);
    AMahjongRoomCameraActor* EnsureRoomCamera();
    /** 将本地 PlayerController 的视角切换到房间预定摄像机。 */
    void ApplyRoomPresentationViewTarget();
    /** 满足最短加载展示时间后执行 ClientTravel。 */
    void CompleteDelayedAllocatedServerConnection();
    void TravelToAllocatedServer(FGuiyangGameServerRoute Route);
};
