#include "Game/GuiyangMahjongGameMode.h"

#include "Game/GuiyangMahjongGameState.h"
#include "Game/GuiyangMahjongPlayerController.h"
#include "Game/GuiyangMahjongPlayerState.h"
#include "GuiyangMahjong.h"
#include "Engine/GameInstance.h"
#include "Room/GuiyangManagedRoomDefinition.h"
#include "Room/GuiyangRoomManager.h"
#include "Runtime/Launch/Resources/Version.h"
#include "Server/GuiyangAgonesLifecycleSubsystem.h"
#include "Server/GuiyangGameServerBridge.h"
#include "Table/MahjongTableEngine.h"
#include "HAL/PlatformTime.h"
#include "EngineUtils.h"
#include "Misc/SecureHash.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "HAL/PlatformMisc.h"
#include "GenericPlatform/GenericPlatformHttp.h"
#include "Kismet/GameplayStatics.h"

namespace
{
    bool IsFullMatchIntegrationEnabled()
    {
        return FParse::Param(FCommandLine::Get(), TEXT("MahjongEnableIntegrationHooks"))
            && FParse::Param(FCommandLine::Get(), TEXT("MahjongIntegrationFullMatch"));
    }
}

AGuiyangMahjongGameMode::AGuiyangMahjongGameMode()
{
    GameStateClass = AGuiyangMahjongGameState::StaticClass();
    PlayerControllerClass = AGuiyangMahjongPlayerController::StaticClass();
    PlayerStateClass = AGuiyangMahjongPlayerState::StaticClass();
}

bool AGuiyangMahjongGameMode::GetPlayerConnectionTelemetry(
    const FString& PlayerId, FGuiyangPlayerConnectionTelemetry& OutTelemetry) const
{
    return RoomManager && RoomManager->GetPlayerConnectionTelemetry(PlayerId, OutTelemetry);
}

bool AGuiyangMahjongGameMode::GetPlayerTrusteeTelemetry(
    const FString& PlayerId, bool& OutTrustee, FDateTime& OutChangedAtUtc) const
{
    const FPlayerTrusteeState* State = TrusteeStateByPlayer.Find(PlayerId);
    if (!State) return false;
    OutTrustee = State->bTrustee;
    OutChangedAtUtc = State->ChangedAtUtc;
    return true;
}

void AGuiyangMahjongGameMode::InitGame(const FString& MapName, const FString& Options, FString& ErrorMessage)
{
    // 在玩家连接前确定本地、Allocator 或 Agones 模式，托管模式缺少安全配置时拒绝启动。
    Super::InitGame(MapName, Options, ErrorMessage);
    RoomManager = NewObject<UGuiyangRoomManager>(this);
    bAgonesGameServer = IsRunningDedicatedServer()
        && UGuiyangAgonesLifecycleSubsystem::IsAgonesRequested(
            FCommandLine::Get(),
            FPlatformMisc::GetEnvironmentVariable(TEXT("MAHJONG_ORCHESTRATOR")));
    bManagedGameServer = IsRunningDedicatedServer()
        && (bAgonesGameServer || FParse::Param(FCommandLine::Get(), TEXT("MahjongManagedGameServer")));
    if (bAgonesGameServer)
    {
        UGuiyangAgonesLifecycleSubsystem* Lifecycle = GetGameInstance()
            ? GetGameInstance()->GetSubsystem<UGuiyangAgonesLifecycleSubsystem>()
            : nullptr;
        if (!Lifecycle || !Lifecycle->IsActive())
        {
            ErrorMessage = TEXT("AGONES_LIFECYCLE_UNAVAILABLE");
            UE_LOG(LogMahjongServer, Error, TEXT("Agones GameServer lifecycle is unavailable"));
            return;
        }
        Lifecycle->OnAllocationReady().AddUObject(
            this, &ThisClass::HandleAgonesAllocationReady);
        FGuiyangGameServerLaunchConfig Config;
        if (Lifecycle->TryGetAllocationConfig(Config)) HandleAgonesAllocationReady(Config);
    }
    else if (bManagedGameServer)
    {
        FGuiyangGameServerLaunchConfig Config;
        const FString SigningKey = FPlatformMisc::GetEnvironmentVariable(TEXT("MAHJONG_JOIN_TICKET_SIGNING_KEY"));
        const FString RegistrationCredential =
            FPlatformMisc::GetEnvironmentVariable(TEXT("MAHJONG_REGISTRATION_CREDENTIAL"));
        const FString MatchResultOutboxPath =
            FPlatformMisc::GetEnvironmentVariable(TEXT("MAHJONG_MATCH_RESULT_OUTBOX_PATH"));
        FString ConfigError;
        if (!FGuiyangGameServerLaunchConfig::TryParse(
            FCommandLine::Get(), SigningKey, RegistrationCredential, MatchResultOutboxPath,
            Config, ConfigError))
        {
            ErrorMessage = ConfigError;
            UE_LOG(LogMahjongServer, Error, TEXT("Managed GameServer configuration rejected: %s"), *ConfigError);
            return;
        }
        // UEngine::LoadMap creates the listen NetDriver only after SetGameMode/InitGame
        // returns. Registering here can therefore publish an endpoint which is not
        // listening yet. Defer registration until BeginPlay, then require a NetDriver.
        PendingManagedConfig = MoveTemp(Config);
        bHasPendingManagedConfig = true;
    }
}

void AGuiyangMahjongGameMode::BeginPlay()
{
    Super::BeginPlay();
    bManagedWorldReady = true;
    TryInitializeManagedBridgeAfterListen();
}

void AGuiyangMahjongGameMode::InitializeManagedBridge(
    const FGuiyangGameServerLaunchConfig& Config)
{
    // 控制面桥接只能初始化一次，并且必须等待监听端口和 World 同时可用。
    if (GameServerBridge) return;
    FString ConfigError;
    UGuiyangGameServerBridge* Bridge = NewObject<UGuiyangGameServerBridge>(this);
    if (!Bridge->Initialize(GetWorld(), Config, ConfigError))
    {
        UE_LOG(LogMahjongServer, Error, TEXT("Managed GameServer bridge failed: %s"), *ConfigError);
        if (bAgonesGameServer)
        {
            if (UGameInstance* GameInstance = GetGameInstance())
            {
                if (UGuiyangAgonesLifecycleSubsystem* Lifecycle =
                    GameInstance->GetSubsystem<UGuiyangAgonesLifecycleSubsystem>())
                {
                    Lifecycle->RequestShutdown();
                }
            }
        }
        return;
    }
    GameServerBridge = Bridge;
}

