#pragma once

#include "CoreMinimal.h"
#include "Subsystems/GameInstanceSubsystem.h"
#include "AgonesSubsystem.h"
#include "Server/GuiyangGameServerBridge.h"
#include "GuiyangAgonesLifecycleSubsystem.generated.h"

DECLARE_MULTICAST_DELEGATE_OneParam(
    FGuiyangAgonesAllocationReady, const FGuiyangGameServerLaunchConfig&);

/**
 * 可选的 Agones 生命周期适配器。
 * 只有独立服务器明确选择 Agones 调度器时才激活，因此本机/WSL Allocator 启动方式不受影响。
 */
UCLASS()
class GUIYANGMAHJONGSERVER_API UGuiyangAgonesLifecycleSubsystem final : public UGameInstanceSubsystem
{
    GENERATED_BODY()

public:
    /** 仅在独立服务器且命令行或环境变量请求 Agones 时创建。 */
    virtual bool ShouldCreateSubsystem(UObject* Outer) const override;
    /** 连接 Agones Sidecar，并注册 GameServer 更新及错误回调。 */
    virtual void Initialize(FSubsystemCollectionBase& Collection) override;
    /** 解除回调并释放 Sidecar 引用。 */
    virtual void Deinitialize() override;

    /** 解析命令行和环境变量中的调度器选择。 */
    static bool IsAgonesRequested(const TCHAR* CommandLine, const FString& EnvironmentValue);
    /** 将 Agones 分配结果与本地密钥组装成统一游戏服启动配置。 */
    static bool TryBuildLaunchConfig(const FGameServerResponse& Response,
        const FString& SigningKey, const FString& MatchResultOutboxPath,
        FGuiyangGameServerLaunchConfig& OutConfig, FString& OutError);

    /** 当前是否已经启用 Agones 适配器。 */
    bool IsActive() const { return bActive; }
    /**
     * 在专用地图和监听 NetDriver 完成初始化后启动 Health、Watch 和 Sidecar 连接。
     * 插件 Connect 内置 5 秒重试，因此 Sidecar 晚于游戏进程就绪时不会立即退出。
     */
    void StartAfterWorldReady();
    /** GameServer 是否已经完成分配并向 Agones 标记 Ready。 */
    bool IsReady() const { return bReady; }
    /** 向 Agones PlayerTracking 报告玩家加入/离开。 */
    void NotifyPlayerConnected(const FString& PlayerId);
    void NotifyPlayerDisconnected(const FString& PlayerId);
    /** 请求 Agones 安全关闭当前 GameServer。 */
    void RequestShutdown();
    /** 取得完成分配后的统一配置。 */
    bool TryGetAllocationConfig(FGuiyangGameServerLaunchConfig& OutConfig) const;
    /** 分配完成事件，供 GameMode 延迟启动权威房间。 */
    FGuiyangAgonesAllocationReady& OnAllocationReady() { return AllocationReady; }

private:
    /** Sidecar 连接、GameServer 更新、错误及玩家跟踪回调。 */
    UFUNCTION() void HandleConnected(const FGameServerResponse& Response);
    UFUNCTION() void HandleGameServerUpdated(const FGameServerResponse& Response);
    UFUNCTION() void HandleError(const FAgonesError& Error);
    UFUNCTION() void HandleEmptySuccess(const FEmptyResponse& Response);
    UFUNCTION() void HandlePlayerConnected(const FConnectedResponse& Response);
    UFUNCTION() void HandlePlayerDisconnected(const FDisconnectResponse& Response);

    /** 当前 GameInstance 提供的 Agones 客户端。 */
    UPROPERTY(Transient) TObjectPtr<UAgonesSubsystem> Agones;
    /** 分配事件与最近一次有效配置。 */
    FGuiyangAgonesAllocationReady AllocationReady;
    TOptional<FGuiyangGameServerLaunchConfig> AllocationConfig;
    /** 生命周期状态位，防止重复 Ready 或重复 Shutdown。 */
    bool bActive = false;
    /** 防止地图重载或重复 BeginPlay 对 Sidecar 建立多组健康定时器和 Watch。 */
    bool bConnectionStarted = false;
    bool bReady = false;
    bool bShutdownRequested = false;
};
