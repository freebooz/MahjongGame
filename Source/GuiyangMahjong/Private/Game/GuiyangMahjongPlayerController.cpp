#include "Game/GuiyangMahjongPlayerController.h"

#include "Engine/NetConnection.h"
#include "Engine/World.h"
#include "Game/GuiyangClientControllerBridge.h"
#include "Game/GuiyangMahjongGameState.h"
#include "Game/GuiyangMahjongPlayerState.h"
#include "Game/GuiyangServerRequestHandler.h"
#include "GameFramework/GameModeBase.h"
#include "GuiyangMahjong.h"
#include "HAL/PlatformTime.h"
#include "Misc/ScopeLock.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "TimerManager.h"

TAtomic<uint64> AGuiyangMahjongPlayerController::ServerRpcReceivedCount(0);
FCriticalSection AGuiyangMahjongPlayerController::ServerRpcTelemetryMutex;
TMap<FString, AGuiyangMahjongPlayerController::FServerRpcAccumulator>
    AGuiyangMahjongPlayerController::ServerRpcTelemetryByMethod;

void AGuiyangMahjongPlayerController::RecordServerRpcTelemetry(
    const TCHAR* MethodName, const double StartedAtSeconds, const bool bRejected, const bool bFailed)
{
    const double DurationMilliseconds =
        FMath::Max(0.0, (FPlatformTime::Seconds() - StartedAtSeconds) * 1000.0);
    // 只在累加器更新期间持锁，业务调用均已结束；固定方法名保证 Map 不会被外部输入撑大。
    FScopeLock Lock(&ServerRpcTelemetryMutex);
    FServerRpcAccumulator& Metric = ServerRpcTelemetryByMethod.FindOrAdd(MethodName);
    ++Metric.ReceivedCount;
    if (bRejected) ++Metric.RejectedCount;
    if (bFailed) ++Metric.FailedCount;
    // 同步处理超过一秒即视为超时样本；该阈值用于发现主线程阻塞，不改变 RPC 业务超时。
    if (DurationMilliseconds >= 1000.0) ++Metric.TimeoutCount;
    Metric.RecentDurations.Add(DurationMilliseconds);
    if (Metric.RecentDurations.Num() > 256)
    {
        Metric.RecentDurations.RemoveAt(0, Metric.RecentDurations.Num() - 256, EAllowShrinking::No);
    }
}

TArray<FGuiyangRpcMethodTelemetry> AGuiyangMahjongPlayerController::GetServerRpcTelemetry()
{
    FScopeLock Lock(&ServerRpcTelemetryMutex);
    TArray<FGuiyangRpcMethodTelemetry> Result;
    Result.Reserve(ServerRpcTelemetryByMethod.Num());
    for (const TPair<FString, FServerRpcAccumulator>& Pair : ServerRpcTelemetryByMethod)
    {
        TArray<double> SortedDurations = Pair.Value.RecentDurations;
        SortedDurations.Sort();
        const auto Percentile = [&SortedDurations](const double Quantile)
        {
            if (SortedDurations.IsEmpty()) return 0.0;
            const int32 Index = FMath::Clamp(
                FMath::CeilToInt(Quantile * SortedDurations.Num()) - 1,
                0,
                SortedDurations.Num() - 1);
            return SortedDurations[Index];
        };
        FGuiyangRpcMethodTelemetry Snapshot;
        Snapshot.MethodName = Pair.Key;
        Snapshot.ReceivedCount = Pair.Value.ReceivedCount;
        Snapshot.RejectedCount = Pair.Value.RejectedCount;
        Snapshot.FailedCount = Pair.Value.FailedCount;
        Snapshot.TimeoutCount = Pair.Value.TimeoutCount;
        Snapshot.P95DurationMilliseconds = Percentile(0.95);
        Snapshot.P99DurationMilliseconds = Percentile(0.99);
        Result.Add(MoveTemp(Snapshot));
    }
    Result.Sort([](const FGuiyangRpcMethodTelemetry& Left, const FGuiyangRpcMethodTelemetry& Right)
    {
        return Left.MethodName < Right.MethodName;
    });
    return Result;
}

AGuiyangMahjongPlayerController::AGuiyangMahjongPlayerController()
{
    // ClientRestart and replicated Pawn state can run after the room camera is selected.
    // Keep camera ownership in the client presentation layer for the whole controller lifetime.
    bAutoManageActiveCameraTarget = false;
}

