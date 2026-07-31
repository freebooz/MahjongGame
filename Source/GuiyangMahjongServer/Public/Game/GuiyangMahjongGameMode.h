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
    /** 登录完成后绑定票据授权身份并发布在线状态；未授权连接必须立即拒绝进入房间。 */
    virtual void PostLogin(APlayerController* NewPlayer) override;
    /** 连接退出时释放权威映射并启动房间断线宽限流程，不立即删除可重连座位。 */
    virtual void Logout(AController* Exiting) override;
    /** 世界结束时停止动作/下一局计时器并关闭控制面桥接，防止进程退出期间继续上报。 */
    virtual void EndPlay(const EEndPlayReason::Type EndPlayReason) override;

    /** 使用控制面 Bootstrap 创建本进程唯一的权威房间。 */
    bool InitializeManagedRoomAuthority(const FGuiyangManagedRoomDefinition& Definition, FString& OutError);
    /** 向心跳桥接层提供玩家连接状态，不暴露 RoomManager 的可写引用。 */
    bool GetPlayerConnectionTelemetry(
        const FString& PlayerId, FGuiyangPlayerConnectionTelemetry& OutTelemetry) const;
    /** 返回当前托管状态及最近变化时间；无记录时返回 false，调用方应发送 null。 */
    bool GetPlayerTrusteeTelemetry(
        const FString& PlayerId, bool& OutTrustee, FDateTime& OutChangedAtUtc) const;
    /** 返回绑定到存活且票据授权连接的玩家 ID，不包含等待重连的离线座位。 */
    void GetConnectedAuthorizedPlayerIds(TArray<FString>& OutPlayerIds) const;

    /** 处理默认或完整规则开房请求；托管模式下不允许客户端创建第二个权威房间。 */
    virtual void HandleCreateRoom(class AGuiyangMahjongPlayerController* Controller, const FMahjongCreateRoomRequest& Request) override;
    /** 为已认证玩家选择或创建快速房间，失败结果通过定向客户端错误返回。 */
    virtual void HandleQuickStart(class AGuiyangMahjongPlayerController* Controller) override;
    /** 验证短期会话并绑定 PlayerState；不得信任 RPC 中可伪造的玩家标识。 */
    void HandleAuthenticateSession(class AGuiyangMahjongPlayerController* Controller, const FString& PlayerId,
        const FString& DisplayName, EGuiyangLoginProvider Provider, const FString& SessionToken) override;
    /** 校验房间码和密码后占用座位，密码正文不会进入复制状态或日志。 */
    virtual void HandleJoinRoom(class AGuiyangMahjongPlayerController* Controller, const FMahjongJoinRoomRequest& Request) override;
    /** 切换准备状态并在满足规则时触发开局；非成员和错误生命周期请求会被拒绝。 */
    virtual void HandleToggleReady(class AGuiyangMahjongPlayerController* Controller) override;
    /** 结算阶段确认下一局，依靠房间状态序列保证重复 RPC 幂等。 */
    virtual void HandleNextRound(class AGuiyangMahjongPlayerController* Controller) override;
    /** 释放当前座位并发布新房间状态；断线重连与主动离开采用不同清理语义。 */
    virtual void HandleLeaveRoom(class AGuiyangMahjongPlayerController* Controller) override;
    /** 校验当前座位后切换玩家托管，并把最终状态定向回传给客户端。 */
    virtual void HandleSetTrustee(
        class AGuiyangMahjongPlayerController* Controller, bool bEnabled) override;
    /** 验证状态版本、座位权限和客户端序号后交给权威牌桌执行统一动作。 */
    virtual void HandleTableAction(class AGuiyangMahjongPlayerController* Controller, const FMahjongActionRequest& Request) override;
    /** 将旧版出牌 RPC 转换为统一动作请求，保留兼容性但不绕过任何权威校验。 */
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
    TMap<TObjectPtr<APlayerController>, FString> AuthorizedDisplayNamesByController;
    /** 每名玩家的超时自动托管状态；玩家主动完成合法操作后自动解除。 */
    struct FPlayerTrusteeState
    {
        bool bTrustee = false;
        FDateTime ChangedAtUtc;
    };
    TMap<FString, FPlayerTrusteeState> TrusteeStateByPlayer;
    /** 当前编排模式及托管世界初始化状态。 */
    bool bManagedGameServer = false;
    bool bAgonesGameServer = false;
    bool bManagedWorldReady = false;
    bool bHasPendingManagedConfig = false;
    FGuiyangGameServerLaunchConfig PendingManagedConfig;
    /** 带局/回合/阶段版本的动作超时定时器。 */
    FTimerHandle ActionTimeoutHandle;
    /** 所有客户端共享的中间局自动推进兜底计时器；任一有效确认后必须取消。 */
    FTimerHandle NextRoundAutoStartHandle;
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
    void ArmNextRoundAutoStart(const FMahjongRoomState& WaitingRoomState);
    void HandleNextRoundAutoStart(FString ExpectedRoomCode);
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