void AGuiyangMahjongGameMode::TryInitializeManagedBridgeAfterListen()
{
    if (!bManagedGameServer || !bManagedWorldReady || !bHasPendingManagedConfig || GameServerBridge)
    {
        return;
    }
    if (!GetWorld() || !GetWorld()->GetNetDriver())
    {
        UE_LOG(LogMahjongServer, Error,
            TEXT("Managed GameServer refused registration because the listen NetDriver is unavailable"));
        FPlatformMisc::RequestExitWithStatus(false, 78);
        return;
    }
    InitializeManagedBridge(PendingManagedConfig);
    if (!GameServerBridge)
    {
        UE_LOG(LogMahjongServer, Error,
            TEXT("Managed GameServer bridge initialization failed after NetDriver startup"));
        FPlatformMisc::RequestExitWithStatus(false, 78);
        return;
    }
    bHasPendingManagedConfig = false;
    PendingManagedConfig = {};
}

void AGuiyangMahjongGameMode::HandleAgonesAllocationReady(
    const FGuiyangGameServerLaunchConfig& Config)
{
    PendingManagedConfig = Config;
    bHasPendingManagedConfig = true;
    TryInitializeManagedBridgeAfterListen();
}

void AGuiyangMahjongGameMode::PreLogin(const FString& Options, const FString& Address,
    const FUniqueNetIdRepl& UniqueId, FString& ErrorMessage)
{
    // 托管游戏服在生成 PlayerController 前验证并消费一次性入场票据。
    Super::PreLogin(Options, Address, UniqueId, ErrorMessage);
    if (!ErrorMessage.IsEmpty() || !bManagedGameServer) return;
    if (!GameServerBridge)
    {
        ErrorMessage = TEXT("GAMESERVER_CONFIGURATION_INVALID");
        return;
    }
    if (!GameServerBridge->IsRegistered())
    {
        ErrorMessage = TEXT("GAMESERVER_NOT_REGISTERED");
        return;
    }

    const FString PlayerId = FGenericPlatformHttp::UrlDecode(
        UGameplayStatics::ParseOption(Options, TEXT("PlayerId")));
    const FString JoinTicket = FGenericPlatformHttp::UrlDecode(
        UGameplayStatics::ParseOption(Options, TEXT("JoinTicket")));
    FGuiyangJoinTicketClaims Claims;
    if (!GameServerBridge->ValidateAndConsumeJoinTicket(JoinTicket, PlayerId, Claims, ErrorMessage))
    {
        UE_LOG(LogMahjongServer, Warning,
            TEXT("Managed player rejected before login InstanceId=%s Reason=%s"),
            *GameServerBridge->GetConfig().ServerInstanceId, *ErrorMessage);
        return;
    }
    const int64 NowUnixSeconds = FDateTime::UtcNow().ToUnixTimestamp();
    for (auto It = PendingTicketExpiryByDigest.CreateIterator(); It; ++It)
    {
        if (It.Value() <= NowUnixSeconds)
        {
            PendingAuthorizedPlayersByTicketDigest.Remove(It.Key());
            PendingAuthorizedDisplayNamesByTicketDigest.Remove(It.Key());
            It.RemoveCurrent();
        }
    }
    const FString TicketDigest = HashJoinTicket(JoinTicket);
    PendingAuthorizedPlayersByTicketDigest.Add(TicketDigest, Claims.PlayerId);
    PendingAuthorizedDisplayNamesByTicketDigest.Add(
        TicketDigest, Claims.DisplayName.TrimStartAndEnd());
    PendingTicketExpiryByDigest.Add(TicketDigest, Claims.ExpiresAtUnixSeconds);
}

FString AGuiyangMahjongGameMode::InitNewPlayer(APlayerController* NewPlayerController,
    const FUniqueNetIdRepl& UniqueId, const FString& Options, const FString& Portal)
{
    // 将 PreLogin 暂存的玩家 ID 绑定到实际网络连接，避免信任后续客户端自报身份。
    const FString Result = Super::InitNewPlayer(NewPlayerController, UniqueId, Options, Portal);
    if (!Result.IsEmpty() || !bManagedGameServer) return Result;
    if (!GameServerBridge) return TEXT("GAMESERVER_CONFIGURATION_INVALID");
    const FString JoinTicket = FGenericPlatformHttp::UrlDecode(
        UGameplayStatics::ParseOption(Options, TEXT("JoinTicket")));
    const FString TicketDigest = HashJoinTicket(JoinTicket);
    FString PlayerId;
    FString DisplayName;
    int64 TicketExpiry = 0;
    const bool bHasPlayerBinding = PendingAuthorizedPlayersByTicketDigest.RemoveAndCopyValue(TicketDigest, PlayerId);
    const bool bHasDisplayName =
        PendingAuthorizedDisplayNamesByTicketDigest.RemoveAndCopyValue(TicketDigest, DisplayName);
    const bool bHasExpiry = PendingTicketExpiryByDigest.RemoveAndCopyValue(TicketDigest, TicketExpiry);
    if (!bHasPlayerBinding || !bHasDisplayName || !bHasExpiry
        || TicketExpiry <= FDateTime::UtcNow().ToUnixTimestamp()
        || PlayerId.IsEmpty() || DisplayName.IsEmpty() || !NewPlayerController)
    {
        return TEXT("JOIN_TICKET_BINDING_FAILED");
    }
    AuthorizedPlayerIdsByController.Add(NewPlayerController, MoveTemp(PlayerId));
    AuthorizedDisplayNamesByController.Add(NewPlayerController, MoveTemp(DisplayName));
    return FString();
}

void AGuiyangMahjongGameMode::GetConnectedAuthorizedPlayerIds(
    TArray<FString>& OutPlayerIds) const
{
    OutPlayerIds.Reset();
    TSet<FString> UniquePlayerIds;
    for (const TPair<TObjectPtr<APlayerController>, FString>& Entry
        : AuthorizedPlayerIdsByController)
    {
        const APlayerController* Controller = Entry.Key.Get();
        if (!IsValid(Controller) || !Controller->GetNetConnection()
            || Entry.Value.IsEmpty() || UniquePlayerIds.Contains(Entry.Value))
        {
            continue;
        }
        UniquePlayerIds.Add(Entry.Value);
        OutPlayerIds.Add(Entry.Value);
    }
}

