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
class FGuiyangRuntimeRecoveryStore;

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
    /** 心跳只读取恢复状态摘要；不返回完整快照、私有手牌或证据正文。 */
    bool IsRecoveredAuthority() const { return bRecoveredGameServer; }
    bool IsSettlementEvidenceReady() const { return bSettlementEvidenceReady; }
    FString GetAuthoritativeStateHash() const;

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
    virtual void HandleReconnectStateConfirmed(
        class AGuiyangMahjongPlayerController* Controller,
        const FString& ControlToken,
        int32 StateVersion,
        const FString& PublicStateHash) override;

private:
    /** 服务端领域对象：房间、牌桌和控制面桥接。 */
    UPROPERTY(Transient) TObjectPtr<class UGuiyangRoomManager> RoomManager;
    UPROPERTY(Transient) TObjectPtr<class UMahjongTableEngine> TableEngine;
    UPROPERTY(Transient) TObjectPtr<class UGuiyangGameServerBridge> GameServerBridge;
    /** 最近已发布的单局/最终结算序号，防止 Tick 或重连重复广播。 */
    int32 LastPublishedSettlementSequence = INDEX_NONE;
    int32 LastFinalizedSettlementSequence = INDEX_NONE;
    int32 LastPublishedFinalRoomSequence = INDEX_NONE;
    /**
     * 当前局和已结束局的公平性证明。
     *
     * PendingShuffleProof 在局内含敏感披露材料，只能存于服务端内存；CompletedShuffleProofs
     * 仅在对应单局结算落盘后追加，最终随权威结算进入内部审计存储。
     */
    TOptional<FGuiyangShuffleAuditProof> PendingShuffleProof;
    TArray<FGuiyangShuffleAuditProof> CompletedShuffleProofs;
    /** 每局披露记录形成的链式摘要，用于发现删除、插入和重排。 */
    FString FairnessEventChainDigest;
    /** 当前活动房间码及托管模式固定房间码。 */
    FString ActiveRoomCode;
    FString ManagedRoomCode;
    /** 玩家会话摘要及登录前已由票据授权的短期缓存。 */
    TMap<FString, FString> SessionTokenDigestsByPlayer;
    TMap<FString, FString> PendingAuthorizedPlayersByTicketDigest;
    TMap<FString, FString> PendingAuthorizedDisplayNamesByTicketDigest;
    TMap<FString, int64> PendingTicketExpiryByDigest;
    /** 强票据声明在 PreLogin 与 InitNewPlayer 之间只按票据摘要暂存，原票据不会驻留。 */
    TMap<FString, FGuiyangJoinTicketClaims> PendingTicketClaimsByDigest;
    /** 已完成身份绑定的网络连接到玩家 ID 映射。 */
    TMap<TObjectPtr<APlayerController>, FString> AuthorizedPlayerIdsByController;
    TMap<TObjectPtr<APlayerController>, FString> AuthorizedDisplayNamesByController;
    /** 控制器绑定的完整已验证声明，用于座位、会话 Epoch 和多端控制令牌校验。 */
    TMap<TObjectPtr<APlayerController>, FGuiyangJoinTicketClaims> AuthorizedClaimsByController;
    /** 每次重连生成一次控制确认令牌；只保存其摘要，确认后立即删除。 */
    TMap<TObjectPtr<APlayerController>, FString> PendingReconnectTokenDigests;
    TSet<TObjectPtr<APlayerController>> ReconnectConfirmedControllers;
    /** 每名玩家的超时自动托管状态；玩家主动完成合法操作后自动解除。 */
    struct FPlayerTrusteeState
    {
        bool bTrustee = false;
        FDateTime ChangedAtUtc;
    };
    TMap<FString, FPlayerTrusteeState> TrusteeStateByPlayer;
    /** 已接受动作 ID 和每玩家短窗接收时间；仅保存有界摘要，不保存客户端敏感负载。 */
    TMap<FString, int64> AcceptedActionIds;
    TMap<FString, TArray<double>> RecentActionTimesByPlayer;
    /** 当前比赛的私有证据/快照仓库；只在托管 Dedicated Server 初始化。 */
    TUniquePtr<FGuiyangRuntimeRecoveryStore> RuntimeRecoveryStore;
    FDateTime LastAuthoritativeSnapshotAtUtc;
    int64 LastSnapshotActionSequence = 0;
    bool bRecoveredGameServer = false;
    bool bSettlementEvidenceReady = true;
    /** 只消费一次的恢复计时器剩余值；重新武装后立即清空。 */
    TOptional<float> RecoveredActionTimeoutRemainingSeconds;
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
    /** 在进入规则引擎前校验动作信封、Epoch、时钟窗口、重复 ID 和请求频率。 */
    bool ValidateAuthoritativeActionEnvelope(
        const class AGuiyangMahjongPlayerState& Player,
        const FMahjongActionRequest& Request,
        FString& OutError);
    /** 记录动作链并按动作数量/时间/关键动作策略生成完整快照。 */
    void RecordAcceptedActionEvidence(
        const class AGuiyangMahjongPlayerState& Player,
        const FMahjongActionRequest& Request,
        const FMahjongActionResult& Result,
        int32 StateVersionBefore);
    bool PersistAuthoritativeSnapshot(bool bSettlementBarrier, FString& OutError);
    /** 从前一 Epoch 最新快照和后续动作重放；哈希或序号异常时拒绝成为 Ready。 */
    bool TryRecoverPriorEpoch(FString& OutError);
};
