#include "Game/GuiyangMahjongGameMode.h"

#include "Game/GuiyangMahjongGameState.h"
#include "Game/GuiyangMahjongPlayerController.h"
#include "Game/GuiyangMahjongPlayerState.h"
#include "GuiyangMahjong.h"
#include "Engine/GameInstance.h"
#include "Engine/NetConnection.h"
#include "Room/GuiyangManagedRoomDefinition.h"
#include "Room/GuiyangRoomManager.h"
#include "Runtime/Launch/Resources/Version.h"
#include "Server/GuiyangAgonesLifecycleSubsystem.h"
#include "Server/GuiyangFairShuffle.h"
#include "Server/GuiyangGameServerBridge.h"
#include "Snapshot/GuiyangRuntimeRecoveryStore.h"
#include "Evidence/GuiyangActionEvidence.h"
#include "Table/MahjongTableEngine.h"
#include "EngineUtils.h"
#include "Misc/SecureHash.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "HAL/PlatformMisc.h"
#include "GenericPlatform/GenericPlatformHttp.h"
#include "Kismet/GameplayStatics.h"

namespace
{
    constexpr float NextRoundAutoStartDelaySeconds = 10.0f;
    // A player gets a quiet grace period before the clock becomes visible.
    // The authoritative timeout fires only after the complete grace + countdown
    // window, so hiding the first phase on the client cannot shorten a turn.
    constexpr int32 PlayerTurnGraceSeconds = 15;
    constexpr int32 PlayerTurnVisibleCountdownSeconds = 30;

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

    // Mahjong is driven entirely by the authoritative table state and the
    // client-owned fixed room camera. AGameModeBase otherwise spawns an
    // ADefaultPawn for every connected player; its built-in visible sphere
    // mesh was replicated into the middle of the table at different times on
    // different clients. Neither a gameplay pawn nor a spectator pawn belongs
    // in this room.
    DefaultPawnClass = nullptr;
    SpectatorClass = nullptr;
    bStartPlayersAsSpectators = true;
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
    if (bAgonesGameServer)
    {
        if (UGameInstance* GameInstance = GetGameInstance())
        {
            if (UGuiyangAgonesLifecycleSubsystem* Lifecycle =
                GameInstance->GetSubsystem<UGuiyangAgonesLifecycleSubsystem>())
            {
                // Server 专用地图和监听对象已加载，至此才允许 Sidecar 将实例标记为 Ready。
                Lifecycle->StartAfterWorldReady();
            }
        }
    }
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
            PendingTicketClaimsByDigest.Remove(It.Key());
            It.RemoveCurrent();
        }
    }
    const FString TicketDigest = HashJoinTicket(JoinTicket);
    PendingAuthorizedPlayersByTicketDigest.Add(TicketDigest, Claims.PlayerId);
    PendingAuthorizedDisplayNamesByTicketDigest.Add(
        TicketDigest, Claims.DisplayName.TrimStartAndEnd());
    PendingTicketExpiryByDigest.Add(TicketDigest, Claims.ExpiresAtUnixSeconds);
    PendingTicketClaimsByDigest.Add(TicketDigest, Claims);
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
    FGuiyangJoinTicketClaims Claims;
    const bool bHasClaims = PendingTicketClaimsByDigest.RemoveAndCopyValue(TicketDigest, Claims);
    if (!bHasPlayerBinding || !bHasDisplayName || !bHasExpiry || !bHasClaims
        || TicketExpiry <= FDateTime::UtcNow().ToUnixTimestamp()
        || PlayerId.IsEmpty() || DisplayName.IsEmpty() || !NewPlayerController)
    {
        return TEXT("JOIN_TICKET_BINDING_FAILED");
    }
    AuthorizedPlayerIdsByController.Add(NewPlayerController, MoveTemp(PlayerId));
    AuthorizedDisplayNamesByController.Add(NewPlayerController, MoveTemp(DisplayName));
    AuthorizedClaimsByController.Add(NewPlayerController, MoveTemp(Claims));
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

FString AGuiyangMahjongGameMode::GetAuthoritativeStateHash() const
{
    FMahjongTableRecoveryState State;
    return TableEngine && TableEngine->ExportRecoveryState(State)
        ? FGuiyangRuntimeRecoveryStore::CalculateTableStateHash(State)
        : FString();
}