void AGuiyangMahjongGameMode::PostLogin(APlayerController* NewPlayer)
{
    Super::PostLogin(NewPlayer);
    if (const FString* AuthorizedPlayerId = AuthorizedPlayerIdsByController.Find(NewPlayer))
    {
        const FString PlayerId = *AuthorizedPlayerId;
        if (UGameInstance* GameInstance = GetGameInstance())
        {
            if (UGuiyangAgonesLifecycleSubsystem* Lifecycle =
                GameInstance->GetSubsystem<UGuiyangAgonesLifecycleSubsystem>())
            {
                Lifecycle->NotifyPlayerConnected(PlayerId);
            }
        }

        // The short-lived signed JoinTicket is the authoritative admission credential for a
        // managed GameServer. Complete the server session and room admission here instead of
        // waiting for a second client profile RPC. This keeps direct/reconnect travel from
        // getting stuck on the creating-room screen when the client login UI has no lobby token.
        const FString* AuthorizedDisplayName = AuthorizedDisplayNamesByController.Find(NewPlayer);
        const FString DisplayName = AuthorizedDisplayName ? *AuthorizedDisplayName : FString();
        AGuiyangMahjongPlayerController* MahjongController =
            Cast<AGuiyangMahjongPlayerController>(NewPlayer);
        AGuiyangMahjongPlayerState* MahjongPlayer = MahjongController
            ? MahjongController->GetPlayerState<AGuiyangMahjongPlayerState>()
            : nullptr;
        if (DisplayName.IsEmpty() || !MahjongPlayer
            || !MahjongPlayer->AuthenticateServer(
                PlayerId, DisplayName, EGuiyangLoginProvider::Guest))
        {
            if (MahjongController)
            {
                MahjongController->Client_ShowErrorMessage(TEXT("入场票据身份初始化失败"));
            }
            return;
        }

        FMahjongRoomState State;
        if (!RoomManager || ManagedRoomCode.IsEmpty())
        {
            MahjongController->Client_ShowErrorMessage(TEXT("托管房间尚未就绪"));
            return;
        }
        EMahjongRoomError Error = EMahjongRoomError::None;
        if (!RoomManager->AdmitManagedPlayer(
                ManagedRoomCode, PlayerId, DisplayName, State, Error))
        {
            MahjongController->Client_ShowErrorMessage(ErrorToMessage(Error));
            return;
        }
        const FMahjongSeatInfo* Seat = State.Seats.FindByPredicate(
            [&PlayerId](const FMahjongSeatInfo& Item)
            {
                return Item.bOccupied && Item.PlayerId == PlayerId;
            });
        MahjongPlayer->EnterRoomServer(
            State.RoomInfo.RoomId,
            Seat ? Seat->SeatIndex : INDEX_NONE,
            Seat ? Seat->bReady : false);
        PublishRoomState(State);
        if (State.Lifecycle == EMahjongRoomLifecycle::Starting)
        {
            TryStartTable(State);
        }
    }
}

void AGuiyangMahjongGameMode::Logout(AController* Exiting)
{
    // 先记录可重连离线状态，再移除连接级授权；托管房主不会因掉线立即销毁远程房间。
    if (const FString* PlayerId = AuthorizedPlayerIdsByController.Find(Cast<APlayerController>(Exiting)))
    {
        if (UGameInstance* GameInstance = GetGameInstance())
        {
            if (UGuiyangAgonesLifecycleSubsystem* Lifecycle =
                GameInstance->GetSubsystem<UGuiyangAgonesLifecycleSubsystem>())
            {
                Lifecycle->NotifyPlayerDisconnected(*PlayerId);
            }
        }
    }
    if (AGuiyangMahjongPlayerController* Controller = Cast<AGuiyangMahjongPlayerController>(Exiting))
    {
        AGuiyangMahjongPlayerState* Player = Controller->GetPlayerState<AGuiyangMahjongPlayerState>();
        FMahjongRoomState State;
        if (Player && RoomManager && RoomManager->GetRoomState(Player->RoomCode, State)
            && (State.Lifecycle == EMahjongRoomLifecycle::Playing
                || State.Lifecycle == EMahjongRoomLifecycle::WaitingNextRound
                || State.Lifecycle == EMahjongRoomLifecycle::Starting
                || State.Lifecycle == EMahjongRoomLifecycle::Settlement))
        {
            EMahjongRoomError Error;
            if (RoomManager->MarkDisconnected(Player->MahjongPlayerId, State, Error)) PublishRoomState(State);
        }
        else
        {
            HandleLeaveRoom(Controller);
        }
    }
    AuthorizedPlayerIdsByController.Remove(Cast<APlayerController>(Exiting));
    AuthorizedDisplayNamesByController.Remove(Cast<APlayerController>(Exiting));
    Super::Logout(Exiting);
}

void AGuiyangMahjongGameMode::EndPlay(const EEndPlayReason::Type EndPlayReason)
{
    if (UGameInstance* GameInstance = GetGameInstance())
    {
        if (UGuiyangAgonesLifecycleSubsystem* Lifecycle =
            GameInstance->GetSubsystem<UGuiyangAgonesLifecycleSubsystem>())
        {
            Lifecycle->OnAllocationReady().RemoveAll(this);
            Lifecycle->RequestShutdown();
        }
    }
    if (GameServerBridge) GameServerBridge->Shutdown();
    PendingAuthorizedPlayersByTicketDigest.Reset();
    PendingAuthorizedDisplayNamesByTicketDigest.Reset();
    PendingTicketExpiryByDigest.Reset();
    AuthorizedPlayerIdsByController.Reset();
    AuthorizedDisplayNamesByController.Reset();
    ManagedRoomCode.Reset();
    Super::EndPlay(EndPlayReason);
}

bool AGuiyangMahjongGameMode::InitializeManagedRoomAuthority(
    const FGuiyangManagedRoomDefinition& Definition, FString& OutError)
{
    // 一个独立服务器进程只承载一个托管房间，重复 Bootstrap 必须幂等拒绝。
    OutError.Reset();
    if (!bManagedGameServer || !RoomManager || !ManagedRoomCode.IsEmpty())
    {
        OutError = TEXT("ROOM_AUTHORITY_STATE_INVALID");
        return false;
    }
    FMahjongRoomState State;
    EMahjongRoomError Error;
    if (!RoomManager->CreateManagedRoom(Definition, State, Error))
    {
        OutError = TEXT("ROOM_AUTHORITY_INITIALIZATION_FAILED");
        return false;
    }
    ManagedRoomCode = Definition.RoomCode;
    PublishRoomState(State);
    UE_LOG(LogMahjongServer, Display,
        TEXT("Managed room initialized BackendRoomId=%s RoomCode=%s MatchId=%s RuleHash=%s"),
        *Definition.BackendRoomId, *Definition.RoomCode, *Definition.MatchId, *Definition.RuleSnapshot.RuleHash);
    return true;
}

