#pragma once

#include "CoreMinimal.h"
#include "GameFramework/PlayerController.h"
#include "Core/MahjongTypes.h"
#include "Auth/GuiyangLoginTypes.h"
#include "Network/MahjongNetworkTypes.h"
#include "Lobby/GuiyangLobbyTypes.h"
#include "GuiyangMahjongPlayerController.generated.h"

DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FMahjongPrivateHandUpdated, const FMahjongPrivatePlayerState&, State);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FMahjongAvailableActionsUpdated, const TArray<FMahjongAction>&, Actions);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FMahjongSettlementShown, const FMahjongSettlementResult&, Result);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FMahjongErrorShown, const FString&, Message);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FMahjongReconnectRestored, const FMahjongReconnectSnapshot&, Snapshot);
DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FMahjongFinalSettlementShown, const FMahjongFinalSettlementResult&, Result);

class IGuiyangClientControllerBridge;
class IGuiyangServerRequestHandler;

/**
 * 固定白名单 RPC 方法的只读进程级快照。
 * 名称只允许来自源码常量，统计生命周期与 Dedicated Server 进程一致，不持久化玩家标识。
 */
struct GUIYANGMAHJONG_API FGuiyangRpcMethodTelemetry
{
    /** 固定方法名，例如 Server.RequestAction。 */
    FString MethodName;
    /** 自进程启动以来的接收、拒绝、失败和超时累计数。 */
    uint64 ReceivedCount = 0;
    uint64 RejectedCount = 0;
    uint64 FailedCount = 0;
    uint64 TimeoutCount = 0;
    /** 最近 256 次调用的 P95/P99 同步处理耗时，单位毫秒。 */
    double P95DurationMilliseconds = 0.0;
    double P99DurationMilliseconds = 0.0;
};

/** 共享的可复制玩家控制器；客户端表现与服务端权威处理分别由目标专用模块注入。 */
UCLASS()
class GUIYANGMAHJONG_API AGuiyangMahjongPlayerController : public APlayerController
{
    GENERATED_BODY()

public:
    /** 房间使用固定桌面镜头，禁止引擎在 Pawn/旁观者状态变化时自动抢回 ViewTarget。 */
    AGuiyangMahjongPlayerController();

    /** 确保本地房间表现 Actor 存在，并返回其引用。 */
    AActor* EnsureMahjongRoomPresentation();

    /** 使用 Lobby/Allocator 下发的路由连接指定独立游戏服。 */
    UFUNCTION(BlueprintCallable, Category="Mahjong|Network")
    void ConnectToAllocatedServer(const FGuiyangGameServerRoute& Route);

    /** 私有手牌、可执行动作、结算、错误和重连状态的 UI 事件。 */
    UPROPERTY(BlueprintAssignable, Category="Mahjong|UI") FMahjongPrivateHandUpdated OnPrivateHandUpdated;
    UPROPERTY(BlueprintAssignable, Category="Mahjong|UI") FMahjongAvailableActionsUpdated OnAvailableActionsUpdated;
    UPROPERTY(BlueprintAssignable, Category="Mahjong|UI") FMahjongSettlementShown OnSettlementShown;
    UPROPERTY(BlueprintAssignable, Category="Mahjong|UI") FMahjongErrorShown OnErrorShown;
    UPROPERTY(BlueprintAssignable, Category="Mahjong|UI") FMahjongReconnectRestored OnReconnectRestored;
    UPROPERTY(BlueprintAssignable, Category="Mahjong|UI") FMahjongFinalSettlementShown OnFinalSettlementShown;

    /** 直接连接指定地址；主要用于本地开发和回退路径。 */
    UFUNCTION(BlueprintCallable, Category="Mahjong|Network")
    void ConnectToServer(const FString& ServerIP, int32 Port, const FString& PlayerName);
    /** 重试最近一次连接或退回连接/大厅界面。 */
    UFUNCTION(BlueprintCallable, Category="Mahjong|Network") void RetryLastConnection();
    UFUNCTION(BlueprintCallable, Category="Mahjong|Network") void ReturnToConnectScreen();
    UFUNCTION(BlueprintCallable, Category="Mahjong|Network") void ReturnToLobby();
    /** 先立即显示加载层，再通过 Lobby 创建并分配远程房间。 */
    UFUNCTION(BlueprintCallable, Category="Mahjong|Lobby")
    void RequestCreateRoomWithLoading(const FMahjongCreateRoomRequest& Request);
    UFUNCTION(BlueprintCallable, Category="Mahjong|UI") void ShowCreatingRoomLoading();
    void CompleteRemoteReturnToLobby();
    /** 发送带客户端单调序号的统一牌桌动作请求。 */
    UFUNCTION(BlueprintCallable, Category="Mahjong|Table")
    void RequestTableAction(EMahjongActionType Type, int32 TargetTileId);

