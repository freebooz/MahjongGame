#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Interfaces/IHttpRequest.h"
#include "Interfaces/IHttpResponse.h"
#include "Server/GuiyangServerTicketVerifier.h"
#include "Server/GuiyangFairShuffle.h"
#include "TimerManager.h"
#include "UObject/Object.h"
#include "GuiyangGameServerBridge.generated.h"

/** 独立游戏服从命令行读取的启动参数；由 Lobby/Allocator 在分配房间时注入。 */
struct GUIYANGMAHJONGSERVER_API FGuiyangGameServerLaunchConfig
{
    /** 对外展示的六位房间号。 */
    FString RoomId;
    /** 一场完整比赛的全局唯一标识。 */
    FString MatchId;
    /** 当前独立服务器实例标识，用于心跳和故障定位。 */
    FString ServerInstanceId;
    /** Lobby 内网 API 根地址。 */
    FString LobbyInternalUrl;
    /** GameData 内网 API 根地址；最终结算不得再通过玩家 HTTP 网关。 */
    FString GameDataInternalUrl;
    /** 游戏服注册与心跳使用的内部凭证。 */
    FString RegistrationCredential;
    /** 当前服务端构建版本，用于拒绝不兼容客户端。 */
    FString BuildVersion;
    /** 当前分配允许的规则集和网络协议版本；Join Ticket 必须与两者完全一致。 */
    FString RuleSetVersion;
    FString ProtocolVersion;
    /** 允许接入当前服务构建的客户端版本白名单；来源为受控环境变量，不接受 Ticket 自行声明扩展。 */
    TArray<FString> CompatibleClientBuilds;
    /** 返回给客户端的可连接 IP。 */
    FString AdvertisedIp;
    /** 校验一次性入场票据的签名密钥。 */
    FString JoinTicketSigningKey;
    /**
     * 最终结算信封的独立 HMAC 密钥；由受控运行环境注入，不写入命令行、日志或 Outbox。
     * 它与短生命周期 ResultCredential 分离，使 Allocator 能在 DS 崩溃后原样转交已签名信封。
     */
    FString SettlementSigningKey;
    /** 比赛结果发送失败时使用的本地 Outbox 文件。 */
    FString MatchResultOutboxPath;
    /** 权威动作与快照的持久化根目录；Agones 模式必须挂载可跨 Pod 读取的 RWX 卷。 */
    FString RecoveryDirectory;
    /** 仅用于滚动升级/紧急回滚的旧票据兼容开关；生产完成升级后必须保持 false。 */
    bool bAllowLegacyJoinTickets = false;
    /** 游戏监听端口。 */
    int32 Port = 0;
    /** Lobby 房间路由代际；重新分配后递增，用于拒绝旧实例和旧 Join Ticket。 */
    int64 RoomEpoch = 1;
    /** Allocation Service 租约 fencing token；旧实例即使延迟回调也不得覆盖当前分配。 */
    int64 LeaseFencingToken = 1;

    /** 解析并校验命令行参数；失败时通过 OutError 返回面向运维的原因。 */
    static bool TryParse(const TCHAR* CommandLine, const FString& SigningKey,
        const FString& RegistrationCredential, const FString& MatchResultOutboxPath,
        FGuiyangGameServerLaunchConfig& OutConfig, FString& OutError);
};

struct FMahjongFinalSettlementResult;
struct FGuiyangRecoveryEvidenceObject;
class UNetDriver;

/** 连接独立游戏服与 Lobby 控制面的桥接对象，负责注册、心跳、票据和结果上报。 */
UCLASS()
class GUIYANGMAHJONGSERVER_API UGuiyangGameServerBridge final : public UObject
{
    GENERATED_BODY()

public:
    /** 绑定世界并启动注册流程；必须在 GameMode 接受玩家前调用。 */
    bool Initialize(UWorld* InWorld, const FGuiyangGameServerLaunchConfig& InConfig, FString& OutError);
    /** 停止定时器和网络请求，使对象进入只读关闭状态。 */
    void Shutdown();
    /** UObject 销毁兜底，确保 Shutdown 即使未显式调用也会执行。 */
    virtual void BeginDestroy() override;