void AGuiyangMahjongGameMode::HandleAuthenticateSession(AGuiyangMahjongPlayerController* Controller,
    const FString& PlayerId, const FString& DisplayName, const EGuiyangLoginProvider Provider,
    const FString& SessionToken)
{
    // 本地模式校验会话摘要；托管模式只接受已经由入场票据绑定的玩家身份。
    const FString CleanPlayerId = PlayerId.TrimStartAndEnd();
    const FString CleanDisplayName = DisplayName.TrimStartAndEnd();
    const bool bProviderAllowed = Provider == EGuiyangLoginProvider::Guest
        || Provider == EGuiyangLoginProvider::SimulatedWechat;
    if (bManagedGameServer)
    {
        const FString* AuthorizedPlayerId = AuthorizedPlayerIdsByController.Find(Controller);
        if (!AuthorizedPlayerId || *AuthorizedPlayerId != CleanPlayerId)
        {
            if (Controller) Controller->Client_ShowErrorMessage(TEXT("入场票据与玩家身份不匹配"));
            return;
        }
    }
    if (!Controller || CleanPlayerId.IsEmpty() || CleanPlayerId.Len() > 80
        || CleanDisplayName.IsEmpty() || CleanDisplayName.Len() > 24
        || (!bManagedGameServer && (SessionToken.Len() < 16 || SessionToken.Len() > 256))
        || !bProviderAllowed)
    {
        if (Controller) Controller->Client_ShowErrorMessage(TEXT("登录会话格式无效"));
        return;
    }

    // A managed GameServer has already authenticated this connection in PreLogin with a
    // short-lived, single-use JoinTicket and bound the claimed player to this controller in
    // InitNewPlayer. The Auth access token sent by this profile RPC is a different credential
    // and may legitimately rotate between logins, so it must not be compared with a previous
    // connection's access-token digest. Legacy/local servers do not have JoinTicket admission
    // and therefore retain the session-token continuity check below.
    if (!bManagedGameServer)
    {
        const FString CandidateDigest = HashSessionToken(SessionToken);
        if (CandidateDigest.IsEmpty())
        {
            Controller->Client_ShowErrorMessage(TEXT("登录会话校验失败"));
            return;
        }
        if (const FString* ExistingDigest = SessionTokenDigestsByPlayer.Find(CleanPlayerId))
        {
            if (!ConstantTimeDigestEquals(*ExistingDigest, CandidateDigest))
            {
                Controller->Client_ShowErrorMessage(TEXT("重连凭据不匹配"));
                return;
            }
        }
        else
        {
            SessionTokenDigestsByPlayer.Add(CleanPlayerId, CandidateDigest);
        }
    }

    for (TActorIterator<AGuiyangMahjongPlayerController> It(GetWorld()); It; ++It)
    {
        if (*It == Controller) continue;
        const AGuiyangMahjongPlayerState* Other = It->GetPlayerState<AGuiyangMahjongPlayerState>();
        if (Other && Other->MahjongPlayerId == CleanPlayerId && Other->HasValidServerSession())
        {
            Controller->Client_ShowErrorMessage(TEXT("该账号已经在线"));
            return;
        }
    }

    AGuiyangMahjongPlayerState* Player = Controller->GetPlayerState<AGuiyangMahjongPlayerState>();
    if (!Player || !Player->AuthenticateServer(CleanPlayerId, CleanDisplayName, Provider))
    {
        Controller->Client_ShowErrorMessage(TEXT("服务器认证失败"));
        return;
    }

    // Managed connections were counted immediately after their signed join ticket was accepted.
    // A non-managed Agones server learns the stable player id only after this profile RPC, so
    // register it here exactly once and retain the controller binding for Logout.
    if (!AuthorizedPlayerIdsByController.Contains(Controller))
    {
        AuthorizedPlayerIdsByController.Add(Controller, CleanPlayerId);
        if (UGameInstance* GameInstance = GetGameInstance())
        {
            if (UGuiyangAgonesLifecycleSubsystem* Lifecycle =
                GameInstance->GetSubsystem<UGuiyangAgonesLifecycleSubsystem>())
            {
                Lifecycle->NotifyPlayerConnected(CleanPlayerId);
            }
        }
    }

    FString RoomCode;
    if (!RoomManager)
    {
        Controller->Client_ShowErrorMessage(TEXT("房间服务尚未就绪"));
        return;
    }
    if (bManagedGameServer && !RoomManager->GetPlayerRoomCode(CleanPlayerId, RoomCode))
    {
        if (ManagedRoomCode.IsEmpty())
        {
            Controller->Client_ShowErrorMessage(TEXT("托管房间尚未就绪"));
            return;
        }
        FMahjongRoomState AdmittedState;
        EMahjongRoomError AdmitError;
        if (!RoomManager->AdmitManagedPlayer(ManagedRoomCode, CleanPlayerId, CleanDisplayName,
            AdmittedState, AdmitError))
        {
            Controller->Client_ShowErrorMessage(ErrorToMessage(AdmitError));
            return;
        }
        const FMahjongSeatInfo* AdmittedSeat = AdmittedState.Seats.FindByPredicate(
            [&CleanPlayerId](const FMahjongSeatInfo& Item) { return Item.PlayerId == CleanPlayerId; });
        Player->EnterRoomServer(AdmittedState.RoomInfo.RoomId,
            AdmittedSeat ? AdmittedSeat->SeatIndex : INDEX_NONE,
            AdmittedSeat ? AdmittedSeat->bReady : false);
        PublishRoomState(AdmittedState);
        return;
    }
    if (!RoomManager->GetPlayerRoomCode(CleanPlayerId, RoomCode)) return;
    FMahjongRoomState State;
    EMahjongRoomError Error;
    int32 RemainingSeconds = 0;
    if (!RoomManager->ReconnectPlayer(CleanPlayerId, State, RemainingSeconds, Error))
    {
        Controller->Client_ShowErrorMessage(ErrorToMessage(Error));
        return;
    }
    const FMahjongSeatInfo* Seat = State.Seats.FindByPredicate([&CleanPlayerId](const FMahjongSeatInfo& Item)
    {
        return Item.PlayerId == CleanPlayerId;
    });
    Player->EnterRoomServer(State.RoomInfo.RoomId, Seat ? Seat->SeatIndex : INDEX_NONE, Seat ? Seat->bReady : false);
    PublishRoomState(State);
    PublishReconnectSnapshot(Controller, State, RemainingSeconds);
}