    /** 由客户端调用、在权威服务器执行的大厅与牌桌 RPC。 */
    UFUNCTION(Server, Reliable) void Server_RequestCreateRoom();
    UFUNCTION(Server, Reliable) void Server_RequestQuickStart();
    UFUNCTION(Server, Reliable) void Server_AuthenticateSession(const FString& PlayerId, const FString& DisplayName,
        EGuiyangLoginProvider Provider, const FString& SessionToken);
    UFUNCTION(Server, Reliable) void Server_RequestCreateRoomWithConfig(const FMahjongCreateRoomRequest& Request);
    UFUNCTION(Server, Reliable) void Server_RequestJoinRoom(const FString& PlayerName);
    UFUNCTION(Server, Reliable) void Server_RequestJoinRoomByCode(const FMahjongJoinRoomRequest& Request);
    UFUNCTION(Server, Reliable) void Server_RequestReady();
    UFUNCTION(Server, Reliable) void Server_RequestLeaveRoom();
    UFUNCTION(Server, Reliable) void Server_RequestNextRound();
    UFUNCTION(Server, Reliable) void Server_RequestPlayTile(FMahjongTile Tile);
    UFUNCTION(Server, Reliable) void Server_RequestAction(FMahjongActionRequest Request);
    UFUNCTION(Server, Reliable) void Server_RequestIntegrationDisconnect();

    /** 由服务器定向发送给所属客户端的私有状态与结果 RPC。 */
    UFUNCTION(Client, Reliable) void Client_UpdatePrivateHand(const FMahjongPrivatePlayerState& PrivateState);
    UFUNCTION(Client, Reliable) void Client_ShowAvailableActions(const TArray<FMahjongAction>& Actions);
    UFUNCTION(Client, Reliable) void Client_ShowSettlement(const FMahjongSettlementResult& Result);
    UFUNCTION(Client, Reliable) void Client_ShowErrorMessage(const FString& Message);
    UFUNCTION(Client, Reliable) void Client_RestoreReconnectSnapshot(
        const FMahjongReconnectSnapshot& Snapshot, const TArray<FMahjongAction>& AvailableActions);
    UFUNCTION(Client, Reliable) void Client_ShowFinalSettlement(const FMahjongFinalSettlementResult& Result);

    /** 返回下一次连接时附带的玩家显示名。 */
    UFUNCTION(BlueprintPure, Category="Mahjong|Network")
    const FString& GetPendingPlayerName() const { return PendingPlayerName; }
    void SetPendingPlayerName(const FString& PlayerName) { PendingPlayerName = PlayerName; }
    /** Latest authoritative reaction offer, retained across HUD reconstruction. */
    const TArray<FMahjongAction>& GetLastAvailableActions() const { return LastAvailableActions; }
    /** Dedicated Server 进程自启动以来已进入的服务端 RPC 处理器次数。 */
    static uint64 GetServerRpcReceivedCount() { return ServerRpcReceivedCount.Load(); }
    /** 复制固定方法白名单的分类统计，调用方可安全地在心跳序列化期间读取。 */
    static TArray<FGuiyangRpcMethodTelemetry> GetServerRpcTelemetry();

protected:
    /** 客户端启动时创建桥接对象；服务端则保持纯网络控制器。 */
    virtual void BeginPlay() override;

private:
    /** 客户端目标动态创建的 UI/关卡桥接实现。 */
    UPROPERTY(Transient) TObjectPtr<UObject> ClientBridge;
    /** 连接迁移期间保留的玩家名。 */
    UPROPERTY() FString PendingPlayerName;
    UPROPERTY(Transient) TArray<FMahjongAction> LastAvailableActions;
    /** 最近一次动作序号，用于服务端幂等和乱序检查。 */
    int32 LastClientActionSequence = -1;
    static TAtomic<uint64> ServerRpcReceivedCount;

    /** 单个固定方法的有界进程内累加器；RecentDurations 只保留最近 256 个样本。 */
    struct FServerRpcAccumulator
    {
        uint64 ReceivedCount = 0;
        uint64 RejectedCount = 0;
        uint64 FailedCount = 0;
        uint64 TimeoutCount = 0;
        TArray<double> RecentDurations;
    };
    /** 统计锁只保护很短的内存更新；不得在持锁时调用业务处理器或网络接口。 */
    static FCriticalSection ServerRpcTelemetryMutex;
    static TMap<FString, FServerRpcAccumulator> ServerRpcTelemetryByMethod;
    /**
     * 记录一次同步 RPC 处理结果；StartedAtSeconds 使用 FPlatformTime 单调时钟。
     * bRejected 表示入口校验拒绝，bFailed 表示处理器不可用或业务未进入成功路径。
     */
    static void RecordServerRpcTelemetry(
        const TCHAR* MethodName, double StartedAtSeconds, bool bRejected, bool bFailed);

    /** 取得客户端或服务端目标模块提供的接口实现。 */
    IGuiyangClientControllerBridge* GetClientBridge() const;
    IGuiyangServerRequestHandler* GetServerRequestHandler() const;
};