void AGuiyangMahjongGameMode::PostLogin(APlayerController* NewPlayer)
{
    Super::PostLogin(NewPlayer);
    if (const FString* AuthorizedPlayerId = AuthorizedPlayerIdsByController.Find(NewPlayer))
    {
        const FString PlayerId = *AuthorizedPlayerId;
        // 新 Ticket 的 Session/Epoch 获得唯一控制权；旧设备连接先失效，再允许新连接恢复座位。
        for (const TPair<TObjectPtr<APlayerController>, FString>& Entry : AuthorizedPlayerIdsByController)
        {
            APlayerController* Existing = Entry.Key.Get();
            if (!Existing || Existing == NewPlayer || Entry.Value != PlayerId) continue;
            if (UNetConnection* ExistingConnection = Existing->GetNetConnection()) ExistingConnection->Close();
            ReconnectConfirmedControllers.Remove(Existing);
            PendingReconnectTokenDigests.Remove(Existing);
        }
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
        const FGuiyangJoinTicketClaims* Claims = AuthorizedClaimsByController.Find(NewPlayer);
        if (!Claims || !RoomManager->AdmitManagedPlayer(
                ManagedRoomCode, PlayerId, DisplayName, State, Error, Claims->SeatId))
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
        if (TableEngine && ActiveRoomCode == State.RoomInfo.RoomId)
            PublishReconnectSnapshot(MahjongController, State,
                State.RuleSnapshot.Config.ReconnectTimeoutSeconds);
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
    AuthorizedClaimsByController.Remove(Cast<APlayerController>(Exiting));
    PendingReconnectTokenDigests.Remove(Cast<APlayerController>(Exiting));
    ReconnectConfirmedControllers.Remove(Cast<APlayerController>(Exiting));
    Super::Logout(Exiting);
}

void AGuiyangMahjongGameMode::EndPlay(const EEndPlayReason::Type EndPlayReason)
{
    GetWorldTimerManager().ClearTimer(ActionTimeoutHandle);
    GetWorldTimerManager().ClearTimer(NextRoundAutoStartHandle);
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
    PendingTicketClaimsByDigest.Reset();
    AuthorizedPlayerIdsByController.Reset();
    AuthorizedDisplayNamesByController.Reset();
    AuthorizedClaimsByController.Reset();
    PendingReconnectTokenDigests.Reset();
    ReconnectConfirmedControllers.Reset();
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
    // 每个托管进程只承载一场比赛；Bootstrap 成功时建立全新的公平性审计链。
    PendingShuffleProof.Reset();
    CompletedShuffleProofs.Reset();
    FairnessEventChainDigest.Reset();
    if (bManagedGameServer && GameServerBridge)
    {
        RuntimeRecoveryStore = MakeUnique<FGuiyangRuntimeRecoveryStore>();
        if (!RuntimeRecoveryStore->Initialize(GameServerBridge->GetConfig(), OutError))
        {
            RuntimeRecoveryStore.Reset();
            return false;
        }
        if (!TryRecoverPriorEpoch(OutError))
        {
            RuntimeRecoveryStore.Reset();
            return false;
        }
        if (bRecoveredGameServer)
        {
            FMahjongRoomState RecoveredState;
            if (RoomManager->GetRoomState(ManagedRoomCode, RecoveredState))
                State = MoveTemp(RecoveredState);
        }
    }
    PublishRoomState(State);
    if (bRecoveredGameServer && TableEngine)
    {
        // 恢复后重新武装权威计时器；若崩溃点已进入 Settlement，则继续证据屏障和幂等结算流程。
        PublishTableSnapshots();
        FinalizeRoundIfNeeded();
        FString CurrentEpochSnapshotError;
        if (!PersistAuthoritativeSnapshot(false, CurrentEpochSnapshotError))
        {
            OutError = TEXT("恢复成功但新 Epoch 快照落盘失败：") + CurrentEpochSnapshotError;
            return false;
        }
    }
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
        const FGuiyangJoinTicketClaims* Claims = AuthorizedClaimsByController.Find(Controller);
        if (!Claims || !RoomManager->AdmitManagedPlayer(ManagedRoomCode, CleanPlayerId, CleanDisplayName,
            AdmittedState, AdmitError, Claims->SeatId))
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
    if (!Player || !RoomManager || Player->MahjongPlayerId.IsEmpty())
    {
        if (Controller)
        {
            Controller->Client_ShowErrorMessage(TEXT("当前房间状态无效，无法退出"));
        }
        return;
    }
    FMahjongRoomState PreviousState;
    const bool bHadRoomState =
        RoomManager->GetRoomState(Player->RoomCode, PreviousState);
    const bool bWasActiveRound = bHadRoomState
        && (PreviousState.Lifecycle == EMahjongRoomLifecycle::Starting
            || PreviousState.Lifecycle == EMahjongRoomLifecycle::Playing
            || PreviousState.Lifecycle == EMahjongRoomLifecycle::Settlement
            || PreviousState.Lifecycle == EMahjongRoomLifecycle::WaitingNextRound);
    FMahjongRoomState State;
    EMahjongRoomError Error;
    if (RoomManager->LeaveRoom(Player->MahjongPlayerId, State, Error))
    {
        TrusteeStateByPlayer.Remove(Player->MahjongPlayerId);
        Player->LeaveRoomServer();
        if (bWasActiveRound)
        {
            GetWorldTimerManager().ClearTimer(ActionTimeoutHandle);
            GetWorldTimerManager().ClearTimer(NextRoundAutoStartHandle);
            ArmedTimeoutRoundId = INDEX_NONE;
            ArmedTimeoutTurnId = INDEX_NONE;
            ArmedTimeoutPhase = EMahjongTablePhase::WaitingForPlayers;
            TableEngine = nullptr;
            if (AGuiyangMahjongGameState* MahjongState =
                GetGameState<AGuiyangMahjongGameState>())
            {
                MahjongState->SetPublicTableStateAuthority(
                    FMahjongPublicTableState());
            }
            for (TActorIterator<AGuiyangMahjongPlayerController> It(GetWorld());
                 It; ++It)
            {
                if (*It != Controller)
                {
                    It->Client_UpdatePrivateHand(FMahjongPrivatePlayerState());
                    It->Client_ShowAvailableActions(TArray<FMahjongAction>());
                }
            }
            UE_LOG(LogMahjongServer, Display,
                TEXT("Explicit player departure aborted the active round and reset surviving seats: Room=%s Player=%s"),
                *PreviousState.RoomInfo.RoomId, *Player->MahjongPlayerId);
        }
        PublishRoomState(State);
        Controller->Client_ConfirmLeaveRoom();
        return;
    }
    Controller->Client_ShowErrorMessage(ErrorToMessage(Error));
}

void AGuiyangMahjongGameMode::HandleSetTrustee(
    AGuiyangMahjongPlayerController* Controller, const bool bEnabled)
{
    AGuiyangMahjongPlayerState* Player = nullptr;
    if (!ResolvePlayer(Controller, Player) || Player->SeatIndex == INDEX_NONE)
    {
        return;
    }

    const AGuiyangMahjongGameState* State =
        GetGameState<AGuiyangMahjongGameState>();
    const bool bPlaying = State
        && (State->RoomState.Lifecycle == EMahjongRoomLifecycle::Playing
            || State->RoomState.Lifecycle == EMahjongRoomLifecycle::Starting
            || State->RoomState.Lifecycle == EMahjongRoomLifecycle::WaitingNextRound);
    if (!bPlaying)
    {
        Controller->Client_ShowErrorMessage(TEXT("牌局尚未开始，无法托管"));
        Controller->Client_UpdateTrusteeState(false);
        return;
    }

    SetSeatTrusteeState(Player->SeatIndex, bEnabled);
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
    // 房间生命周期进入 Starting 后，使用冻结规则和 CSPRNG 种子创建权威牌桌。
    if (!RoomManager || (TableEngine && TableEngine->GetPublicState().Phase != EMahjongTablePhase::Settlement)) return;
    GetWorldTimerManager().ClearTimer(NextRoundAutoStartHandle);
    UMahjongTableEngine* RoundEngine = TableEngine ? TableEngine.Get() : NewObject<UMahjongTableEngine>(this);
    FString Error;
    const int32 RoundId = CompletedShuffleProofs.Num() + 1;
    int32 Seed = 0;
    FGuiyangShuffleAuditProof Proof;
    if (!FGuiyangFairShuffle::Generate(
        StartingRoomState.RoomInfo.RoomId, RoundId,
        StartingRoomState.RuleSnapshot, Seed, Proof, Error))
    {
        UE_LOG(LogMahjongServer, Error,
            TEXT("Secure shuffle material generation failed Room=%s Round=%d Reason=%s"),
            *StartingRoomState.RoomInfo.RoomId, RoundId, *Error);
        return;
    }
    // 托管服必须在发牌前可靠落盘承诺；落盘失败时拒绝开局，不能形成“先看牌后选承诺”的空间。
    if (bManagedGameServer
        && (!GameServerBridge || !GameServerBridge->AppendShuffleAuditRecord(
            Proof, false, FString())))
    {
        UE_LOG(LogMahjongServer, Error,
            TEXT("Pre-deal shuffle commitment persistence failed Room=%s Round=%d"),
            *StartingRoomState.RoomInfo.RoomId, RoundId);
        return;
    }
    UE_LOG(LogMahjongServer, Display,
        TEXT("Authoritative pre-deal shuffle committed Room=%s Round=%d Commitment=%s"),
        *StartingRoomState.RoomInfo.RoomId, RoundId, *Proof.SeedCommitment);
    if (!RoundEngine->StartRound(StartingRoomState.RuleSnapshot, StartingRoomState.Seats,
        StartingRoomState.RoomInfo.DealerSeat, Seed, Error))
    {
        if (!TableEngine) RoundEngine = nullptr;
        return;
    }
    const TArray<FMahjongTile>* Deck = RoundEngine->GetDeckOrderForServerAudit();
    if (!Deck || Deck->IsEmpty())
    {
        UE_LOG(LogMahjongServer, Error,
            TEXT("Post-shuffle deck audit snapshot unavailable Room=%s Round=%d"),
            *StartingRoomState.RoomInfo.RoomId, RoundId);
        return;
    }
    Proof.DeckOrderDigest = FGuiyangFairShuffle::CalculateDeckOrderDigest(*Deck);
    PendingShuffleProof = MoveTemp(Proof);
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
    FString SnapshotError;
    if (RuntimeRecoveryStore && !PersistAuthoritativeSnapshot(false, SnapshotError))
    {
        UE_LOG(LogMahjongServer, Error,
            TEXT("Post-deal authoritative snapshot failed Room=%s Reason=%s"),
            *ActiveRoomCode, *SnapshotError);
    }
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
    if (State.Lifecycle == EMahjongRoomLifecycle::Starting)
    {
        GetWorldTimerManager().ClearTimer(NextRoundAutoStartHandle);
        TryStartTable(State);
    }
}

bool AGuiyangMahjongGameMode::ValidateAuthoritativeActionEnvelope(
    const AGuiyangMahjongPlayerState& Player,
    const FMahjongActionRequest& Request,
    FString& OutError)
{
    // 动作 ID 必须是规范 UUID；服务端不接受空 ID，也不把 ID 当作玩家身份依据。
    FGuid ParsedActionId;
    if (!FGuid::Parse(Request.ClientActionId, ParsedActionId))
    {
        OutError = TEXT("操作标识格式无效");
        return false;
    }
    const int64 ExpectedEpoch = GameServerBridge ? GameServerBridge->GetConfig().RoomEpoch : 0;
    if (Request.RoomEpoch != ExpectedEpoch)
    {
        OutError = TEXT("房间实例已经切换，请重新连接");
        return false;
    }
    if (!TableEngine || Request.ExpectedStateVersion != TableEngine->GetPublicState().StateSequence)
    {
        OutError = TEXT("牌桌状态已经变化，请同步后重试");
        return false;
    }
    const FString ReplayKey = Player.MahjongPlayerId + TEXT("|") + Request.ClientActionId;
    if (AcceptedActionIds.Contains(ReplayKey))
    {
        OutError = TEXT("该操作已经处理");
        return false;
    }
    const int64 NowMilliseconds = FDateTime::UtcNow().ToUnixTimestamp() * 1000;
    if (Request.ClientSentAtUnixMilliseconds <= 0
        || FMath::Abs(NowMilliseconds - Request.ClientSentAtUnixMilliseconds) > 120000)
    {
        OutError = TEXT("操作时间窗口无效，请校准时间后重连");
        return false;
    }

    // 每玩家每秒最多十个牌桌意图；限流只保护入口，规则引擎仍会独立校验回合和牌所有权。
    const double NowSeconds = FPlatformTime::Seconds();
    TArray<double>& Recent = RecentActionTimesByPlayer.FindOrAdd(Player.MahjongPlayerId);
    Recent.RemoveAll([NowSeconds](const double Value) { return NowSeconds - Value >= 1.0; });
    if (Recent.Num() >= 10)
    {
        OutError = TEXT("操作过于频繁，请稍后重试");
        return false;
    }
    Recent.Add(NowSeconds);

    // 重放缓存保留十分钟即可覆盖连接重试；按 UTC 秒清理不会影响规则确定性。
    const int64 ExpireBefore = FDateTime::UtcNow().ToUnixTimestamp() - 600;
    for (auto It = AcceptedActionIds.CreateIterator(); It; ++It)
    {
        if (It.Value() < ExpireBefore) It.RemoveCurrent();
    }
    return true;
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
    if (bRecoveredGameServer && !ReconnectConfirmedControllers.Contains(Controller))
    {
        Controller->Client_ShowErrorMessage(TEXT("恢复状态尚未确认，请等待同步完成"));
        return;
    }
    FString EnvelopeError;
    if (!ValidateAuthoritativeActionEnvelope(*Player, Request, EnvelopeError))
    {
        Controller->Client_ShowErrorMessage(EnvelopeError);
        return;
    }
    const int32 StateVersionBefore = TableEngine->GetPublicState().StateSequence;
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
    AcceptedActionIds.Add(
        Player->MahjongPlayerId + TEXT("|") + Request.ClientActionId,
        FDateTime::UtcNow().ToUnixTimestamp());
    RecordAcceptedActionEvidence(*Player, Request, Result, StateVersionBefore);
    // 玩家成功提交权威动作即解除此前由超时触发的托管；无效请求不得解除托管。
    SetSeatTrusteeState(Player->SeatIndex, false);
    PublishTableSnapshots();
    FinalizeRoundIfNeeded();
}

void AGuiyangMahjongGameMode::HandleLegacyPlayTile(AGuiyangMahjongPlayerController* Controller, const FMahjongTile& Tile, const int32 ClientSequence)
{
    if (bManagedGameServer)
    {
        if (Controller) Controller->Client_ShowErrorMessage(TEXT("当前服务器要求新版权威动作协议"));
        return;
    }
    if (!TableEngine) return;
    FMahjongActionRequest Request;
    Request.Type = EMahjongActionType::Play;
    Request.ClientActionId = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
    Request.RoundId = TableEngine->GetPublicState().RoundId;
    Request.TurnId = TableEngine->GetPublicState().TurnId;
    Request.TargetTileId = Tile.UniqueId;
    Request.ClientSequence = ClientSequence;
    Request.ExpectedStateVersion = TableEngine->GetPublicState().StateSequence;
    Request.RoomEpoch = GameServerBridge ? GameServerBridge->GetConfig().RoomEpoch : 0;
    Request.ClientSentAtUnixMilliseconds = FDateTime::UtcNow().ToUnixTimestamp() * 1000;
    HandleTableAction(Controller, Request);
}

void AGuiyangMahjongGameMode::PublishTableSnapshots()
{
    // 公共快照复制给 GameState，私有手牌通过所属 Controller 的 Client RPC 定向下发。
    if (!TableEngine) return;
    RefreshActionTimeoutTimer();
    if (AGuiyangMahjongGameState* MahjongState = GetGameState<AGuiyangMahjongGameState>())
    {
        FMahjongPublicTableState PublicState = TableEngine->GetPublicState();
        PublicState.RoomEpoch = GameServerBridge ? GameServerBridge->GetConfig().RoomEpoch : 0;
        FMahjongTableRecoveryState RecoveryState;
        if (TableEngine->ExportRecoveryState(RecoveryState))
            PublicState.PublicStateHash = FGuiyangRuntimeRecoveryStore::CalculateTableStateHash(RecoveryState);
        MahjongState->SetPublicTableStateAuthority(PublicState);
    }
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
    const bool bPlayerTurn = State.Phase == EMahjongTablePhase::PlayerTurn;
    const int32 VisibleTimeoutSeconds = bPlayerTurn
        ? PlayerTurnVisibleCountdownSeconds
        : TableEngine->GetLockedRuleSnapshot().Config.ReactionTimeoutSeconds;
    const int32 TotalTimeoutSeconds = bPlayerTurn
        ? PlayerTurnGraceSeconds + PlayerTurnVisibleCountdownSeconds
        : VisibleTimeoutSeconds;
    // Only the explicit full-match integration mode uses a fast timer.
    // Production turns use a hidden 15-second grace period followed by the
    // replicated 30-second visible countdown.
    float TimerDelay = IsFullMatchIntegrationEnabled()
        ? 0.05f
        : static_cast<float>(TotalTimeoutSeconds);
    if (RecoveredActionTimeoutRemainingSeconds.IsSet())
    {
        TimerDelay = FMath::Clamp(
            RecoveredActionTimeoutRemainingSeconds.GetValue(), 0.05f, TimerDelay);
        RecoveredActionTimeoutRemainingSeconds.Reset();
    }
    TableEngine->SetActionDeadlineForServer(GetWorld()->GetTimeSeconds() + TimerDelay,
        IsFullMatchIntegrationEnabled() ? 1 : VisibleTimeoutSeconds);
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
    const int32 StateVersionBefore = TableEngine->GetPublicState().StateSequence;
    const FMahjongActionResult Result = TableEngine->ResolveActionTimeout(ExpectedRoundId, ExpectedTurnId, ExpectedPhase);
    if (!Result.bSuccess) return;
    // 超时动作由服务器代打，只有超时发生前确实拥有可选动作的座位进入托管。
    for (const int32 TimedOutSeat : TimedOutSeats) SetSeatTrusteeState(TimedOutSeat, true);
    if (RuntimeRecoveryStore && GameServerBridge)
    {
        FMahjongTableRecoveryState RecoveryState;
        const FString StateHash = TableEngine->ExportRecoveryState(RecoveryState)
            ? FGuiyangRuntimeRecoveryStore::CalculateTableStateHash(RecoveryState)
            : FString();
        for (const int32 TimedOutSeat : TimedOutSeats)
        {
            FGuiyangActionEvidenceRecord Record;
            Record.MatchId = GameServerBridge->GetConfig().MatchId;
            Record.RoomId = GameServerBridge->GetConfig().RoomId;
            Record.RoomEpoch = GameServerBridge->GetConfig().RoomEpoch;
            Record.StateVersionBefore = StateVersionBefore;
            Record.StateVersionAfter = TableEngine->GetPublicState().StateSequence;
            Record.StateHashAfter = StateHash;
            const FMahjongSeatInfo* TimedOutPlayer = TableEngine->GetPublicState().Seats.FindByPredicate(
                [TimedOutSeat](const FMahjongSeatInfo& Seat) { return Seat.SeatIndex == TimedOutSeat; });
            Record.PlayerId = TimedOutPlayer ? TimedOutPlayer->PlayerId : FString::Printf(TEXT("seat:%d"), TimedOutSeat);
            Record.SeatId = TimedOutSeat;
            Record.ActionType = ExpectedPhase == EMahjongTablePhase::PlayerTurn
                ? TEXT("TimeoutAutoPlay") : TEXT("TimeoutAutoPass");
            Record.OccurredAtUtc = FDateTime::UtcNow().ToIso8601();
            Record.Request.ClientActionId = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
            Record.Request.RoundId = ExpectedRoundId;
            Record.Request.TurnId = ExpectedTurnId;
            Record.Request.RoomEpoch = GameServerBridge->GetConfig().RoomEpoch;
            Record.Request.Type = Result.Action.Type;
            Record.NormalizedPayload = FGuiyangActionEvidence::NormalizeRequest(Record.Request);
            Record.bReplayable = false;
            FString EvidenceError;
            if (!RuntimeRecoveryStore->AppendAction(Record, EvidenceError))
                UE_LOG(LogMahjongServer, Error, TEXT("Timeout evidence append failed Reason=%s"), *EvidenceError);
        }
    }
    // 超时可能一次推进多个自动 Pass；立即写完整快照，避免把聚合自动动作误表示成单个玩家意图。
    FString SnapshotError;
    if (RuntimeRecoveryStore && !PersistAuthoritativeSnapshot(false, SnapshotError))
        UE_LOG(LogMahjongServer, Error, TEXT("Trustee snapshot failed Reason=%s"), *SnapshotError);
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

    for (TActorIterator<AGuiyangMahjongPlayerController> It(GetWorld()); It; ++It)
    {
        const AGuiyangMahjongPlayerState* ControllerPlayer =
            It->GetPlayerState<AGuiyangMahjongPlayerState>();
        if (ControllerPlayer && ControllerPlayer->MahjongPlayerId == Seat->PlayerId)
        {
            It->Client_UpdateTrusteeState(bTrustee);
            break;
        }
    }
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
    FString EvidenceError;
    if (RuntimeRecoveryStore && !PersistAuthoritativeSnapshot(true, EvidenceError))
    {
        // 结算前证据是强屏障：失败时保持 Settlement 内存状态，不向 Lobby 上报正常结果。
        bSettlementEvidenceReady = false;
        UE_LOG(LogMahjongServer, Error,
            TEXT("Settlement evidence barrier failed Room=%s Reason=%s"),
            *ActiveRoomCode, *EvidenceError);
        return;
    }
    bSettlementEvidenceReady = true;
    if (PendingShuffleProof.IsSet())
    {
        const TArray<FMahjongTile>* Deck = TableEngine->GetDeckOrderForServerAudit();
        FGuiyangShuffleAuditProof Proof = PendingShuffleProof.GetValue();
        Proof.RevealedAtUtc = FDateTime::UtcNow();
        if (!Deck || !FGuiyangFairShuffle::Verify(
            ActiveRoomCode, TableEngine->GetLockedRuleSnapshot(), *Deck, Proof))
        {
            UE_LOG(LogMahjongServer, Error,
                TEXT("Shuffle fairness proof verification failed Room=%s Round=%d"),
                *ActiveRoomCode, Proof.RoundId);
            return;
        }
        const FString NextEventChainDigest = FGuiyangFairShuffle::CalculateEventChainDigest(
            FairnessEventChainDigest, ActiveRoomCode, Proof);
        // Reveal 只能在牌桌进入 Settlement 后写入；失败时保留 Pending，等待下一次安全重试。
        if (bManagedGameServer
            && (!GameServerBridge || !GameServerBridge->AppendShuffleAuditRecord(
                Proof, true, NextEventChainDigest)))
        {
            UE_LOG(LogMahjongServer, Error,
                TEXT("Post-round shuffle proof persistence failed Room=%s Round=%d"),
                *ActiveRoomCode, Proof.RoundId);
            return;
        }
        CompletedShuffleProofs.Add(MoveTemp(Proof));
        FairnessEventChainDigest = NextEventChainDigest;
        PendingShuffleProof.Reset();
    }
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
    if (State.Lifecycle == EMahjongRoomLifecycle::WaitingNextRound)
    {
        ArmNextRoundAutoStart(State);
    }
    if (State.Lifecycle == EMahjongRoomLifecycle::Settlement
        && State.RoomInfo.CurrentRound >= State.RoomInfo.RoundCount)
        PublishFinalSettlement(State);
}

void AGuiyangMahjongGameMode::ArmNextRoundAutoStart(const FMahjongRoomState& WaitingRoomState)
{
    if (WaitingRoomState.Lifecycle != EMahjongRoomLifecycle::WaitingNextRound
        || WaitingRoomState.RoomInfo.RoomId.IsEmpty())
    {
        return;
    }

    FTimerDelegate Delegate;
    Delegate.BindUObject(this, &ThisClass::HandleNextRoundAutoStart,
        WaitingRoomState.RoomInfo.RoomId);
    GetWorldTimerManager().SetTimer(NextRoundAutoStartHandle, Delegate,
        NextRoundAutoStartDelaySeconds, false);
    UE_LOG(LogMahjongServer, Display,
        TEXT("Round settlement auto-advance armed: room=%s delay=%.1fs"),
        *WaitingRoomState.RoomInfo.RoomId, NextRoundAutoStartDelaySeconds);
}

void AGuiyangMahjongGameMode::HandleNextRoundAutoStart(FString ExpectedRoomCode)
{
    if (!RoomManager || ExpectedRoomCode.IsEmpty())
    {
        return;
    }

    FMahjongRoomState State;
    if (!RoomManager->GetRoomState(ExpectedRoomCode, State)
        || State.Lifecycle != EMahjongRoomLifecycle::WaitingNextRound)
    {
        return;
    }

    TArray<FString> PendingPlayerIds;
    for (const FMahjongSeatInfo& Seat : State.Seats)
    {
        if (Seat.bOccupied && !Seat.bReady && !Seat.PlayerId.IsEmpty())
        {
            PendingPlayerIds.Add(Seat.PlayerId);
        }
    }

    int32 AutoAcknowledgedPlayers = 0;
    for (const FString& PlayerId : PendingPlayerIds)
    {
        EMahjongRoomError Error = EMahjongRoomError::None;
        FMahjongRoomState UpdatedState;
        if (RoomManager->RequestNextRound(PlayerId, UpdatedState, Error))
        {
            State = MoveTemp(UpdatedState);
            ++AutoAcknowledgedPlayers;
        }
    }

    for (TActorIterator<AGuiyangMahjongPlayerController> It(GetWorld()); It; ++It)
    {
        AGuiyangMahjongPlayerState* Player =
            It->GetPlayerState<AGuiyangMahjongPlayerState>();
        if (!Player)
        {
            continue;
        }
        if (const FMahjongSeatInfo* Seat = State.Seats.FindByPredicate(
            [Player](const FMahjongSeatInfo& Item)
            {
                return Item.PlayerId == Player->MahjongPlayerId;
            }))
        {
            Player->EnterRoomServer(State.RoomInfo.RoomId, Seat->SeatIndex,
                Seat->bReady);
        }
    }

    UE_LOG(LogMahjongServer, Display,
        TEXT("Round settlement auto-advance fired: room=%s acknowledged=%d lifecycle=%d"),
        *ExpectedRoomCode, AutoAcknowledgedPlayers,
        static_cast<int32>(State.Lifecycle));
    PublishRoomState(State);
    if (State.Lifecycle == EMahjongRoomLifecycle::Starting)
    {
        TryStartTable(State);
    }
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
        Snapshot.TableState.RoomEpoch = GameServerBridge ? GameServerBridge->GetConfig().RoomEpoch : 0;
        FMahjongTableRecoveryState RecoveryState;
        if (TableEngine->ExportRecoveryState(RecoveryState))
            Snapshot.TableState.PublicStateHash =
                FGuiyangRuntimeRecoveryStore::CalculateTableStateHash(RecoveryState);
        TableEngine->GetPrivateState(Player->SeatIndex, Snapshot.PrivateState);
        Actions = TableEngine->GetAvailableActions(Player->SeatIndex);
    }
    Snapshot.ControlToken = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
    Snapshot.MissingActionCount = 0;
    PendingReconnectTokenDigests.Add(Controller, HashSessionToken(Snapshot.ControlToken));
    ReconnectConfirmedControllers.Remove(Controller);
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

void AGuiyangMahjongGameMode::HandleReconnectStateConfirmed(
    AGuiyangMahjongPlayerController* Controller,
    const FString& ControlToken,
    const int32 StateVersion,
    const FString& PublicStateHash)
{
    const FString* ExpectedDigest = PendingReconnectTokenDigests.Find(Controller);
    FMahjongTableRecoveryState RecoveryState;
    const FString CurrentHash = TableEngine && TableEngine->ExportRecoveryState(RecoveryState)
        ? FGuiyangRuntimeRecoveryStore::CalculateTableStateHash(RecoveryState)
        : FString();
    if (!Controller || !ExpectedDigest
        || !ConstantTimeDigestEquals(*ExpectedDigest, HashSessionToken(ControlToken))
        || !TableEngine || StateVersion != TableEngine->GetPublicState().StateSequence
        || PublicStateHash != CurrentHash)
    {
        if (Controller) Controller->Client_ShowErrorMessage(TEXT("重连状态确认失败，请重新连接"));
        return;
    }
    PendingReconnectTokenDigests.Remove(Controller);
    ReconnectConfirmedControllers.Add(Controller);
}

void AGuiyangMahjongGameMode::PublishFinalSettlement(const FMahjongRoomState& RoomState)
{
    // 最终结果既发送四个客户端，也以可靠 Outbox 方式上报 GameData；客户端永远不能提交该信封。
    if (RoomState.StateSequence == LastPublishedFinalRoomSequence) return;
    const FMahjongFinalSettlementResult Result = UGuiyangRoomManager::BuildFinalSettlement(RoomState);
    if (bManagedGameServer && GameServerBridge)
    {
        FString EvidenceError;
        if (!PersistAuthoritativeSnapshot(true, EvidenceError))
        {
            bSettlementEvidenceReady = false;
            UE_LOG(LogMahjongServer, Error,
                TEXT("Final GameData snapshot barrier failed MatchId=%s Reason=%s"),
                *Result.MatchId, *EvidenceError);
            return;
        }
        FMahjongTableRecoveryState FinalState;
        const FString FinalStateHash = TableEngine && TableEngine->ExportRecoveryState(FinalState)
            ? FGuiyangRuntimeRecoveryStore::CalculateTableStateHash(FinalState)
            : FString();
        TArray<FGuiyangRecoveryEvidenceObject> EvidenceObjects;
        if (!RuntimeRecoveryStore
            || !RuntimeRecoveryStore->MaterializeFinalEvidence(EvidenceObjects, EvidenceError))
        {
            bSettlementEvidenceReady = false;
            UE_LOG(LogMahjongServer, Error,
                TEXT("Final GameData evidence materialization failed MatchId=%s Reason=%s"),
                *Result.MatchId, *EvidenceError);
            return;
        }
        FString CommitmentCanonical = TEXT("shuffle-commitments-v1");
        for (const FGuiyangShuffleAuditProof& Proof : CompletedShuffleProofs)
            CommitmentCanonical += FString::Printf(TEXT("|%d:%s"), Proof.RoundId, *Proof.SeedCommitment);
        const FTCHARToUTF8 CommitmentUtf8(*CommitmentCanonical);
        FSHA256Signature CommitmentSignature;
        const FString RandomCommitment = FPlatformMisc::GetSHA256Signature(
            CommitmentUtf8.Get(), static_cast<uint32>(CommitmentUtf8.Length()), CommitmentSignature)
            ? CommitmentSignature.ToString().ToLower()
            : FString();
        GameServerBridge->QueueFinalSettlement(
            Result,
            1,
            CompletedShuffleProofs,
            FairnessEventChainDigest,
            FinalStateHash,
            RuntimeRecoveryStore->GetLastActionHash(),
            RandomCommitment,
            FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower),
            EvidenceObjects);
    }
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

void AGuiyangMahjongGameMode::RecordAcceptedActionEvidence(
    const AGuiyangMahjongPlayerState& Player,
    const FMahjongActionRequest& Request,
    const FMahjongActionResult& Result,
    const int32 StateVersionBefore)
{
    if (!RuntimeRecoveryStore || !TableEngine || !GameServerBridge) return;
    FMahjongTableRecoveryState RecoveryState;
    if (!TableEngine->ExportRecoveryState(RecoveryState))
    {
        UE_LOG(LogMahjongServer, Error, TEXT("Accepted action state export failed Player=%s"), *Player.MahjongPlayerId);
        return;
    }
    FGuiyangActionEvidenceRecord Record;
    Record.MatchId = GameServerBridge->GetConfig().MatchId;
    Record.RoomId = GameServerBridge->GetConfig().RoomId;
    Record.RoomEpoch = GameServerBridge->GetConfig().RoomEpoch;
    Record.StateVersionBefore = StateVersionBefore;
    Record.StateVersionAfter = TableEngine->GetPublicState().StateSequence;
    Record.StateHashAfter = FGuiyangRuntimeRecoveryStore::CalculateTableStateHash(RecoveryState);
    Record.PlayerId = Player.MahjongPlayerId;
    Record.SeatId = Player.SeatIndex;
    Record.ActionType = FString::FromInt(static_cast<int32>(Result.Action.Type));
    Record.NormalizedPayload = FGuiyangActionEvidence::NormalizeRequest(Request);
    Record.OccurredAtUtc = FDateTime::UtcNow().ToIso8601();
    Record.Request = Request;
    FString Error;
    if (!RuntimeRecoveryStore->AppendAction(Record, Error))
    {
        UE_LOG(LogMahjongServer, Error,
            TEXT("Authoritative action evidence append failed Player=%s Reason=%s"),
            *Player.MahjongPlayerId, *Error);
        return;
    }

    const bool bCriticalAction = Request.Type == EMahjongActionType::AnGang
        || Request.Type == EMahjongActionType::MingGang
        || Request.Type == EMahjongActionType::BuGang
        || Request.Type == EMahjongActionType::Hu
        || TableEngine->GetPublicState().Phase == EMahjongTablePhase::Settlement;
    const bool bActionThreshold = RuntimeRecoveryStore->GetLastActionSequence()
        - LastSnapshotActionSequence >= RuntimeRecoveryStore->GetSnapshotEveryActions();
    const bool bTimeThreshold = LastAuthoritativeSnapshotAtUtc.GetTicks() <= 0
        || FDateTime::UtcNow() - LastAuthoritativeSnapshotAtUtc
            >= FTimespan::FromSeconds(RuntimeRecoveryStore->GetSnapshotMaxIntervalSeconds());
    if (bCriticalAction || bActionThreshold || bTimeThreshold)
    {
        if (!PersistAuthoritativeSnapshot(false, Error))
            UE_LOG(LogMahjongServer, Error, TEXT("Periodic authoritative snapshot failed Reason=%s"), *Error);
    }
}

bool AGuiyangMahjongGameMode::PersistAuthoritativeSnapshot(
    const bool bSettlementBarrier,
    FString& OutError)
{
    if (!RuntimeRecoveryStore || !TableEngine || !RoomManager || !GameServerBridge)
    {
        OutError = TEXT("权威快照依赖尚未初始化");
        return false;
    }
    FGuiyangAuthoritativeSnapshot Snapshot;
    Snapshot.MatchId = GameServerBridge->GetConfig().MatchId;
    Snapshot.RoomId = GameServerBridge->GetConfig().RoomId;
    Snapshot.RoomCode = ActiveRoomCode.IsEmpty() ? ManagedRoomCode : ActiveRoomCode;
    Snapshot.RoomEpoch = GameServerBridge->GetConfig().RoomEpoch;
    Snapshot.RuleSetVersion = GameServerBridge->GetConfig().RuleSetVersion;
    Snapshot.CreatedAtUtc = FDateTime::UtcNow().ToIso8601();
    Snapshot.RemainingActionTimeoutSeconds = GetWorld()
        ? FMath::Max(0.0f, GetWorldTimerManager().GetTimerRemaining(ActionTimeoutHandle))
        : 0.0f;
    if (!RoomManager->GetRoomState(Snapshot.RoomCode, Snapshot.RoomState)
        || !TableEngine->ExportRecoveryState(Snapshot.TableState))
    {
        OutError = TEXT("无法导出完整房间或牌桌状态");
        return false;
    }
    Snapshot.StateVersion = Snapshot.TableState.PublicState.StateSequence;
    Snapshot.RandomState = FString::Printf(
        TEXT("seed=%s;nonce=%s;deck-offset=%d;commitment=%s"),
        PendingShuffleProof.IsSet() ? *PendingShuffleProof.GetValue().SeedHex : TEXT("revealed"),
        PendingShuffleProof.IsSet() ? *PendingShuffleProof.GetValue().ServerNonceHex : TEXT("revealed"),
        Snapshot.TableState.DeckState.ClockwiseDrawOffset,
        PendingShuffleProof.IsSet()
            ? *PendingShuffleProof.GetValue().SeedCommitment
            : TEXT("revealed"));
    Snapshot.bHasPendingShuffleProof = PendingShuffleProof.IsSet();
    if (PendingShuffleProof.IsSet()) Snapshot.PendingShuffleProof = PendingShuffleProof.GetValue();
    Snapshot.CompletedShuffleProofs = CompletedShuffleProofs;
    Snapshot.FairnessEventChainDigest = FairnessEventChainDigest;
    for (const FMahjongSeatInfo& Seat : Snapshot.RoomState.Seats)
    {
        const FPlayerTrusteeState* Trustee = TrusteeStateByPlayer.Find(Seat.PlayerId);
        if (Seat.bOccupied && Trustee && Trustee->bTrustee) Snapshot.TrusteeSeats.Add(Seat.SeatIndex);
    }
    if (!RuntimeRecoveryStore->SaveSnapshot(Snapshot, OutError))
    {
        if (bSettlementBarrier) bSettlementEvidenceReady = false;
        return false;
    }
    LastAuthoritativeSnapshotAtUtc = FDateTime::UtcNow();
    LastSnapshotActionSequence = RuntimeRecoveryStore->GetLastActionSequence();
    return true;
}

bool AGuiyangMahjongGameMode::TryRecoverPriorEpoch(FString& OutError)
{
    bRecoveredGameServer = false;
    if (!RuntimeRecoveryStore || !RoomManager || !GameServerBridge) return true;
    FGuiyangAuthoritativeSnapshot Snapshot;
    TArray<FGuiyangActionEvidenceRecord> Actions;
    if (!RuntimeRecoveryStore->LoadLatestPriorEpoch(Snapshot, Actions, OutError))
    {
        // 空错误表示该比赛从未产生快照，是全新分配而不是恢复故障。
        return OutError.IsEmpty();
    }
    if (Snapshot.RuleSetVersion != GameServerBridge->GetConfig().RuleSetVersion)
    {
        OutError = TEXT("恢复快照规则版本与新实例不兼容");
        return false;
    }
    UMahjongTableEngine* RecoveredEngine = NewObject<UMahjongTableEngine>(this);
    if (!RecoveredEngine || !RecoveredEngine->RestoreRecoveryState(Snapshot.TableState, OutError)
        || !RoomManager->RestoreManagedRoomState(Snapshot.RoomCode, Snapshot.RoomState, OutError))
    {
        return false;
    }
    FString LastHash = Snapshot.PreviousActionHash;
    int64 LastSequence = Snapshot.ActionSequence;
    for (const FGuiyangActionEvidenceRecord& Action : Actions)
    {
        if (!Action.bReplayable)
        {
            OutError = TEXT("恢复点之后包含未被快照覆盖的聚合超时动作");
            return false;
        }
        if (Action.ActionSequence != LastSequence + 1
            || Action.PreviousHash != LastHash
            || Action.StateVersionBefore != RecoveredEngine->GetPublicState().StateSequence)
        {
            OutError = TEXT("恢复动作序号、哈希链或状态版本不连续");
            return false;
        }
        FMahjongActionRequest ReplayRequest = Action.Request;
        ReplayRequest.ExpectedStateVersion = RecoveredEngine->GetPublicState().StateSequence;
        FMahjongActionResult Result;
        if (ReplayRequest.Type == EMahjongActionType::Play)
            Result = RecoveredEngine->SubmitPlayTile(Action.SeatId, ReplayRequest);
        else if (RecoveredEngine->GetPublicState().Phase == EMahjongTablePhase::PlayerTurn)
            Result = RecoveredEngine->SubmitTurnAction(Action.SeatId, ReplayRequest);
        else
            Result = RecoveredEngine->SubmitReaction(Action.SeatId, ReplayRequest);
        FMahjongTableRecoveryState ReplayedState;
        if (!Result.bSuccess || !RecoveredEngine->ExportRecoveryState(ReplayedState)
            || RecoveredEngine->GetPublicState().StateSequence != Action.StateVersionAfter
            || FGuiyangRuntimeRecoveryStore::CalculateTableStateHash(ReplayedState) != Action.StateHashAfter)
        {
            OutError = TEXT("确定性动作重放结果与证据哈希不一致");
            return false;
        }
        LastSequence = Action.ActionSequence;
        LastHash = Action.ActionHash;
    }
    TableEngine = RecoveredEngine;
    ActiveRoomCode = Snapshot.RoomCode;
    PendingShuffleProof = Snapshot.bHasPendingShuffleProof
        ? TOptional<FGuiyangShuffleAuditProof>(Snapshot.PendingShuffleProof)
        : TOptional<FGuiyangShuffleAuditProof>();
    CompletedShuffleProofs = Snapshot.CompletedShuffleProofs;
    FairnessEventChainDigest = Snapshot.FairnessEventChainDigest;
    RecoveredActionTimeoutRemainingSeconds = Snapshot.RemainingActionTimeoutSeconds;
    RuntimeRecoveryStore->AdoptRecoveredChain(LastSequence, LastHash);
    LastSnapshotActionSequence = Snapshot.ActionSequence;
    for (const int32 SeatIndex : Snapshot.TrusteeSeats)
    {
        const FMahjongSeatInfo* Seat = Snapshot.RoomState.Seats.FindByPredicate(
            [SeatIndex](const FMahjongSeatInfo& Item) { return Item.SeatIndex == SeatIndex; });
        if (!Seat || Seat->PlayerId.IsEmpty()) continue;
        FPlayerTrusteeState& Trustee = TrusteeStateByPlayer.FindOrAdd(Seat->PlayerId);
        Trustee.bTrustee = true;
        Trustee.ChangedAtUtc = FDateTime::UtcNow();
    }
    bRecoveredGameServer = true;
    UE_LOG(LogMahjongServer, Display,
        TEXT("GameServer recovery completed Match=%s PreviousEpoch=%lld CurrentEpoch=%lld ReplayedActions=%d"),
        *Snapshot.MatchId, Snapshot.RoomEpoch, GameServerBridge->GetConfig().RoomEpoch, Actions.Num());
    OutError.Reset();
    return true;
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