void AGuiyangMahjongGameMode::HandleCreateRoom(AGuiyangMahjongPlayerController* Controller, const FMahjongCreateRoomRequest& Request)
{
    // 解析授权玩家后才调用房间领域服务，Controller 不直接修改 GameState。
    if (bManagedGameServer)
    {
        if (Controller) Controller->Client_ShowErrorMessage(TEXT("托管房间不能在牌桌服务器内创建"));
        return;
    }
    AGuiyangMahjongPlayerState* Player = nullptr;
    if (!ResolvePlayer(Controller, Player)) return;
    FMahjongRoomState State;
    EMahjongRoomError Error;
    if (!RoomManager->CreateRoom(Player->MahjongPlayerId, Player->DisplayName, Request, State, Error))
    {
        Controller->Client_ShowErrorMessage(ErrorToMessage(Error));
        return;
    }
    Player->EnterRoomServer(State.RoomInfo.RoomId, 0);
    PublishRoomState(State);
    if (IsFullMatchIntegrationEnabled() && Player->MahjongPlayerId == TEXT("integration-client-0"))
    {
        UE_LOG(LogMahjongServer, Display,
            TEXT("MAHJONG_INTEGRATION_FULL_MATCH_ROOM_READY Room=%s Rounds=%d TurnTimeout=%d ReactionTimeout=%d"),
            *State.RoomInfo.RoomId, State.RoomInfo.RoundCount, State.RuleSnapshot.Config.TurnTimeoutSeconds,
            State.RuleSnapshot.Config.ReactionTimeoutSeconds);
    }
}

void AGuiyangMahjongGameMode::HandleQuickStart(AGuiyangMahjongPlayerController* Controller)
{
    if (bManagedGameServer)
    {
        if (Controller) Controller->Client_ShowErrorMessage(TEXT("请从大厅进行快速开始"));
        return;
    }
    AGuiyangMahjongPlayerState* Player = nullptr;
    if (!ResolvePlayer(Controller, Player)) return;
    FMahjongRoomState State;
    EMahjongRoomError Error;
    if (!RoomManager->QuickStart(Player->MahjongPlayerId, Player->DisplayName, State, Error))
    {
        Controller->Client_ShowErrorMessage(ErrorToMessage(Error));
        return;
    }
    const FMahjongSeatInfo* Seat = State.Seats.FindByPredicate([Player](const FMahjongSeatInfo& Item)
    {
        return Item.PlayerId == Player->MahjongPlayerId;
    });
    Player->EnterRoomServer(State.RoomInfo.RoomId, Seat ? Seat->SeatIndex : INDEX_NONE);
    PublishRoomState(State);
}

void AGuiyangMahjongGameMode::HandleJoinRoom(AGuiyangMahjongPlayerController* Controller, const FMahjongJoinRoomRequest& Request)
{
    if (bManagedGameServer)
    {
        if (Controller) Controller->Client_ShowErrorMessage(TEXT("请从大厅选择房间并获取入场票据"));
        return;
    }
    AGuiyangMahjongPlayerState* Player = nullptr;
    if (!ResolvePlayer(Controller, Player)) return;
    FMahjongRoomState State;
    EMahjongRoomError Error;
    if (!RoomManager->JoinRoom(Player->MahjongPlayerId, Player->DisplayName, Request, State, Error))
    {
        Controller->Client_ShowErrorMessage(ErrorToMessage(Error));
        return;
    }
    const FMahjongSeatInfo* Seat = State.Seats.FindByPredicate([Player](const FMahjongSeatInfo& Item) { return Item.PlayerId == Player->MahjongPlayerId; });
    Player->EnterRoomServer(State.RoomInfo.RoomId, Seat ? Seat->SeatIndex : INDEX_NONE);
    PublishRoomState(State);
}

void AGuiyangMahjongGameMode::HandleToggleReady(AGuiyangMahjongPlayerController* Controller)
{
    AGuiyangMahjongPlayerState* Player = nullptr;
    if (!ResolvePlayer(Controller, Player)) return;
    FMahjongRoomState State;
    EMahjongRoomError Error;
    if (!RoomManager->ToggleReady(Player->MahjongPlayerId, State, Error))
    {
        Controller->Client_ShowErrorMessage(ErrorToMessage(Error));
        return;
    }
    if (const FMahjongSeatInfo* Seat = State.Seats.FindByPredicate([Player](const FMahjongSeatInfo& Item) { return Item.PlayerId == Player->MahjongPlayerId; }))
        Player->EnterRoomServer(State.RoomInfo.RoomId, Seat->SeatIndex, Seat->bReady);
    PublishRoomState(State);
    if (State.Lifecycle == EMahjongRoomLifecycle::Starting) TryStartTable(State);
}

void AGuiyangMahjongGameMode::HandleLeaveRoom(AGuiyangMahjongPlayerController* Controller)
{
    AGuiyangMahjongPlayerState* Player = Controller ? Controller->GetPlayerState<AGuiyangMahjongPlayerState>() : nullptr;
    if (!Player || !RoomManager || Player->MahjongPlayerId.IsEmpty()) return;
    FMahjongRoomState State;
    EMahjongRoomError Error;
    if (RoomManager->LeaveRoom(Player->MahjongPlayerId, State, Error))
    {
        Player->LeaveRoomServer();
        PublishRoomState(State);
    }
}

bool AGuiyangMahjongGameMode::ResolvePlayer(AGuiyangMahjongPlayerController* Controller, AGuiyangMahjongPlayerState*& OutPlayerState) const
{
    OutPlayerState = Controller ? Controller->GetPlayerState<AGuiyangMahjongPlayerState>() : nullptr;
    if (!RoomManager || !OutPlayerState || !OutPlayerState->HasValidServerSession())
    {
        if (Controller) Controller->Client_ShowErrorMessage(TEXT("会话无效，请重新登录"));
        return false;
    }
    return true;
}

void AGuiyangMahjongGameMode::PublishRoomState(const FMahjongRoomState& State)
{
    if (AGuiyangMahjongGameState* MahjongState = GetGameState<AGuiyangMahjongGameState>()) MahjongState->SetRoomStateAuthority(State);
}

