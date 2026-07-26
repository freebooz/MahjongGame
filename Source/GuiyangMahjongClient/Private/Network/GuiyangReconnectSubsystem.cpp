#include "Network/GuiyangReconnectSubsystem.h"

#include "Engine/Engine.h"
#include "Engine/World.h"
#include "Game/GuiyangMahjongGameState.h"
#include "HAL/PlatformTime.h"
#include "GuiyangMahjong.h"

void UGuiyangReconnectSubsystem::Initialize(FSubsystemCollectionBase& Collection)
{
    Super::Initialize(Collection);
    if (GEngine)
    {
        NetworkFailureHandle = GEngine->OnNetworkFailure().AddUObject(
            this, &ThisClass::HandleNetworkFailure);
        TravelFailureHandle = GEngine->OnTravelFailure().AddUObject(
            this, &ThisClass::HandleTravelFailure);
    }
}

void UGuiyangReconnectSubsystem::Deinitialize()
{
    if (GEngine)
    {
        GEngine->OnNetworkFailure().Remove(NetworkFailureHandle);
        GEngine->OnTravelFailure().Remove(TravelFailureHandle);
    }
    Super::Deinitialize();
}

void UGuiyangReconnectSubsystem::RememberConnection(const FString& ServerIP, const int32 ServerPort,
    const FString& PlayerName)
{
    // 只在成功发起连接时保存最近端点，供短线重试使用。
    LastServerIP = ServerIP.TrimStartAndEnd();
    LastServerPort = FMath::Clamp(ServerPort, 1, 65535);
    LastPlayerName = PlayerName.TrimStartAndEnd();
}

void UGuiyangReconnectSubsystem::RememberRemoteRoute(const FString& RoomId, const FString& MatchId)
{
    FGuid Parsed;
    LastRemoteRoomId = FGuid::Parse(RoomId, Parsed) ? RoomId : FString();
    LastRemoteMatchId = FGuid::Parse(MatchId, Parsed) ? MatchId : FString();
}

void UGuiyangReconnectSubsystem::BeginReconnectWindow(const FString& Status, const int32 TimeoutSeconds)
{
    // 使用单调时间记录截止点，避免系统时钟调整影响剩余秒数。
    ReconnectStatus = Status.IsEmpty() ? TEXT("网络连接已断开") : Status;
    if (!bReconnectPending)
    {
        ReconnectDeadlineSeconds = FPlatformTime::Seconds() + ClampReconnectTimeoutSeconds(TimeoutSeconds);
    }
    bReconnectPending = true;
    bRetrying = false;
    UE_LOG(LogMahjongReconnect, Warning, TEXT("进入重连窗口：%s，时限=%d秒"),
        *ReconnectStatus, GetRemainingSeconds());
    BroadcastState();
}

void UGuiyangReconnectSubsystem::MarkRetrying()
{
    if (!bReconnectPending || GetRemainingSeconds() <= 0) return;
    bRetrying = true;
    ReconnectStatus = TEXT("正在重新连接并恢复牌桌状态……");
    BroadcastState();
}

void UGuiyangReconnectSubsystem::MarkRestored()
{
    // 恢复成功后清空重试状态，但保留最近连接供下一次网络故障使用。
    bReconnectPending = false;
    bRetrying = false;
    ReconnectDeadlineSeconds = 0.0;
    ReconnectStatus.Reset();
    BroadcastState();
}

void UGuiyangReconnectSubsystem::MarkRetryFailed(const FString& Status)
{
    if (!bReconnectPending || GetRemainingSeconds() <= 0) return;
    bRetrying = false;
    ReconnectStatus = Status.IsEmpty() ? TEXT("重连失败，请重试") : Status;
    BroadcastState();
}

void UGuiyangReconnectSubsystem::CancelReconnect()
{
    MarkRestored();
    LastRemoteRoomId.Reset();
    LastRemoteMatchId.Reset();
    UE_LOG(LogMahjongReconnect, Log, TEXT("玩家取消重连并返回连接界面"));
}