    /** 是否已经完成控制面注册并获得心跳凭证。 */
    bool IsRegistered() const { return bRegistered; }
    /** 校验玩家、房间和有效期，并以一次性语义消费入场票据。 */
    bool ValidateAndConsumeJoinTicket(const FString& Ticket, const FString& PlayerId,
        FGuiyangJoinTicketClaims& OutClaims, FString& OutError);
    /**
     * 将强最终结果信封写入可靠 Outbox，随后异步上报 GameData。
     * 三个摘要和证据清单必须已通过结算前屏障，任何缺失都会拒绝入队。
     */
    void QueueFinalSettlement(
        const FMahjongFinalSettlementResult& Result,
        int32 SettlementVersion,
        const TArray<FGuiyangShuffleAuditProof>& ShuffleProofs,
        const FString& EventChainDigest,
        const FString& FinalStateHash,
        const FString& ActionLogHash,
        const FString& RandomCommitment,
        const FString& EvidenceId,
        const TArray<FGuiyangRecoveryEvidenceObject>& EvidenceObjects);
    /**
     * 追加本地公平性审计事件。
     *
     * Commitment 阶段严禁写入种子、nonce 或牌序摘要；Reveal 阶段只能在单局结算后调用。
     * 返回 false 表示可靠落盘失败，开局调用方必须停止发牌。
     */
    bool AppendShuffleAuditRecord(
        const FGuiyangShuffleAuditProof& Proof,
        bool bReveal,
        const FString& EventChainDigest) const;
    /** 返回初始化后锁定的启动配置。 */
    const FGuiyangGameServerLaunchConfig& GetConfig() const { return Config; }

private:
    /** 发送首次注册请求，成功后开始心跳。 */
    void SendRegistration();
    /** 处理注册结果并保存服务端下发的临时凭证。 */
    void HandleRegistrationResponse(FHttpRequestPtr Request, FHttpResponsePtr Response, bool bSucceeded);
    /** 周期性报告房间生命周期、人数和当前局号。 */
    void SendHeartbeat();
    void HandleHeartbeatResponse(FHttpRequestPtr Request, FHttpResponsePtr Response, bool bSucceeded);
    /** 尝试发送尚未确认的最终结算。 */
    void SendPendingMatchResult();
    void HandleMatchResultResponse(FHttpRequestPtr Request, FHttpResponsePtr Response, bool bSucceeded);
    /** 使用有上限的退避策略安排下一次结果重试。 */
    void ScheduleMatchResultRetry();
    /** 在发送前先落盘，保证进程崩溃后仍可补报。 */
    bool PersistPendingMatchResult(const TSharedRef<FJsonObject>& Report) const;
    void DeletePersistedMatchResult() const;
    /** 把权威牌桌状态转换为控制面可识别的生命周期字符串。 */
    FString BuildHeartbeatLifecycle(int32& OutRoundId) const;
    /**
     * 从当前 GameNetDriver 累加应用层收发字节。
     * 同一驱动内使用 uint32 无符号差值处理回绕；驱动重建时重新设基线，避免产生异常尖峰。
     */
    void UpdateNetworkTelemetry();

    /** 仅弱引用世界，避免桥接对象反向延长 World 生命周期。 */
    TWeakObjectPtr<UWorld> World;
    /** 初始化后不再变化的服务实例配置。 */
    FGuiyangGameServerLaunchConfig Config;
    /** 维护票据签名校验和一次性消费缓存。 */
    TUniquePtr<FGuiyangJoinTicketValidator> TicketValidator;
    /** 控制面为心跳与结果上报分别签发的短期凭证。 */
    FString HeartbeatCredential;
    FString ResultCredential;
    /** 当前等待控制面确认的结果负载及幂等键。 */
    FString PendingMatchResultBody;
    FString PendingMatchId;
    /** 心跳与结果重试定时器。 */
    FTimerHandle HeartbeatTimer;
    FTimerHandle MatchResultRetryTimer;
    /** 控制面建议的心跳间隔。 */
    int32 HeartbeatIntervalSeconds = 3;
    /** 单调递增的结果序号，防止旧结果覆盖新结果。 */
    int64 PendingResultSequence = 0;
    /** 当前结果上报重试次数。 */
    int32 MatchResultAttempt = 0;
    /** 首次进入 Playing 的 UTC 时间，仅用于监控，不参与权威规则计算。 */
    FDateTime GameStartedAtUtc;
    /** 最近网络驱动原始计数和进程生命周期累计量，单位字节。 */
    TWeakObjectPtr<UNetDriver> SampledNetDriver;
    uint32 PreviousNetworkIngressBytes = 0;
    uint32 PreviousNetworkEgressBytes = 0;
    uint64 NetworkIngressBytes = 0;
    uint64 NetworkEgressBytes = 0;
    bool bNetworkBaselineInitialized = false;
    /** 当前只读结算投影；正文哈希用于争议核对，不存储或修改玩家结果。 */
    FString SettlementStatus;
    FString SettlementResultHash;
    FString SettlementFailureReason;
    /** 最近结算的单调序号；确认后继续保留供最后一次心跳和本地诊断。 */
    int64 SettlementResultSequence = 0;
    FDateTime SettlementSubmittedAtUtc;
    FDateTime SettlementConfirmedAtUtc;
    /** 网络流程状态位，避免关闭过程中继续发送或重复并发。 */
    bool bRegistered = false;
    bool bShuttingDown = false;
    bool bMatchResultRequestInFlight = false;
};