void AGuiyangMahjongGameMode::TryStartTable(const FMahjongRoomState& StartingRoomState)
{
    // 房间生命周期进入 Starting 后，使用规则快照和服务器种子创建权威牌桌。
    if (!RoomManager || (TableEngine && TableEngine->GetPublicState().Phase != EMahjongTablePhase::Settlement)) return;
    UMahjongTableEngine* RoundEngine = TableEngine ? TableEngine.Get() : NewObject<UMahjongTableEngine>(this);
    FString Error;
    // 只在权威服务端、且紧邻 StartRound 发牌前生成洗牌种子。组合系统
    // GUID、UTC 时间、进程周期、房间号和单调代次，避免快速连续开局或
    // 新分配的 Dedicated Server 重复同一牌序。
    ++ShuffleGeneration;
    uint32 SeedBits = GetTypeHash(FGuid::NewGuid());
    SeedBits = HashCombineFast(SeedBits, GetTypeHash(FDateTime::UtcNow().GetTicks()));
    SeedBits = HashCombineFast(SeedBits, GetTypeHash(FPlatformTime::Cycles64()));
    SeedBits = HashCombineFast(SeedBits, GetTypeHash(StartingRoomState.RoomInfo.RoomId));
    SeedBits = HashCombineFast(SeedBits, ShuffleGeneration);
    int32 Seed = static_cast<int32>(SeedBits);
    if (Seed == LastShuffleSeed)
    {
        Seed = static_cast<int32>(SeedBits + 0x9E3779B9u + ShuffleGeneration);
    }
    LastShuffleSeed = Seed;
    UE_LOG(LogMahjongServer, Display,
        TEXT("Authoritative pre-deal shuffle: room=%s generation=%u seed=%d tiles=108 honors=0"),
        *StartingRoomState.RoomInfo.RoomId, ShuffleGeneration, Seed);
    if (!RoundEngine->StartRound(StartingRoomState.RuleSnapshot, StartingRoomState.Seats,
        StartingRoomState.RoomInfo.DealerSeat, Seed, Error))
    {
        if (!TableEngine) RoundEngine = nullptr;
        return;
    }
    FMahjongRoomState PlayingState;
    EMahjongRoomError RoomError;
    if (!RoomManager->BeginPlaying(StartingRoomState.RoomInfo.RoomId, PlayingState, RoomError))
    {
        return;
    }
    TableEngine = RoundEngine;
    ActiveRoomCode = StartingRoomState.RoomInfo.RoomId;
    LastPublishedSettlementSequence = INDEX_NONE;
    LastFinalizedSettlementSequence = INDEX_NONE;
    LastPublishedFinalRoomSequence = INDEX_NONE;
    ArmedTimeoutRoundId = INDEX_NONE;
    ArmedTimeoutTurnId = INDEX_NONE;
    ArmedTimeoutPhase = EMahjongTablePhase::WaitingForPlayers;
    PublishRoomState(PlayingState);
    PublishTableSnapshots();
    if (FParse::Param(FCommandLine::Get(), TEXT("MahjongEnableIntegrationHooks")))
    {
        UE_LOG(LogMahjongServer, Display,
            TEXT("MAHJONG_INTEGRATION_TABLE_STARTED Room=%s Players=%d Round=%d"),
            *PlayingState.RoomInfo.RoomId, PlayingState.Seats.Num(), TableEngine->GetPublicState().RoundId);
    }
}

void AGuiyangMahjongGameMode::HandleNextRound(AGuiyangMahjongPlayerController* Controller)
{
    AGuiyangMahjongPlayerState* Player = nullptr;
    if (!ResolvePlayer(Controller, Player)) return;
    FMahjongRoomState State;
    EMahjongRoomError Error;
    if (!RoomManager->RequestNextRound(Player->MahjongPlayerId, State, Error))
    {
        Controller->Client_ShowErrorMessage(ErrorToMessage(Error));
        return;
    }
    if (const FMahjongSeatInfo* Seat = State.Seats.FindByPredicate([Player](const FMahjongSeatInfo& Item)
    {
        return Item.PlayerId == Player->MahjongPlayerId;
    }))
    {
        Player->EnterRoomServer(State.RoomInfo.RoomId, Seat->SeatIndex, Seat->bReady);
    }
    PublishRoomState(State);
    if (State.Lifecycle == EMahjongRoomLifecycle::Starting) TryStartTable(State);
}

void AGuiyangMahjongGameMode::HandleTableAction(AGuiyangMahjongPlayerController* Controller, const FMahjongActionRequest& Request)
{
    // 根据当前阶段分派到出牌、回合动作或响应接口，所有合法性由 TableEngine 复核。
    AGuiyangMahjongPlayerState* Player = nullptr;
    if (!ResolvePlayer(Controller, Player) || !TableEngine || Player->SeatIndex == INDEX_NONE)
    {
        if (Controller) Controller->Client_ShowErrorMessage(TEXT("牌桌尚未开始"));
        return;
    }
    FMahjongActionResult Result;
    if (Request.Type == EMahjongActionType::Play)
        Result = TableEngine->SubmitPlayTile(Player->SeatIndex, Request);
    else if (TableEngine->GetPublicState().Phase == EMahjongTablePhase::PlayerTurn)
        Result = TableEngine->SubmitTurnAction(Player->SeatIndex, Request);
    else
        Result = TableEngine->SubmitReaction(Player->SeatIndex, Request);
    if (!Result.bSuccess)
    {
        Controller->Client_ShowErrorMessage(Result.Message);
        return;
    }
    // 玩家成功提交权威动作即解除此前由超时触发的托管；无效请求不得解除托管。
    SetSeatTrusteeState(Player->SeatIndex, false);
    PublishTableSnapshots();
    FinalizeRoundIfNeeded();
}

void AGuiyangMahjongGameMode::HandleLegacyPlayTile(AGuiyangMahjongPlayerController* Controller, const FMahjongTile& Tile, const int32 ClientSequence)
{
    if (!TableEngine) return;
    FMahjongActionRequest Request;
    Request.Type = EMahjongActionType::Play;
    Request.RoundId = TableEngine->GetPublicState().RoundId;
    Request.TurnId = TableEngine->GetPublicState().TurnId;
    Request.TargetTileId = Tile.UniqueId;
    Request.ClientSequence = ClientSequence;
    HandleTableAction(Controller, Request);
}