void AGuiyangMahjongPlayerController::BeginPlay()
{
    Super::BeginPlay();
    if (!IsLocalController() || IsRunningDedicatedServer()) return;

    ClientBridge = FGuiyangClientBridgeRegistry::Create(*this);
    if (IGuiyangClientControllerBridge* Bridge = GetClientBridge())
    {
        Bridge->InitializeClient(*this);
    }
    else
    {
        UE_LOG(LogMahjongUI, Error, TEXT("Client presentation module did not register a UI bridge"));
    }
}

IGuiyangClientControllerBridge* AGuiyangMahjongPlayerController::GetClientBridge() const
{
    return ClientBridge ? Cast<IGuiyangClientControllerBridge>(ClientBridge.Get()) : nullptr;
}

IGuiyangServerRequestHandler* AGuiyangMahjongPlayerController::GetServerRequestHandler() const
{
    AGameModeBase* GameMode = GetWorld() ? GetWorld()->GetAuthGameMode<AGameModeBase>() : nullptr;
    return GameMode ? Cast<IGuiyangServerRequestHandler>(GameMode) : nullptr;
}

AActor* AGuiyangMahjongPlayerController::EnsureMahjongRoomPresentation()
{
    if (IGuiyangClientControllerBridge* Bridge = GetClientBridge()) return Bridge->EnsureRoomPresentation();
    return nullptr;
}

void AGuiyangMahjongPlayerController::ConnectToServer(
    const FString& ServerIP, const int32 Port, const FString& PlayerName)
{
    if (IGuiyangClientControllerBridge* Bridge = GetClientBridge())
        Bridge->ConnectToServer(ServerIP, Port, PlayerName);
}

void AGuiyangMahjongPlayerController::ConnectToAllocatedServer(const FGuiyangGameServerRoute& Route)
{
    if (IGuiyangClientControllerBridge* Bridge = GetClientBridge()) Bridge->ConnectToAllocatedServer(Route);
}

void AGuiyangMahjongPlayerController::RetryLastConnection()
{
    if (IGuiyangClientControllerBridge* Bridge = GetClientBridge()) Bridge->RetryLastConnection();
}

void AGuiyangMahjongPlayerController::ReturnToConnectScreen()
{
    if (IGuiyangClientControllerBridge* Bridge = GetClientBridge()) Bridge->ReturnToConnectScreen();
}

void AGuiyangMahjongPlayerController::ReturnToLobby()
{
    if (IGuiyangClientControllerBridge* Bridge = GetClientBridge()) Bridge->ReturnToLobby();
}

void AGuiyangMahjongPlayerController::ShowCreatingRoomLoading()
{
    if (IGuiyangClientControllerBridge* Bridge = GetClientBridge()) Bridge->ShowCreatingRoomLoading();
}

void AGuiyangMahjongPlayerController::RequestCreateRoomWithLoading(const FMahjongCreateRoomRequest& Request)
{
    if (IGuiyangClientControllerBridge* Bridge = GetClientBridge())
        Bridge->RequestCreateRoomWithLoading(Request);
}

void AGuiyangMahjongPlayerController::CompleteRemoteReturnToLobby()
{
    if (IGuiyangClientControllerBridge* Bridge = GetClientBridge()) Bridge->CompleteRemoteReturnToLobby();
}

void AGuiyangMahjongPlayerController::RequestTableAction(
    const EMahjongActionType Type, const int32 TargetTileId)
{
    if (Type == EMahjongActionType::Draw)
    {
        OnErrorShown.Broadcast(TEXT("摸牌只能由服务端发起"));
        return;
    }
    const AGuiyangMahjongGameState* State = GetWorld()
        ? GetWorld()->GetGameState<AGuiyangMahjongGameState>() : nullptr;
    if (!State || State->PublicTableState.RoundId <= 0 || State->PublicTableState.TurnId <= 0)
    {
        OnErrorShown.Broadcast(TEXT("牌局状态尚未同步，请稍后重试"));
        return;
    }
    FMahjongActionRequest Request;
    Request.ClientActionId = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
    Request.Type = Type;
    Request.RoundId = State->PublicTableState.RoundId;
    Request.TurnId = State->PublicTableState.TurnId;
    Request.TargetTileId = TargetTileId;
    Request.ClientSequence = ++LastClientActionSequence;
    Request.ExpectedStateVersion = State->PublicTableState.StateSequence;
    Request.RoomEpoch = State->PublicTableState.RoomEpoch;
    Request.ClientSentAtUnixMilliseconds = FDateTime::UtcNow().ToUnixTimestamp() * 1000;
    Server_RequestAction(Request);
}

