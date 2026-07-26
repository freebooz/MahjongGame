#pragma once

#include "CoreMinimal.h"
#include "Interfaces/IHttpRequest.h"
#include "Interfaces/IHttpResponse.h"
#include "Server/GuiyangServerTicketVerifier.h"
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
    /** 游戏服注册与心跳使用的内部凭证。 */
    FString RegistrationCredential;
    /** 当前服务端构建版本，用于拒绝不兼容客户端。 */
    FString BuildVersion;
    /** 返回给客户端的可连接 IP。 */
    FString AdvertisedIp;
    /** 校验一次性入场票据的签名密钥。 */
    FString JoinTicketSigningKey;
    /** 比赛结果发送失败时使用的本地 Outbox 文件。 */
    FString MatchResultOutboxPath;
    /** 游戏监听端口。 */
    int32 Port = 0;

    /** 解析并校验命令行参数；失败时通过 OutError 返回面向运维的原因。 */
    static bool TryParse(const TCHAR* CommandLine, const FString& SigningKey,
        const FString& RegistrationCredential, const FString& MatchResultOutboxPath,
        FGuiyangGameServerLaunchConfig& OutConfig, FString& OutError);
};

struct FMahjongFinalSettlementResult;

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
    /** 将最终结算写入可靠 Outbox，随后异步上报 Lobby。 */
    void QueueFinalSettlement(const FMahjongFinalSettlementResult& Result, int64 ResultSequence);
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
    /** 网络流程状态位，避免关闭过程中继续发送或重复并发。 */
    bool bRegistered = false;
    bool bShuttingDown = false;
    bool bMatchResultRequestInFlight = false;
};