void AGuiyangMahjongGameMode::PublishTableSnapshots()
{
    // 公共快照复制给 GameState，私有手牌通过所属 Controller 的 Client RPC 定向下发。
    if (!TableEngine) return;
    RefreshActionTimeoutTimer();
    if (AGuiyangMahjongGameState* MahjongState = GetGameState<AGuiyangMahjongGameState>())
        MahjongState->SetPublicTableStateAuthority(TableEngine->GetPublicState());
    FMahjongSettlementResult Settlement;
    const bool bPublishSettlement = TableEngine->GetSettlementResult(Settlement)
        && LastPublishedSettlementSequence != TableEngine->GetPublicState().StateSequence;
    for (TActorIterator<AGuiyangMahjongPlayerController> It(GetWorld()); It; ++It)
    {
        AGuiyangMahjongPlayerController* Controller = *It;
        const AGuiyangMahjongPlayerState* Player = Controller->GetPlayerState<AGuiyangMahjongPlayerState>();
        if (!Player || Player->SeatIndex == INDEX_NONE) continue;
        FMahjongPrivatePlayerState PrivateState;
        if (TableEngine->GetPrivateState(Player->SeatIndex, PrivateState)) Controller->Client_UpdatePrivateHand(PrivateState);
        Controller->Client_ShowAvailableActions(TableEngine->GetAvailableActions(Player->SeatIndex));
        if (bPublishSettlement) Controller->Client_ShowSettlement(Settlement);
    }
    if (bPublishSettlement) LastPublishedSettlementSequence = TableEngine->GetPublicState().StateSequence;
}

void AGuiyangMahjongGameMode::RefreshActionTimeoutTimer()
{
    // 每次状态推进都取消旧计时器，并把局号/回合/阶段写入新的回调令牌。
    if (!TableEngine || !GetWorld()) return;
    const FMahjongPublicTableState& State = TableEngine->GetPublicState();
    const bool bActionPhase = State.Phase == EMahjongTablePhase::PlayerTurn
        || State.Phase == EMahjongTablePhase::WaitingForAction;
    if (!bActionPhase || !TableEngine->GetLockedRuleSnapshot().Config.bEnableTimeoutAutoPlay)
    {
        GetWorldTimerManager().ClearTimer(ActionTimeoutHandle);
        ArmedTimeoutRoundId = INDEX_NONE;
        ArmedTimeoutTurnId = INDEX_NONE;
        ArmedTimeoutPhase = EMahjongTablePhase::WaitingForPlayers;
        TableEngine->SetActionDeadlineForServer(0.0, 0);
        return;
    }
    if (ArmedTimeoutRoundId == State.RoundId && ArmedTimeoutTurnId == State.TurnId
        && ArmedTimeoutPhase == State.Phase && GetWorldTimerManager().IsTimerActive(ActionTimeoutHandle)) return;

    GetWorldTimerManager().ClearTimer(ActionTimeoutHandle);
    ArmedTimeoutRoundId = State.RoundId;
    ArmedTimeoutTurnId = State.TurnId;
    ArmedTimeoutPhase = State.Phase;
    const int32 TimeoutSeconds = State.Phase == EMahjongTablePhase::PlayerTurn
        ? TableEngine->GetLockedRuleSnapshot().Config.TurnTimeoutSeconds
        : TableEngine->GetLockedRuleSnapshot().Config.ReactionTimeoutSeconds;
    // 仅显式完整对局集成模式使用快速计时器；正式游戏仍严格采用规则快照秒数。
    const float TimerDelay = IsFullMatchIntegrationEnabled() ? 0.05f : static_cast<float>(TimeoutSeconds);
    TableEngine->SetActionDeadlineForServer(GetWorld()->GetTimeSeconds() + TimerDelay,
        IsFullMatchIntegrationEnabled() ? 1 : TimeoutSeconds);
    FTimerDelegate Delegate;
    Delegate.BindUObject(this, &ThisClass::HandleActionTimeout, State.RoundId, State.TurnId, State.Phase);
    GetWorldTimerManager().SetTimer(ActionTimeoutHandle, Delegate, TimerDelay, false);
}

void AGuiyangMahjongGameMode::HandleActionTimeout(const int32 ExpectedRoundId, const int32 ExpectedTurnId,
    const EMahjongTablePhase ExpectedPhase)
{
    if (!TableEngine) return;
    TArray<int32> TimedOutSeats;
    if (ExpectedPhase == EMahjongTablePhase::PlayerTurn)
    {
        TimedOutSeats.Add(TableEngine->GetPublicState().CurrentTurnSeat);
    }
    else
    {
        // 响应窗口可能有多名玩家同时可操作，必须把所有未响应座位标记为托管。
        for (int32 SeatIndex = 0; SeatIndex < 4; ++SeatIndex)
        {
            if (!TableEngine->GetAvailableActions(SeatIndex).IsEmpty()) TimedOutSeats.Add(SeatIndex);
        }
    }
    const FMahjongActionResult Result = TableEngine->ResolveActionTimeout(ExpectedRoundId, ExpectedTurnId, ExpectedPhase);
    if (!Result.bSuccess) return;
    // 超时动作由服务器代打，只有超时发生前确实拥有可选动作的座位进入托管。
    for (const int32 TimedOutSeat : TimedOutSeats) SetSeatTrusteeState(TimedOutSeat, true);
    PublishTableSnapshots();
    FinalizeRoundIfNeeded();
}

void AGuiyangMahjongGameMode::SetSeatTrusteeState(const int32 SeatIndex, const bool bTrustee)
{
    const AGuiyangMahjongGameState* MahjongState = GetGameState<AGuiyangMahjongGameState>();
    const FMahjongSeatInfo* Seat = MahjongState
        ? MahjongState->RoomState.Seats.FindByPredicate(
            [SeatIndex](const FMahjongSeatInfo& Item) { return Item.SeatIndex == SeatIndex; })
        : nullptr;
    if (!Seat || Seat->PlayerId.IsEmpty()) return;
    FPlayerTrusteeState& State = TrusteeStateByPlayer.FindOrAdd(Seat->PlayerId);
    if (State.ChangedAtUtc.GetTicks() > 0 && State.bTrustee == bTrustee) return;
    State.bTrustee = bTrustee;
    State.ChangedAtUtc = FDateTime::UtcNow();
}

void AGuiyangMahjongGameMode::FinalizeRoundIfNeeded()
{
    // 结算序号保证同一局只写入房间累计分一次。
    if (!TableEngine || !RoomManager || ActiveRoomCode.IsEmpty()) return;
    const int32 SettlementSequence = TableEngine->GetPublicState().StateSequence;
    if (TableEngine->GetPublicState().Phase != EMahjongTablePhase::Settlement
        || SettlementSequence == LastFinalizedSettlementSequence) return;

    FMahjongSettlementResult Settlement;
    if (!TableEngine->GetSettlementResult(Settlement)) return;
    FMahjongRoomState State;
    EMahjongRoomError Error;
    if (!RoomManager->FinishRound(ActiveRoomCode, Settlement, State, Error)) return;
    LastFinalizedSettlementSequence = SettlementSequence;
    for (TActorIterator<AGuiyangMahjongPlayerController> It(GetWorld()); It; ++It)
    {
        if (AGuiyangMahjongPlayerState* Player = It->GetPlayerState<AGuiyangMahjongPlayerState>())
            Player->EnterRoomServer(State.RoomInfo.RoomId, Player->SeatIndex, false);
    }
    PublishRoomState(State);
    if (State.Lifecycle == EMahjongRoomLifecycle::Settlement
        && State.RoomInfo.CurrentRound >= State.RoomInfo.RoundCount)
        PublishFinalSettlement(State);
}