void AGuiyangMahjongPlayerController::Server_AuthenticateSession_Implementation(
    const FString& PlayerId, const FString& DisplayName, const EGuiyangLoginProvider Provider,
    const FString& SessionToken)
{
    const double StartedAtSeconds = FPlatformTime::Seconds();
    ++ServerRpcReceivedCount;
    IGuiyangServerRequestHandler* Handler = GetServerRequestHandler();
    if (Handler)
        Handler->HandleAuthenticateSession(this, PlayerId, DisplayName, Provider, SessionToken);
    RecordServerRpcTelemetry(TEXT("Server.AuthenticateSession"), StartedAtSeconds, false, Handler == nullptr);
}

void AGuiyangMahjongPlayerController::Server_RequestCreateRoom_Implementation()
{
    const double StartedAtSeconds = FPlatformTime::Seconds();
    ++ServerRpcReceivedCount;
    IGuiyangServerRequestHandler* Handler = GetServerRequestHandler();
    if (Handler)
        Handler->HandleCreateRoom(this, FMahjongCreateRoomRequest());
    RecordServerRpcTelemetry(TEXT("Server.RequestCreateRoom"), StartedAtSeconds, false, Handler == nullptr);
}

void AGuiyangMahjongPlayerController::Server_RequestQuickStart_Implementation()
{
    const double StartedAtSeconds = FPlatformTime::Seconds();
    ++ServerRpcReceivedCount;
    IGuiyangServerRequestHandler* Handler = GetServerRequestHandler();
    if (Handler) Handler->HandleQuickStart(this);
    RecordServerRpcTelemetry(TEXT("Server.RequestQuickStart"), StartedAtSeconds, false, Handler == nullptr);
}

void AGuiyangMahjongPlayerController::Server_RequestCreateRoomWithConfig_Implementation(
    const FMahjongCreateRoomRequest& Request)
{
    const double StartedAtSeconds = FPlatformTime::Seconds();
    ++ServerRpcReceivedCount;
    IGuiyangServerRequestHandler* Handler = GetServerRequestHandler();
    if (Handler) Handler->HandleCreateRoom(this, Request);
    RecordServerRpcTelemetry(
        TEXT("Server.RequestCreateRoomWithConfig"), StartedAtSeconds, false, Handler == nullptr);
}

void AGuiyangMahjongPlayerController::Server_RequestJoinRoom_Implementation(const FString& PlayerName)
{
    const double StartedAtSeconds = FPlatformTime::Seconds();
    ++ServerRpcReceivedCount;
    UE_LOG(LogMahjongServer, Verbose, TEXT("Legacy join request from %s as %s"), *GetName(), *PlayerName);
    IGuiyangServerRequestHandler* Handler = GetServerRequestHandler();
    if (Handler) Handler->HandleQuickStart(this);
    RecordServerRpcTelemetry(TEXT("Server.RequestJoinRoomLegacy"), StartedAtSeconds, false, Handler == nullptr);
}

void AGuiyangMahjongPlayerController::Server_RequestJoinRoomByCode_Implementation(
    const FMahjongJoinRoomRequest& Request)
{
    const double StartedAtSeconds = FPlatformTime::Seconds();
    ++ServerRpcReceivedCount;
    IGuiyangServerRequestHandler* Handler = GetServerRequestHandler();
    if (Handler) Handler->HandleJoinRoom(this, Request);
    RecordServerRpcTelemetry(TEXT("Server.RequestJoinRoomByCode"), StartedAtSeconds, false, Handler == nullptr);
}

void AGuiyangMahjongPlayerController::Server_RequestReady_Implementation()
{
    const double StartedAtSeconds = FPlatformTime::Seconds();
    ++ServerRpcReceivedCount;
    IGuiyangServerRequestHandler* Handler = GetServerRequestHandler();
    if (Handler) Handler->HandleToggleReady(this);
    RecordServerRpcTelemetry(TEXT("Server.RequestReady"), StartedAtSeconds, false, Handler == nullptr);
}