int32 UGuiyangReconnectSubsystem::GetRemainingSeconds() const
{
    if (!bReconnectPending || ReconnectDeadlineSeconds <= 0.0) return 0;
    return FMath::Max(0, FMath::CeilToInt(ReconnectDeadlineSeconds - FPlatformTime::Seconds()));
}

bool UGuiyangReconnectSubsystem::CanRetry() const
{
    return bReconnectPending && !bRetrying && GetRemainingSeconds() > 0
        && ((!LastServerIP.IsEmpty() && LastServerPort >= 1 && LastServerPort <= 65535
                && !LastPlayerName.IsEmpty())
            || (!LastRemoteRoomId.IsEmpty() && !LastRemoteMatchId.IsEmpty()));
}

bool UGuiyangReconnectSubsystem::GetLastConnection(FString& OutServerIP, int32& OutServerPort,
    FString& OutPlayerName) const
{
    OutServerIP = LastServerIP;
    OutServerPort = LastServerPort;
    OutPlayerName = LastPlayerName;
    return !OutServerIP.IsEmpty() && OutServerPort >= 1 && OutServerPort <= 65535 && !OutPlayerName.IsEmpty();
}

bool UGuiyangReconnectSubsystem::GetLastRemoteRoute(FString& OutRoomId, FString& OutMatchId) const
{
    OutRoomId = LastRemoteRoomId;
    OutMatchId = LastRemoteMatchId;
    return !OutRoomId.IsEmpty() && !OutMatchId.IsEmpty();
}

int32 UGuiyangReconnectSubsystem::ClampReconnectTimeoutSeconds(const int32 TimeoutSeconds)
{
    return FMath::Clamp(TimeoutSeconds, 15, 600);
}

void UGuiyangReconnectSubsystem::HandleNetworkFailure(UWorld* World, UNetDriver* NetDriver,
    const ENetworkFailure::Type FailureType, const FString& ErrorString)
{
    // 只处理游戏网络驱动故障；主动返回大厅或编辑器预览不弹出重连遮罩。
    if (World && World->GetGameInstance() != GetGameInstance()) return;
    if (ErrorString.StartsWith(TEXT("JOIN_TICKET_"), ESearchCase::IgnoreCase))
    {
        UE_LOG(LogMahjongReconnect, Warning,
            TEXT("入场票据被服务器拒绝，返回大厅重新获取路由，不进入断线重连：%s"), *ErrorString);
        CancelReconnect();
        return;
    }
    int32 TimeoutSeconds = 120;
    if (const AGuiyangMahjongGameState* GameState = World
        ? World->GetGameState<AGuiyangMahjongGameState>() : nullptr)
    {
        TimeoutSeconds = GameState->RoomState.RuleSnapshot.Config.ReconnectTimeoutSeconds;
    }
    UE_LOG(LogMahjongReconnect, Warning, TEXT("网络失败触发重连：类型=%d，原因=%s"),
        static_cast<int32>(FailureType), *ErrorString);
    BeginReconnectWindow(TEXT("网络连接已断开，请在保留时间内重连"), TimeoutSeconds);
}

void UGuiyangReconnectSubsystem::HandleTravelFailure(UWorld* World, const ETravelFailure::Type FailureType,
    const FString& ErrorString)
{
    if (World && World->GetGameInstance() != GetGameInstance()) return;
    UE_LOG(LogMahjongReconnect, Warning, TEXT("地图连接失败触发重连：类型=%d，原因=%s"),
        static_cast<int32>(FailureType), *ErrorString);
    BeginReconnectWindow(TEXT("无法进入服务器，请检查网络后重试"), 120);
}

void UGuiyangReconnectSubsystem::BroadcastState()
{
    // UI 只消费状态文本、剩余秒数和可重试标志，不直接读取网络驱动。
    OnReconnectStateChanged.Broadcast(ReconnectStatus, GetRemainingSeconds(), CanRetry());
}