void AGuiyangMahjongGameMode::PublishReconnectSnapshot(AGuiyangMahjongPlayerController* Controller,
    const FMahjongRoomState& RoomState, const int32 RemainingReconnectSeconds)
{
    // 重连快照一次性组合房间、公共牌桌、所属玩家私有手牌和剩余宽限时间。
    if (!Controller) return;
    const AGuiyangMahjongPlayerState* Player = Controller->GetPlayerState<AGuiyangMahjongPlayerState>();
    if (!Player || Player->SeatIndex == INDEX_NONE) return;

    FMahjongReconnectSnapshot Snapshot;
    Snapshot.RoomState = RoomState;
    Snapshot.RemainingReconnectSeconds = RemainingReconnectSeconds;
    TArray<FMahjongAction> Actions;
    if (TableEngine && ActiveRoomCode == RoomState.RoomInfo.RoomId)
    {
        Snapshot.TableState = TableEngine->GetPublicState();
        TableEngine->GetPrivateState(Player->SeatIndex, Snapshot.PrivateState);
        Actions = TableEngine->GetAvailableActions(Player->SeatIndex);
    }
    Controller->Client_RestoreReconnectSnapshot(Snapshot, Actions);
    if (FParse::Param(FCommandLine::Get(), TEXT("MahjongEnableIntegrationHooks"))
        && Player->MahjongPlayerId.StartsWith(TEXT("integration-client-")))
    {
        int32 OnlineSeats = 0;
        for (const FMahjongSeatInfo& Seat : RoomState.Seats)
        {
            OnlineSeats += Seat.bOccupied && Seat.bOnline ? 1 : 0;
        }
        UE_LOG(LogMahjongReconnect, Display,
            TEXT("MAHJONG_INTEGRATION_RECONNECT_OK Player=%s Seat=%d Online=%d Hand=%d Round=%d Remaining=%d"),
            *Player->MahjongPlayerId, Player->SeatIndex, OnlineSeats, Snapshot.PrivateState.Hand.Tiles.Num(),
            Snapshot.TableState.RoundId, RemainingReconnectSeconds);
    }
    FMahjongSettlementResult Settlement;
    if (TableEngine && TableEngine->GetSettlementResult(Settlement)) Controller->Client_ShowSettlement(Settlement);
    if (RoomState.Lifecycle == EMahjongRoomLifecycle::Settlement
        && RoomState.RoomInfo.CurrentRound >= RoomState.RoomInfo.RoundCount)
        Controller->Client_ShowFinalSettlement(UGuiyangRoomManager::BuildFinalSettlement(RoomState));
}

void AGuiyangMahjongGameMode::PublishFinalSettlement(const FMahjongRoomState& RoomState)
{
    // 最终结果既发送四个客户端，也以可靠 Outbox 方式上报 Lobby 控制面。
    if (RoomState.StateSequence == LastPublishedFinalRoomSequence) return;
    const FMahjongFinalSettlementResult Result = UGuiyangRoomManager::BuildFinalSettlement(RoomState);
    if (bManagedGameServer && GameServerBridge)
        GameServerBridge->QueueFinalSettlement(Result, RoomState.StateSequence);
    for (TActorIterator<AGuiyangMahjongPlayerController> It(GetWorld()); It; ++It)
        It->Client_ShowFinalSettlement(Result);
    if (IsFullMatchIntegrationEnabled())
    {
        UE_LOG(LogMahjongServer, Display,
            TEXT("MAHJONG_INTEGRATION_FULL_MATCH_COMPLETE Room=%s Rounds=%d Players=%d"),
            *Result.RoomId, Result.CompletedRounds, Result.Players.Num());
    }
    LastPublishedFinalRoomSequence = RoomState.StateSequence;
}

FString AGuiyangMahjongGameMode::HashSessionToken(const FString& SessionToken)
{
    FTCHARToUTF8 Utf8(*SessionToken);
    if (Utf8.Length() <= 0) return FString();
    uint8 Digest[FSHA1::DigestSize];
    FSHA1::HashBuffer(Utf8.Get(), Utf8.Length(), Digest);
    return BytesToHex(Digest, UE_ARRAY_COUNT(Digest)).ToLower();
}

FString AGuiyangMahjongGameMode::HashJoinTicket(const FString& JoinTicket)
{
    return HashSessionToken(JoinTicket);
}

bool AGuiyangMahjongGameMode::ConstantTimeDigestEquals(const FString& Left, const FString& Right)
{
    uint32 Difference = static_cast<uint32>(Left.Len() ^ Right.Len());
    const int32 Count = FMath::Max(Left.Len(), Right.Len());
    for (int32 Index = 0; Index < Count; ++Index)
    {
        const TCHAR LeftChar = Left.IsValidIndex(Index) ? Left[Index] : 0;
        const TCHAR RightChar = Right.IsValidIndex(Index) ? Right[Index] : 0;
        Difference |= static_cast<uint32>(LeftChar ^ RightChar);
    }
    return Difference == 0;
}

FString AGuiyangMahjongGameMode::ErrorToMessage(const EMahjongRoomError Error)
{
    switch (Error)
    {
    case EMahjongRoomError::SessionExpired: return TEXT("重连保留时间已结束");
    case EMahjongRoomError::AlreadyInRoom: return TEXT("你已经在房间中");
    case EMahjongRoomError::RoomNotFound: return TEXT("房间不存在");
    case EMahjongRoomError::RoomFull: return TEXT("房间已满");
    case EMahjongRoomError::PasswordRequired: return TEXT("请输入房间密码");
    case EMahjongRoomError::WrongPassword: return TEXT("房间密码错误");
    case EMahjongRoomError::TooManyPasswordAttempts: return TEXT("密码错误次数过多，请稍后再试");
    case EMahjongRoomError::GameAlreadyStarted: return TEXT("牌局已经开始");
    case EMahjongRoomError::NotInRoom: return TEXT("你当前不在房间中");
    default: return TEXT("房间请求无效");
    }
}