void AGuiyangMahjongPlayerController::Server_RequestLeaveRoom_Implementation()
{
    const double StartedAtSeconds = FPlatformTime::Seconds();
    ++ServerRpcReceivedCount;
    IGuiyangServerRequestHandler* Handler = GetServerRequestHandler();
    if (Handler) Handler->HandleLeaveRoom(this);
    RecordServerRpcTelemetry(TEXT("Server.RequestLeaveRoom"), StartedAtSeconds, false, Handler == nullptr);
}

void AGuiyangMahjongPlayerController::Server_RequestSetTrustee_Implementation(
    const bool bEnabled)
{
    const double StartedAtSeconds = FPlatformTime::Seconds();
    ++ServerRpcReceivedCount;
    IGuiyangServerRequestHandler* Handler = GetServerRequestHandler();
    if (Handler)
    {
        Handler->HandleSetTrustee(this, bEnabled);
    }
    RecordServerRpcTelemetry(
        TEXT("Server.RequestSetTrustee"), StartedAtSeconds, false, Handler == nullptr);
}

void AGuiyangMahjongPlayerController::Server_RequestNextRound_Implementation()
{
    const double StartedAtSeconds = FPlatformTime::Seconds();
    ++ServerRpcReceivedCount;
    IGuiyangServerRequestHandler* Handler = GetServerRequestHandler();
    if (Handler) Handler->HandleNextRound(this);
    RecordServerRpcTelemetry(TEXT("Server.RequestNextRound"), StartedAtSeconds, false, Handler == nullptr);
}

void AGuiyangMahjongPlayerController::Server_RequestPlayTile_Implementation(const FMahjongTile Tile)
{
    const double StartedAtSeconds = FPlatformTime::Seconds();
    ++ServerRpcReceivedCount;
    if (!Tile.IsValid())
    {
        Client_ShowErrorMessage(TEXT("出牌请求无效"));
        RecordServerRpcTelemetry(TEXT("Server.RequestPlayTile"), StartedAtSeconds, true, false);
        return;
    }
    IGuiyangServerRequestHandler* Handler = GetServerRequestHandler();
    if (Handler)
        Handler->HandleLegacyPlayTile(this, Tile, ++LastClientActionSequence);
    RecordServerRpcTelemetry(TEXT("Server.RequestPlayTile"), StartedAtSeconds, false, Handler == nullptr);
}

void AGuiyangMahjongPlayerController::Server_RequestAction_Implementation(const FMahjongActionRequest Request)
{
    const double StartedAtSeconds = FPlatformTime::Seconds();
    ++ServerRpcReceivedCount;
    if (Request.ClientSequence <= LastClientActionSequence || Request.Type == EMahjongActionType::Draw)
    {
        Client_ShowErrorMessage(TEXT("操作已过期或不允许由客户端发起"));
        RecordServerRpcTelemetry(TEXT("Server.RequestAction"), StartedAtSeconds, true, false);
        return;
    }
    LastClientActionSequence = Request.ClientSequence;
    IGuiyangServerRequestHandler* Handler = GetServerRequestHandler();
    if (Handler) Handler->HandleTableAction(this, Request);
    RecordServerRpcTelemetry(TEXT("Server.RequestAction"), StartedAtSeconds, false, Handler == nullptr);
}

void AGuiyangMahjongPlayerController::Server_ConfirmReconnectState_Implementation(
    const FString& ControlToken,
    const int32 StateVersion,
    const FString& PublicStateHash)
{
    const double StartedAtSeconds = FPlatformTime::Seconds();
    ++ServerRpcReceivedCount;
    IGuiyangServerRequestHandler* Handler = GetServerRequestHandler();
    if (Handler) Handler->HandleReconnectStateConfirmed(this, ControlToken, StateVersion, PublicStateHash);
    RecordServerRpcTelemetry(
        TEXT("Server.ConfirmReconnectState"), StartedAtSeconds, false, Handler == nullptr);
}

