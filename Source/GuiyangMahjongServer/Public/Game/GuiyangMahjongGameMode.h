#pragma once

#include "CoreMinimal.h"
#include "GameFramework/GameModeBase.h"
#include "Game/GuiyangServerRequestHandler.h"
#include "Auth/GuiyangLoginTypes.h"
#include "Network/MahjongNetworkTypes.h"
#include "Server/GuiyangGameServerBridge.h"
#include "GuiyangMahjongGameMode.generated.h"

struct FGuiyangManagedRoomDefinition;
struct FGuiyangPlayerConnectionTelemetry;

/** Dedicated Server 权威入口；为牌桌复制指定 GameState 和玩家请求入口。 */
UCLASS()
class GUIYANGMAHJONGSERVER_API AGuiyangMahjongGameMode : public AGameModeBase, public IGuiyangServerRequestHandler
{
    GENERATED_BODY()

public:
    /** 配置服务端专用 Controller、PlayerState 和 GameState。 */
    AGuiyangMahjongGameMode();
    /** 解析托管启动参数并管理登录前后、离线和关卡结束生命周期。 */
    virtual void InitGame(const FString& MapName, const FString& Options, FString& ErrorMessage) override;
    virtual void BeginPlay() override;
    virtual void PreLogin(const FString& Options, const FString& Address,
        const FUniqueNetIdRepl& UniqueId, FString& ErrorMessage) override;
    virtual FString InitNewPlayer(APlayerController* NewPlayerController, const FUniqueNetIdRepl& UniqueId,
        const FString& Options, const FString& Portal = TEXT("")) override;
    virtual void PostLogin(APlayerController* NewPlayer) override;
    virtual void Logout(AController* Exiting) override;
    virtual void EndPlay(const EEndPlayReason::Type EndPlayReason) override;

    /** 使用控制面 Bootstrap 创建本进程唯一的权威房间。 */
    bool InitializeManagedRoomAuthority(const FGuiyangManagedRoomDefinition& Definition, FString& OutError);
<<<<<<< HEAD
    /** 向心跳桥接层提供玩家连接状态，不暴露 RoomManager 的可写引用。 */
    bool GetPlayerConnectionTelemetry(
        const FString& PlayerId, FGuiyangPlayerConnectionTelemetry& OutTelemetry) const;
    /** 返回当前托管状态及最近变化时间；无记录时返回 false，调用方应发送 null。 */
    bool GetPlayerTrusteeTelemetry(
        const FString& PlayerId, bool& OutTrustee, FDateTime& OutChangedAtUtc) const;
=======
    /** Return player ids bound to live, ticket-authorized network connections. */
    void GetConnectedAuthorizedPlayerIds(TArray<FString>& OutPlayerIds) const;
>>>>>>> 50429c000bb99dda5845ee9162aabb9e75a2c8fa

