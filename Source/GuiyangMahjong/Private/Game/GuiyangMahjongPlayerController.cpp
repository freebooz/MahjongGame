#include "Game/GuiyangMahjongPlayerController.h"

#include "Engine/NetConnection.h"
#include "Game/GuiyangClientControllerBridge.h"
#include "Game/GuiyangMahjongGameState.h"
#include "Game/GuiyangMahjongPlayerState.h"
#include "Game/GuiyangServerRequestHandler.h"
#include "GameFramework/GameModeBase.h"
#include "GuiyangMahjong.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "TimerManager.h"

TAtomic<uint64> AGuiyangMahjongPlayerController::ServerRpcReceivedCount(0);

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
    Request.Type = Type;
    Request.RoundId = State->PublicTableState.RoundId;
    Request.TurnId = State->PublicTableState.TurnId;
    Request.TargetTileId = TargetTileId;
    Request.ClientSequence = ++LastClientActionSequence;
    Server_RequestAction(Request);
}

void AGuiyangMahjongPlayerController::Server_AuthenticateSession_Implementation(
    const FString& PlayerId, const FString& DisplayName, const EGuiyangLoginProvider Provider,
    const FString& SessionToken)
{
    ++ServerRpcReceivedCount;
    if (IGuiyangServerRequestHandler* Handler = GetServerRequestHandler())
        Handler->HandleAuthenticateSession(this, PlayerId, DisplayName, Provider, SessionToken);
}

void AGuiyangMahjongPlayerController::Server_RequestCreateRoom_Implementation()
{
    ++ServerRpcReceivedCount;
    if (IGuiyangServerRequestHandler* Handler = GetServerRequestHandler())
        Handler->HandleCreateRoom(this, FMahjongCreateRoomRequest());
}

void AGuiyangMahjongPlayerController::Server_RequestQuickStart_Implementation()
{
    ++ServerRpcReceivedCount;
    if (IGuiyangServerRequestHandler* Handler = GetServerRequestHandler()) Handler->HandleQuickStart(this);
}

void AGuiyangMahjongPlayerController::Server_RequestCreateRoomWithConfig_Implementation(
    const FMahjongCreateRoomRequest& Request)
{
    ++ServerRpcReceivedCount;
    if (IGuiyangServerRequestHandler* Handler = GetServerRequestHandler()) Handler->HandleCreateRoom(this, Request);
}

void AGuiyangMahjongPlayerController::Server_RequestJoinRoom_Implementation(const FString& PlayerName)
{
    ++ServerRpcReceivedCount;
    UE_LOG(LogMahjongServer, Verbose, TEXT("Legacy join request from %s as %s"), *GetName(), *PlayerName);
    if (IGuiyangServerRequestHandler* Handler = GetServerRequestHandler()) Handler->HandleQuickStart(this);
}

void AGuiyangMahjongPlayerController::Server_RequestJoinRoomByCode_Implementation(
    const FMahjongJoinRoomRequest& Request)
{
    ++ServerRpcReceivedCount;
    if (IGuiyangServerRequestHandler* Handler = GetServerRequestHandler()) Handler->HandleJoinRoom(this, Request);
}

void AGuiyangMahjongPlayerController::Server_RequestReady_Implementation()
{
    ++ServerRpcReceivedCount;
    if (IGuiyangServerRequestHandler* Handler = GetServerRequestHandler()) Handler->HandleToggleReady(this);
}

void AGuiyangMahjongPlayerController::Server_RequestLeaveRoom_Implementation()
{
    ++ServerRpcReceivedCount;
    if (IGuiyangServerRequestHandler* Handler = GetServerRequestHandler()) Handler->HandleLeaveRoom(this);
}

void AGuiyangMahjongPlayerController::Server_RequestNextRound_Implementation()
{
    ++ServerRpcReceivedCount;
    if (IGuiyangServerRequestHandler* Handler = GetServerRequestHandler()) Handler->HandleNextRound(this);
}

void AGuiyangMahjongPlayerController::Server_RequestPlayTile_Implementation(const FMahjongTile Tile)
{
    ++ServerRpcReceivedCount;
    if (!Tile.IsValid())
    {
        Client_ShowErrorMessage(TEXT("出牌请求无效"));
        return;
    }
    if (IGuiyangServerRequestHandler* Handler = GetServerRequestHandler())
        Handler->HandleLegacyPlayTile(this, Tile, ++LastClientActionSequence);
}

void AGuiyangMahjongPlayerController::Server_RequestAction_Implementation(const FMahjongActionRequest Request)
{
    ++ServerRpcReceivedCount;
    if (Request.ClientSequence <= LastClientActionSequence || Request.Type == EMahjongActionType::Draw)
    {
        Client_ShowErrorMessage(TEXT("操作已过期或不允许由客户端发起"));
        return;
    }
    LastClientActionSequence = Request.ClientSequence;
    if (IGuiyangServerRequestHandler* Handler = GetServerRequestHandler()) Handler->HandleTableAction(this, Request);
}

void AGuiyangMahjongPlayerController::Server_RequestIntegrationDisconnect_Implementation()
{
    ++ServerRpcReceivedCount;
#if !UE_BUILD_SHIPPING
    const AGuiyangMahjongPlayerState* MahjongPlayer = GetPlayerState<AGuiyangMahjongPlayerState>();
    if (!FParse::Param(FCommandLine::Get(), TEXT("MahjongEnableIntegrationHooks"))
        || !MahjongPlayer || !MahjongPlayer->MahjongPlayerId.StartsWith(TEXT("integration-client-")))
    {
        UE_LOG(LogMahjongReconnect, Warning, TEXT("Rejected unauthorized integration disconnect request"));
        return;
    }
    GetWorldTimerManager().SetTimerForNextTick(FTimerDelegate::CreateWeakLambda(this, [this]()
    {
        if (UNetConnection* Connection = GetNetConnection()) Connection->Close();
    }));
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
}

void AGuiyangMahjongPlayerController::Client_ShowFinalSettlement_Implementation(
    const FMahjongFinalSettlementResult& Result)
{
    if (IGuiyangClientControllerBridge* Bridge = GetClientBridge()) Bridge->NotifyFinalSettlement(Result);
    OnFinalSettlementShown.Broadcast(Result);
}
