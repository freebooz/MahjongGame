#include "Server/GuiyangAgonesLifecycleSubsystem.h"

#include "GuiyangMahjong.h"
#include "HAL/PlatformMisc.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"

namespace
{
    /** 从 Agones Annotation 读取并规范化必填分配字段。 */
    bool ReadAnnotation(const FGameServerResponse& Response, const TCHAR* Key, FString& OutValue)
    {
        const FString* Value = Response.ObjectMeta.Annotations.Find(Key);
        if (!Value) return false;
        OutValue = Value->TrimStartAndEnd();
        return !OutValue.IsEmpty();
    }
}

bool UGuiyangAgonesLifecycleSubsystem::ShouldCreateSubsystem(UObject* Outer) const
{
    // 客户端和 Listen Server 完全不创建该子系统，避免引入无意义的 Sidecar 通信。
    return IsRunningDedicatedServer() && Super::ShouldCreateSubsystem(Outer);
}

bool UGuiyangAgonesLifecycleSubsystem::IsAgonesRequested(
    const TCHAR* CommandLine, const FString& EnvironmentValue)
{
    FString Orchestrator;
    if (CommandLine)
    {
        FParse::Value(CommandLine, TEXT("MahjongOrchestrator="), Orchestrator);
    }
    // 命令行优先于环境变量，便于单个进程覆盖节点级默认配置。
    if (Orchestrator.IsEmpty())
    {
        Orchestrator = EnvironmentValue;
    }
    Orchestrator.TrimStartAndEndInline();
    return Orchestrator.Equals(TEXT("Agones"), ESearchCase::IgnoreCase);
}

bool UGuiyangAgonesLifecycleSubsystem::TryBuildLaunchConfig(
    const FGameServerResponse& Response,
    const FString& SigningKey,
    const FString& MatchResultOutboxPath,
    FGuiyangGameServerLaunchConfig& OutConfig,
    FString& OutError)
{
    OutConfig = {};
    OutError.Reset();
    // 只有 Allocated 状态包含可供玩家使用的稳定房间分配信息。
    if (!Response.Status.State.Equals(TEXT("Allocated"), ESearchCase::IgnoreCase))
    {
        OutError = TEXT("AGONES_GAMESERVER_NOT_ALLOCATED");
        return false;
    }
    if (!ReadAnnotation(Response, TEXT("mahjong.freebooz/room-id"), OutConfig.RoomId)
        || !ReadAnnotation(Response, TEXT("mahjong.freebooz/match-id"), OutConfig.MatchId)
        || !ReadAnnotation(Response, TEXT("mahjong.freebooz/server-instance-id"), OutConfig.ServerInstanceId)
        || !ReadAnnotation(Response, TEXT("mahjong.freebooz/registration-credential"), OutConfig.RegistrationCredential)
        || !ReadAnnotation(Response, TEXT("mahjong.freebooz/lobby-internal-url"), OutConfig.LobbyInternalUrl)
        || !ReadAnnotation(Response, TEXT("mahjong.freebooz/build-version"), OutConfig.BuildVersion))
    {
        OutError = TEXT("AGONES_ALLOCATION_METADATA_INCOMPLETE");
        return false;
    }
    OutConfig.AdvertisedIp = Response.Status.Address.TrimStartAndEnd();
    // 优先使用名为 game 的端口，兼容旧 Fleet 时回退到第一个端口。
    const FPort* GamePort = Response.Status.Ports.FindByPredicate(
        [](const FPort& Port) { return Port.Name.Equals(TEXT("game"), ESearchCase::IgnoreCase); });
    if (!GamePort && !Response.Status.Ports.IsEmpty()) GamePort = &Response.Status.Ports[0];
    OutConfig.Port = GamePort ? GamePort->Port : 0;
    OutConfig.JoinTicketSigningKey = SigningKey;
    OutConfig.MatchResultOutboxPath = MatchResultOutboxPath;
    if (OutConfig.AdvertisedIp.IsEmpty() || OutConfig.Port <= 0 || OutConfig.Port > 65535
        || OutConfig.JoinTicketSigningKey.Len() < 32
        || OutConfig.RegistrationCredential.Len() < 16
        || OutConfig.MatchResultOutboxPath.IsEmpty())
    {
        OutError = TEXT("AGONES_ALLOCATION_CONFIGURATION_INVALID");
        return false;
    }
    return true;
}

void UGuiyangAgonesLifecycleSubsystem::Initialize(FSubsystemCollectionBase& Collection)
{
    Super::Initialize(Collection);
    const FString EnvironmentValue =
        FPlatformMisc::GetEnvironmentVariable(TEXT("MAHJONG_ORCHESTRATOR"));
    if (!IsAgonesRequested(FCommandLine::Get(), EnvironmentValue))
    {
        return;
    }

    // 显式初始化 SDK 依赖后再注册回调，避免连接事件早于本对象准备完成。
    Collection.InitializeDependency<UAgonesSubsystem>();
    Agones = GetGameInstance() ? GetGameInstance()->GetSubsystem<UAgonesSubsystem>() : nullptr;
    if (!Agones)
    {
        UE_LOG(LogMahjongServer, Error, TEXT("Agones orchestrator selected but SDK subsystem is unavailable"));
        return;
    }

    bActive = true;
    Agones->ConnectedDelegate.AddUniqueDynamic(this, &ThisClass::HandleConnected);
    FGameServerDelegate WatchDelegate;
    WatchDelegate.BindDynamic(this, &ThisClass::HandleGameServerUpdated);
    Agones->WatchGameServer(WatchDelegate);
    // SDK 子系统存在于所有独立服务器进程中，但只在明确选择 Agones 后启动健康上报；
    // 否则本地 Allocator 服务器会不断访问本来就不存在的 Sidecar。
    Agones->HealthPing(Agones->HealthRateSeconds);
    Agones->Connect();
    UE_LOG(LogMahjongServer, Display, TEXT("Agones lifecycle connection started"));
}