    /** 实现共享 Controller 转发的鉴权、大厅和牌桌请求。 */
    virtual void HandleCreateRoom(class AGuiyangMahjongPlayerController* Controller, const FMahjongCreateRoomRequest& Request) override;
    virtual void HandleQuickStart(class AGuiyangMahjongPlayerController* Controller) override;
    void HandleAuthenticateSession(class AGuiyangMahjongPlayerController* Controller, const FString& PlayerId,
        const FString& DisplayName, EGuiyangLoginProvider Provider, const FString& SessionToken) override;
    virtual void HandleJoinRoom(class AGuiyangMahjongPlayerController* Controller, const FMahjongJoinRoomRequest& Request) override;
    virtual void HandleToggleReady(class AGuiyangMahjongPlayerController* Controller) override;
    virtual void HandleNextRound(class AGuiyangMahjongPlayerController* Controller) override;
    virtual void HandleLeaveRoom(class AGuiyangMahjongPlayerController* Controller) override;
    virtual void HandleTableAction(class AGuiyangMahjongPlayerController* Controller, const FMahjongActionRequest& Request) override;
    virtual void HandleLegacyPlayTile(class AGuiyangMahjongPlayerController* Controller, const FMahjongTile& Tile, int32 ClientSequence) override;

private:
    /** 服务端领域对象：房间、牌桌和控制面桥接。 */
    UPROPERTY(Transient) TObjectPtr<class UGuiyangRoomManager> RoomManager;
    UPROPERTY(Transient) TObjectPtr<class UMahjongTableEngine> TableEngine;
    UPROPERTY(Transient) TObjectPtr<class UGuiyangGameServerBridge> GameServerBridge;
    /** 最近已发布的单局/最终结算序号，防止 Tick 或重连重复广播。 */
    int32 LastPublishedSettlementSequence = INDEX_NONE;
    int32 LastFinalizedSettlementSequence = INDEX_NONE;
    int32 LastPublishedFinalRoomSequence = INDEX_NONE;
    /** 服务端洗牌代次和最近种子，确保每局发牌前使用新的牌序。 */
    uint32 ShuffleGeneration = 0;
    int32 LastShuffleSeed = 0;
    /** 当前活动房间码及托管模式固定房间码。 */
    FString ActiveRoomCode;
    FString ManagedRoomCode;
    /** 玩家会话摘要及登录前已由票据授权的短期缓存。 */
    TMap<FString, FString> SessionTokenDigestsByPlayer;
    TMap<FString, FString> PendingAuthorizedPlayersByTicketDigest;
    TMap<FString, FString> PendingAuthorizedDisplayNamesByTicketDigest;
    TMap<FString, int64> PendingTicketExpiryByDigest;
    /** 已完成身份绑定的网络连接到玩家 ID 映射。 */
    TMap<TObjectPtr<APlayerController>, FString> AuthorizedPlayerIdsByController;
<<<<<<< HEAD
    /** 每名玩家的超时自动托管状态；玩家主动完成合法操作后自动解除。 */
    struct FPlayerTrusteeState
    {
        bool bTrustee = false;
        FDateTime ChangedAtUtc;
    };
    TMap<FString, FPlayerTrusteeState> TrusteeStateByPlayer;
=======
    TMap<TObjectPtr<APlayerController>, FString> AuthorizedDisplayNamesByController;
>>>>>>> 50429c000bb99dda5845ee9162aabb9e75a2c8fa
    /** 当前编排模式及托管世界初始化状态。 */
    bool bManagedGameServer = false;
    bool bAgonesGameServer = false;
    bool bManagedWorldReady = false;
    bool bHasPendingManagedConfig = false;
    FGuiyangGameServerLaunchConfig PendingManagedConfig;
    /** 带局/回合/阶段版本的动作超时定时器。 */
    FTimerHandle ActionTimeoutHandle;
    int32 ArmedTimeoutRoundId = INDEX_NONE;
    int32 ArmedTimeoutTurnId = INDEX_NONE;
    EMahjongTablePhase ArmedTimeoutPhase = EMahjongTablePhase::WaitingForPlayers;
    /** 从已授权 Controller 解析权威 PlayerState。 */
    bool ResolvePlayer(class AGuiyangMahjongPlayerController* Controller, class AGuiyangMahjongPlayerState*& OutPlayerState) const;
    /** 发布房间与牌桌公共/私有快照并推进结算。 */
    void PublishRoomState(const FMahjongRoomState& State);
    void TryStartTable(const FMahjongRoomState& StartingRoomState);
    void PublishTableSnapshots();
    void FinalizeRoundIfNeeded();
    void RefreshActionTimeoutTimer();
    void HandleActionTimeout(int32 ExpectedRoundId, int32 ExpectedTurnId, EMahjongTablePhase ExpectedPhase);
    /** 按座位更新托管状态；只有真实变化才刷新时间，避免重复心跳制造事件。 */
    void SetSeatTrusteeState(int32 SeatIndex, bool bTrustee);
    void PublishReconnectSnapshot(class AGuiyangMahjongPlayerController* Controller,
        const FMahjongRoomState& RoomState, int32 RemainingReconnectSeconds);
    void PublishFinalSettlement(const FMahjongRoomState& RoomState);
    /** 对敏感会话和票据做不可逆摘要，内存中不长期保留原文。 */
    static FString HashSessionToken(const FString& SessionToken);
    static FString HashJoinTicket(const FString& JoinTicket);
    static bool ConstantTimeDigestEquals(const FString& Left, const FString& Right);
    static FString ErrorToMessage(EMahjongRoomError Error);
    /** 在监听端口就绪后注册控制面桥接，Agones 分配也汇入同一路径。 */
    void InitializeManagedBridge(const FGuiyangGameServerLaunchConfig& Config);
    void TryInitializeManagedBridgeAfterListen();
    void HandleAgonesAllocationReady(const FGuiyangGameServerLaunchConfig& Config);
};