void AGuiyangMahjongPlayerController::Server_RequestIntegrationDisconnect_Implementation()
{
    const double StartedAtSeconds = FPlatformTime::Seconds();
    ++ServerRpcReceivedCount;
#if !UE_BUILD_SHIPPING
    const AGuiyangMahjongPlayerState* MahjongPlayer = GetPlayerState<AGuiyangMahjongPlayerState>();
    if (!FParse::Param(FCommandLine::Get(), TEXT("MahjongEnableIntegrationHooks"))
        || !MahjongPlayer || !MahjongPlayer->MahjongPlayerId.StartsWith(TEXT("integration-client-")))
    {
        UE_LOG(LogMahjongReconnect, Warning, TEXT("Rejected unauthorized integration disconnect request"));
        RecordServerRpcTelemetry(TEXT("Server.RequestIntegrationDisconnect"), StartedAtSeconds, true, false);
        return;
    }
    GetWorldTimerManager().SetTimerForNextTick(FTimerDelegate::CreateWeakLambda(this, [this]()
    {
        if (UNetConnection* Connection = GetNetConnection()) Connection->Close();
    }));
    RecordServerRpcTelemetry(TEXT("Server.RequestIntegrationDisconnect"), StartedAtSeconds, false, false);
#else
    // Shipping 构建明确拒绝集成钩子并记录拒绝，不允许隐式成功。
    RecordServerRpcTelemetry(TEXT("Server.RequestIntegrationDisconnect"), StartedAtSeconds, true, false);
#endif
}

void AGuiyangMahjongPlayerController::Client_UpdatePrivateHand_Implementation(
    const FMahjongPrivatePlayerState& PrivateState)
{
    LastClientActionSequence = FMath::Max(LastClientActionSequence, PrivateState.LastAcceptedClientSequence);
    OnPrivateHandUpdated.Broadcast(PrivateState);
    if (IGuiyangClientControllerBridge* Bridge = GetClientBridge())
        Bridge->HandleIntegrationPrivateState(PrivateState);
}

void AGuiyangMahjongPlayerController::Client_ShowAvailableActions_Implementation(
    const TArray<FMahjongAction>& Actions)
{
    LastAvailableActions = Actions;
    OnAvailableActionsUpdated.Broadcast(Actions);
}

void AGuiyangMahjongPlayerController::Client_ShowSettlement_Implementation(const FMahjongSettlementResult& Result)
{
    OnSettlementShown.Broadcast(Result);
}

void AGuiyangMahjongPlayerController::Client_ShowErrorMessage_Implementation(const FString& Message)
{
    UE_LOG(LogMahjongUI, Warning, TEXT("Client message: %s"), *Message);
    OnErrorShown.Broadcast(Message);
}

void AGuiyangMahjongPlayerController::Client_RestoreReconnectSnapshot_Implementation(
    const FMahjongReconnectSnapshot& Snapshot, const TArray<FMahjongAction>& AvailableActions)
{
    LastClientActionSequence = Snapshot.PrivateState.LastAcceptedClientSequence;
    LastAvailableActions = AvailableActions;
    if (IGuiyangClientControllerBridge* Bridge = GetClientBridge()) Bridge->NotifyReconnectRestored(Snapshot);
    OnReconnectRestored.Broadcast(Snapshot);
    OnPrivateHandUpdated.Broadcast(Snapshot.PrivateState);
    OnAvailableActionsUpdated.Broadcast(AvailableActions);
    // 仅在 UI/本地状态均已应用后确认；Token 只绑定本次控制权迁移，不是登录凭据。
    if (!Snapshot.ControlToken.IsEmpty())
        Server_ConfirmReconnectState(
            Snapshot.ControlToken, Snapshot.TableState.StateSequence, Snapshot.TableState.PublicStateHash);
}

void AGuiyangMahjongPlayerController::Client_ShowFinalSettlement_Implementation(
    const FMahjongFinalSettlementResult& Result)
{
    if (IGuiyangClientControllerBridge* Bridge = GetClientBridge()) Bridge->NotifyFinalSettlement(Result);
    OnFinalSettlementShown.Broadcast(Result);
}

void AGuiyangMahjongPlayerController::Client_ConfirmLeaveRoom_Implementation()
{
    CompleteRemoteReturnToLobby();
}

void AGuiyangMahjongPlayerController::Client_UpdateTrusteeState_Implementation(
    const bool bEnabled)
{
    OnTrusteeStateChanged.Broadcast(bEnabled);
}