void UGuiyangAgonesLifecycleSubsystem::Deinitialize()
{
    if (Agones)
    {
        Agones->ConnectedDelegate.RemoveDynamic(this, &ThisClass::HandleConnected);
    }
    // Deinitialize 可被多次触发，RequestShutdown 内部状态位保证只发送一次。
    RequestShutdown();
    Agones = nullptr;
    bActive = false;
    bReady = false;
    AllocationConfig.Reset();
    AllocationReady.Clear();
    Super::Deinitialize();
}

void UGuiyangAgonesLifecycleSubsystem::HandleConnected(const FGameServerResponse& Response)
{
    bReady = true;
    UE_LOG(LogMahjongServer, Display, TEXT("Agones GameServer ready Name=%s State=%s Address=%s"),
        *Response.ObjectMeta.Name, *Response.Status.State, *Response.Status.Address);
    // 首次连接响应也可能已经是 Allocated，复用统一更新处理。
    HandleGameServerUpdated(Response);

    FSetPlayerCapacityDelegate Success;
    Success.BindDynamic(this, &ThisClass::HandleEmptySuccess);
    FAgonesErrorDelegate Error;
    Error.BindDynamic(this, &ThisClass::HandleError);
    Agones->SetPlayerCapacity(4, Success, Error);
}

void UGuiyangAgonesLifecycleSubsystem::HandleGameServerUpdated(const FGameServerResponse& Response)
{
    // 分配配置一旦接受即不可变，忽略后续 Watch 重复事件。
    if (AllocationConfig.IsSet()
        || !Response.Status.State.Equals(TEXT("Allocated"), ESearchCase::IgnoreCase)) return;
    FGuiyangGameServerLaunchConfig Config;
    FString Error;
    if (!TryBuildLaunchConfig(
        Response,
        FPlatformMisc::GetEnvironmentVariable(TEXT("MAHJONG_JOIN_TICKET_SIGNING_KEY")),
        FPlatformMisc::GetEnvironmentVariable(TEXT("MAHJONG_MATCH_RESULT_OUTBOX_PATH")),
        Config,
        Error))
    {
        UE_LOG(LogMahjongServer, Error, TEXT("Agones allocation rejected: %s"), *Error);
        return;
    }
    AllocationConfig = Config;
    UE_LOG(LogMahjongServer, Display,
        TEXT("Agones allocation accepted InstanceId=%s RoomId=%s Address=%s:%d"),
        *Config.ServerInstanceId, *Config.RoomId, *Config.AdvertisedIp, Config.Port);
    // 只有完整校验后才通知 GameMode 创建权威房间。
    AllocationReady.Broadcast(AllocationConfig.GetValue());
}

bool UGuiyangAgonesLifecycleSubsystem::TryGetAllocationConfig(
    FGuiyangGameServerLaunchConfig& OutConfig) const
{
    if (!AllocationConfig.IsSet()) return false;
    OutConfig = AllocationConfig.GetValue();
    return true;
}

void UGuiyangAgonesLifecycleSubsystem::NotifyPlayerConnected(const FString& PlayerId)
{
    if (!bActive || !bReady || !Agones || PlayerId.IsEmpty()) return;
    FPlayerConnectDelegate Success;
    Success.BindDynamic(this, &ThisClass::HandlePlayerConnected);
    FAgonesErrorDelegate Error;
    Error.BindDynamic(this, &ThisClass::HandleError);
    Agones->PlayerConnect(PlayerId, Success, Error);
}

void UGuiyangAgonesLifecycleSubsystem::NotifyPlayerDisconnected(const FString& PlayerId)
{
    if (!bActive || !Agones || PlayerId.IsEmpty()) return;
    FPlayerDisconnectDelegate Success;
    Success.BindDynamic(this, &ThisClass::HandlePlayerDisconnected);
    FAgonesErrorDelegate Error;
    Error.BindDynamic(this, &ThisClass::HandleError);
    Agones->PlayerDisconnect(PlayerId, Success, Error);
}

void UGuiyangAgonesLifecycleSubsystem::RequestShutdown()
{
    // 关闭请求必须幂等，避免 Sidecar 收到重复 Shutdown。
    if (!bActive || bShutdownRequested || !Agones) return;
    bShutdownRequested = true;
    FShutdownDelegate Success;
    Success.BindDynamic(this, &ThisClass::HandleEmptySuccess);
    FAgonesErrorDelegate Error;
    Error.BindDynamic(this, &ThisClass::HandleError);
    Agones->Shutdown(Success, Error);
}

void UGuiyangAgonesLifecycleSubsystem::HandleError(const FAgonesError& Error)
{
    UE_LOG(LogMahjongServer, Error, TEXT("Agones lifecycle request failed: %s"), *Error.ErrorMessage);
}

void UGuiyangAgonesLifecycleSubsystem::HandleEmptySuccess(const FEmptyResponse& Response)
{
}

void UGuiyangAgonesLifecycleSubsystem::HandlePlayerConnected(const FConnectedResponse& Response)
{
    if (!Response.bConnected)
    {
        UE_LOG(LogMahjongServer, Warning, TEXT("Agones did not confirm player connection"));
    }
}

void UGuiyangAgonesLifecycleSubsystem::HandlePlayerDisconnected(const FDisconnectResponse& Response)
{
    if (!Response.bDisconnected)
    {
        UE_LOG(LogMahjongServer, Warning, TEXT("Agones did not confirm player disconnection"));
    }
}
