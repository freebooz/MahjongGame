#pragma once

#include "CoreMinimal.h"
#include "Lobby/GuiyangLobbyTypes.h"
#include "Network/MahjongNetworkTypes.h"
#include "GuiyangClientControllerBridge.generated.h"

class AGuiyangMahjongPlayerController;

UINTERFACE(MinimalAPI)
class UGuiyangClientControllerBridge : public UInterface
{
    GENERATED_BODY()
};

/** 隔离共享网络控制器与客户端 UI/关卡表现实现的接口。 */
class GUIYANGMAHJONG_API IGuiyangClientControllerBridge
{
    GENERATED_BODY()

public:
    /** 建立与共享 PlayerController 的生命周期关联。 */
    virtual void InitializeClient(AGuiyangMahjongPlayerController& Controller) = 0;
    /** 确保本地三维房间表现根 Actor 已创建。 */
    virtual AActor* EnsureRoomPresentation() = 0;
    /** 连接普通地址或由 Allocator 返回的已分配游戏服。 */
    virtual void ConnectToServer(const FString& ServerIP, int32 Port, const FString& PlayerName) = 0;
    virtual void ConnectToAllocatedServer(const FGuiyangGameServerRoute& Route) = 0;
    /** 重试、返回连接页或返回大厅。 */
    virtual void RetryLastConnection() = 0;
    virtual void ReturnToConnectScreen() = 0;
    virtual void ReturnToLobby() = 0;
    /** 立即展示创建房间加载层，再发起异步创建流程。 */
    virtual void ShowCreatingRoomLoading() = 0;
    virtual void RequestCreateRoomWithLoading(const FMahjongCreateRoomRequest& Request) = 0;
    virtual void CompleteRemoteReturnToLobby() = 0;
    /** 把重连快照、最终结算或集成测试私有状态传给客户端表现层。 */
    virtual void NotifyReconnectRestored(const FMahjongReconnectSnapshot& Snapshot) = 0;
    virtual void NotifyFinalSettlement(const FMahjongFinalSettlementResult& Result) = 0;
    virtual void HandleIntegrationPrivateState(const FMahjongPrivatePlayerState& PrivateState) = 0;
};

/** 客户端模块注册的桥接对象工厂。 */
using FGuiyangClientBridgeFactory = TFunction<UObject*(AGuiyangMahjongPlayerController&)>;

/** 进程级桥接工厂注册表，使共享模块不必静态依赖客户端模块。 */
class GUIYANGMAHJONG_API FGuiyangClientBridgeRegistry
{
public:
    /** 客户端模块启动/卸载时注册或注销工厂。 */
    static void Register(FGuiyangClientBridgeFactory Factory);
    static void Unregister();
    static UObject* Create(AGuiyangMahjongPlayerController& Controller);
};
