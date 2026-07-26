#include "Game/GuiyangClientControllerBridgeImpl.h"

#include "Auth/GuiyangLoginSubsystem.h"
#include "Engine/AssetManager.h"
#include "CineCameraComponent.h"
#include "Engine/Scene.h"
#include "EngineUtils.h"
#include "Game/GuiyangMahjongGameState.h"
#include "Game/GuiyangMahjongPlayerController.h"
#include "Game/GuiyangMahjongPlayerState.h"
#include "Game/Mahjong3DTableActor.h"
#include "Game/MahjongRoomCameraActor.h"
#include "Game/MahjongRoomPresentationActor.h"
#include "GenericPlatform/GenericPlatformHttp.h"
#include "GuiyangMahjong.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformMisc.h"
#include "HAL/PlatformTime.h"
#include "History/GuiyangMatchHistorySubsystem.h"
#include "Lobby/GuiyangLobbySubsystem.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Network/GuiyangReconnectSubsystem.h"
#include "Settings/MahjongRoomPresentationSettings.h"
#include "TimerManager.h"
#include "UI/MobileRootHUDWidget.h"
#include "UnrealClient.h"

namespace
{
    constexpr double MinimumCreatingRoomLoadingSeconds = 1.5;

    void ApplyMissingRoomPostProcessDefaults(FPostProcessSettings& Settings)
    {
        // Blueprint-authored overrides remain authoritative. Missing overrides fall back to
        // deterministic tabletop settings so copying an otherwise-default CineCamera component
        // cannot silently re-enable eye adaptation, bloom and physical-camera exposure.
        if (!Settings.bOverride_AutoExposureMethod)
        {
            Settings.bOverride_AutoExposureMethod = true;
            Settings.AutoExposureMethod = AEM_Histogram;
        }
        if (!Settings.bOverride_AutoExposureApplyPhysicalCameraExposure)
        {
            Settings.bOverride_AutoExposureApplyPhysicalCameraExposure = true;
            Settings.AutoExposureApplyPhysicalCameraExposure = false;
        }
        if (!Settings.bOverride_AutoExposureBias)
        {
            Settings.bOverride_AutoExposureBias = true;
            Settings.AutoExposureBias = -1.0f;
        }
        if (!Settings.bOverride_BloomIntensity)
        {
            Settings.bOverride_BloomIntensity = true;
            Settings.BloomIntensity = 0.0f;
        }
        if (!Settings.bOverride_LensFlareIntensity)
        {
            Settings.bOverride_LensFlareIntensity = true;
            Settings.LensFlareIntensity = 0.0f;
        }
        if (!Settings.bOverride_MotionBlurAmount)
        {
            Settings.bOverride_MotionBlurAmount = true;
            Settings.MotionBlurAmount = 0.0f;
        }
        if (!Settings.bOverride_Sharpen)
        {
            Settings.bOverride_Sharpen = true;
            Settings.Sharpen = 0.75f;
        }
    }
}

void UGuiyangClientControllerBridgeImpl::BeginDestroy()
{
    if (PresentationLoadHandle.IsValid())
    {
        PresentationLoadHandle->CancelHandle();
        PresentationLoadHandle.Reset();
    }
    Super::BeginDestroy();
}

UWorld* UGuiyangClientControllerBridgeImpl::GetWorld() const
{
    return Controller ? Controller->GetWorld() : nullptr;
}

void UGuiyangClientControllerBridgeImpl::InitializeClient(AGuiyangMahjongPlayerController& InController)
{
    // 根 HUD 先创建并显示登录页；房间表现仅在进入 MahjongRoomMap 后按需加载。
    Controller = &InController;
    const bool bUIReviewScreenshot = FParse::Param(FCommandLine::Get(), TEXT("UIReviewScreenshot"));
    if (!bUIReviewScreenshot)
    {
        if (const UGuiyangLoginSubsystem* Login = Controller->GetGameInstance()
            ? Controller->GetGameInstance()->GetSubsystem<UGuiyangLoginSubsystem>() : nullptr;
            Login && Login->IsSessionValid())
        {
            const FGuiyangLoginProfile& Profile = Login->GetCurrentProfile();
            Controller->Server_AuthenticateSession(Profile.PlayerId, Profile.DisplayName, Profile.Provider,
                Login->GetSessionTokenForNetwork());
        }
    }

    if (GetWorld() && GetWorld()->GetMapName().Contains(TEXT("MahjongRoomMap")))
    {
        RequestRoomPresentationClassLoad();
        EnsureRoomPresentation();
    }

    UClass* RootHUDClass = LoadClass<UMobileRootHUDWidget>(nullptr,
        TEXT("/Game/UI/Screens/WBP_RootHUD.WBP_RootHUD_C"));
    if (!RootHUDClass)
    {
        UE_LOG(LogMahjongUI, Error, TEXT("Unable to load WBP_RootHUD; client UI did not start"));
        return;
    }
    RootHUDInstance = CreateWidget<UMobileRootHUDWidget>(Controller, RootHUDClass);
    RootHUDInstance->AddToViewport(100);
    Controller->bShowMouseCursor = true;
    FInputModeGameAndUI InputMode;
    InputMode.SetLockMouseToViewportBehavior(EMouseLockMode::DoNotLock);
    InputMode.SetHideCursorDuringCapture(false);
    Controller->SetInputMode(InputMode);

    if (!bUIReviewScreenshot)
    {
        return;
    }

    FString ReviewScreen = TEXT("Login");
    FParse::Value(FCommandLine::Get(), TEXT("UIReviewScreen="), ReviewScreen);
    if (!RootHUDInstance->ApplyVisualReviewScenario(ReviewScreen))
    {
        UE_LOG(LogMahjongUI, Error, TEXT("UI review scenario initialization failed: %s"), *ReviewScreen);
        return;
    }
    FString ReviewName = TEXT("UIReview");
    FParse::Value(FCommandLine::Get(), TEXT("UIReviewName="), ReviewName);
    ReviewName.ReplaceInline(TEXT("\\"), TEXT("/"));
    while (ReviewName.StartsWith(TEXT("/"))) ReviewName.RightChopInline(1);
    if (ReviewName.IsEmpty() || ReviewName.Contains(TEXT("..")) || !FPaths::IsRelative(ReviewName))
    {
        UE_LOG(LogMahjongUI, Error, TEXT("Unsafe UI review screenshot name rejected: %s"), *ReviewName);
        return;
    }
    if (!ReviewName.EndsWith(TEXT(".png"), ESearchCase::IgnoreCase)) ReviewName += TEXT(".png");
    const FString ScreenshotPath = FPaths::ProjectSavedDir() / TEXT("UIReview") / ReviewName;
    IFileManager::Get().MakeDirectory(*FPaths::GetPath(ScreenshotPath), true);
    float CaptureDelaySeconds = 2.0f;
    FParse::Value(FCommandLine::Get(), TEXT("UIReviewDelaySeconds="), CaptureDelaySeconds);
    CaptureDelaySeconds = FMath::Clamp(CaptureDelaySeconds, 1.0f, 30.0f);
    FTimerHandle ScreenshotTimer;
    Controller->GetWorldTimerManager().SetTimer(ScreenshotTimer,
        FTimerDelegate::CreateWeakLambda(this, [this, ScreenshotPath]()
        {
            FScreenshotRequest::RequestScreenshot(ScreenshotPath, true, false);
            FTimerHandle ExitTimer;
            if (Controller)
            {
                Controller->GetWorldTimerManager().SetTimer(ExitTimer,
                    [] { FPlatformMisc::RequestExit(false); }, 1.0f, false);
            }
        }), CaptureDelaySeconds, false);
}

AActor* UGuiyangClientControllerBridgeImpl::EnsureRoomPresentation()
{
    // 优先复用带稳定标签的关卡实例，避免重连或热重载产生两套灯光/摄像机。
    if (!Controller || !Controller->IsLocalController() || !GetWorld()) return nullptr;
    if (!IsValid(RoomPresentationActor))
    {
        TActorIterator<AMahjongRoomPresentationActor> It(GetWorld());
        if (It) RoomPresentationActor = *It;
        if (!RoomPresentationActor)
        {
            const UMahjongRoomPresentationSettings* Settings =
                GetDefault<UMahjongRoomPresentationSettings>();
            UClass* PresentationClass = Settings ? Settings->PresentationClass.Get() : nullptr;
            if (PresentationClass)
            {
                RoomPresentationActor = SpawnRoomPresentation(*PresentationClass);
            }
            else if (!bPresentationLoadFailed && Settings && !Settings->PresentationClass.IsNull())
            {
                RequestRoomPresentationClassLoad();
                return nullptr;
            }
            else
            {
                UE_LOG(LogMahjongUI, Warning,
                    TEXT("Configured room presentation is unavailable; spawning native fallback"));
                RoomPresentationActor = SpawnRoomPresentation(
                    *AMahjongRoomPresentationActor::StaticClass());
            }
        }
    }
    if (RoomPresentationActor)
    {
        RoomTableActor = RoomPresentationActor->GetTableActor();
    }
    RoomCameraActor = EnsureRoomCamera();
    ApplyRoomPresentationViewTarget();
    return RoomTableActor;
}

void UGuiyangClientControllerBridgeImpl::RequestRoomPresentationClassLoad()
{
    // 软类异步加载把桌面、灯光和相机资源隔离在客户端房间阶段。
    const UMahjongRoomPresentationSettings* Settings =
        GetDefault<UMahjongRoomPresentationSettings>();
    if (!Settings || Settings->PresentationClass.IsNull())
    {
        bPresentationLoadFailed = true;
        return;
    }
    if (Settings->PresentationClass.Get())
    {
        bPresentationLoadFailed = false;
        return;
    }
    if (PresentationLoadHandle.IsValid() && !PresentationLoadHandle->HasLoadCompleted())
    {
        return;
    }

    bPresentationLoadFailed = false;
    PresentationLoadHandle = UAssetManager::GetStreamableManager().RequestAsyncLoad(
        Settings->PresentationClass.ToSoftObjectPath(),
        FStreamableDelegate::CreateUObject(
            this, &ThisClass::HandleRoomPresentationClassLoaded),
        FStreamableManager::AsyncLoadHighPriority);
    if (!PresentationLoadHandle.IsValid())
    {
        bPresentationLoadFailed = true;
        UE_LOG(LogMahjongUI, Error, TEXT("Unable to start async room presentation load: %s"),
            *Settings->PresentationClass.ToString());
    }
}

void UGuiyangClientControllerBridgeImpl::HandleRoomPresentationClassLoaded()
{
    PresentationLoadHandle.Reset();
    const UMahjongRoomPresentationSettings* Settings =
        GetDefault<UMahjongRoomPresentationSettings>();
    UClass* LoadedClass = Settings ? Settings->PresentationClass.Get() : nullptr;
    bPresentationLoadFailed = LoadedClass == nullptr;
    if (!LoadedClass)
    {
        UE_LOG(LogMahjongUI, Error, TEXT("Async room presentation load failed; native fallback will be used"));
    }
    else
    {
        UE_LOG(LogMahjongUI, Display, TEXT("Room presentation class loaded: %s"),
            *LoadedClass->GetPathName());
    }

    if (GetWorld() && GetWorld()->GetMapName().Contains(TEXT("MahjongRoomMap")))
    {
        EnsureRoomPresentation();
    }
}

AMahjongRoomPresentationActor* UGuiyangClientControllerBridgeImpl::SpawnRoomPresentation(
    UClass& PresentationClass)
{
    if (!GetWorld() || !PresentationClass.IsChildOf(AMahjongRoomPresentationActor::StaticClass()))
    {
        UE_LOG(LogMahjongUI, Error, TEXT("Rejected invalid room presentation class: %s"),
            *PresentationClass.GetPathName());
        return nullptr;
    }
    FActorSpawnParameters Parameters;
    Parameters.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
    AMahjongRoomPresentationActor* Spawned =
        GetWorld()->SpawnActor<AMahjongRoomPresentationActor>(
            &PresentationClass, FTransform::Identity, Parameters);
    if (Spawned)
    {
        UE_LOG(LogMahjongUI, Display, TEXT("Spawned local room presentation: %s"),
            *Spawned->GetClass()->GetPathName());
    }
    return Spawned;
}

AMahjongRoomCameraActor* UGuiyangClientControllerBridgeImpl::EnsureRoomCamera()
{
    if (!GetWorld())
    {
        return nullptr;
    }
    if (IsValid(RoomCameraActor))
    {
        return RoomCameraActor;
    }

    // The presentation Blueprint owns the table, lights, and a legacy camera component. Treating
    // that whole Actor as a ViewTarget allows component registration/construction to reset the
    // camera after the first frame. A dedicated Actor has one authoritative transform.
    for (TActorIterator<AMahjongRoomCameraActor> It(GetWorld()); It; ++It)
    {
        if (It->ActorHasTag(AMahjongRoomCameraActor::RoomCameraTag))
        {
            return *It;
        }
    }

    FActorSpawnParameters Parameters;
    Parameters.SpawnCollisionHandlingOverride =
        ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
    AMahjongRoomCameraActor* Spawned = GetWorld()->SpawnActor<AMahjongRoomCameraActor>(
        AMahjongRoomCameraActor::StaticClass(), FTransform::Identity, Parameters);
    if (Spawned)
    {
        Spawned->SetReplicates(false);
        UE_LOG(LogMahjongUI, Display, TEXT("Spawned fixed local Mahjong room camera"));
    }
    return Spawned;
}

void UGuiyangClientControllerBridgeImpl::ApplyRoomPresentationViewTarget()
{
    if (!RoomCameraActor || !Controller)
    {
        return;
    }

    UCineCameraComponent* RuntimeCamera = RoomCameraActor->GetCineCameraComponent();
    const UCineCameraComponent* EditorCamera = RoomPresentationActor
        ? RoomPresentationActor->FindComponentByClass<UCineCameraComponent>() : nullptr;
    if (RuntimeCamera && EditorCamera)
    {
        // Camera composition remains artist-authored in BP_MahjongRoomPresentation. Copy the
        // editor camera into a dedicated runtime ViewTarget so Blueprint component registration
        // cannot move the active camera after the first rendered frame.
        RoomCameraActor->SetActorLocationAndRotation(
            EditorCamera->GetComponentLocation(), EditorCamera->GetComponentRotation());
        RuntimeCamera->SetFilmback(EditorCamera->Filmback);
        RuntimeCamera->SetLensSettings(EditorCamera->LensSettings);
        RuntimeCamera->SetFocusSettings(EditorCamera->FocusSettings);
        RuntimeCamera->SetCurrentFocalLength(EditorCamera->CurrentFocalLength);
        RuntimeCamera->SetCurrentAperture(EditorCamera->CurrentAperture);
        RuntimeCamera->SetConstraintAspectRatio(EditorCamera->bConstrainAspectRatio);
        RuntimeCamera->PostProcessSettings = EditorCamera->PostProcessSettings;
        ApplyMissingRoomPostProcessDefaults(RuntimeCamera->PostProcessSettings);
        RuntimeCamera->PostProcessBlendWeight = EditorCamera->PostProcessBlendWeight;
        UE_LOG(LogMahjongUI, Display,
            TEXT("Applied editor-authored room camera: location=%s rotation=%s focal=%.1fmm"),
            *RoomCameraActor->GetActorLocation().ToCompactString(),
            *RoomCameraActor->GetActorRotation().ToCompactString(),
            RuntimeCamera->CurrentFocalLength);
    }
    else if (RuntimeCamera)
    {
        // Native fallback is only used if the configured presentation asset is unavailable.
        const FVector TableCenter = RoomTableActor
            ? RoomTableActor->GetActorLocation() + FVector(0.0f, 0.0f, 35.0f)
            : FVector(0.0f, 0.0f, 35.0f);
        // The requested 60-degree tabletop-normal angle is a -30 degree UE pitch.
        // This keeps the near hand visible while retaining the reference image's table depth.
        const FVector CameraLocation = TableCenter + FVector(0.0f, -2350.0f, 1357.0f);
        const FRotator CameraRotation(-30.0f, 90.0f, 0.0f);
        RoomCameraActor->SetActorLocationAndRotation(CameraLocation, CameraRotation);
        RuntimeCamera->SetCurrentFocalLength(30.0f);
        FCameraFilmbackSettings Filmback = RuntimeCamera->Filmback;
        Filmback.SensorVerticalOffset = -2.0f;
        RuntimeCamera->SetFilmback(Filmback);
        RuntimeCamera->SetConstraintAspectRatio(false);
        UE_LOG(LogMahjongUI, Warning,
            TEXT("Applied native fallback room camera because editor camera is unavailable"));
    }
    if (RuntimeCamera)
    {
        // The editor-authored camera controls composition, but gameplay readability must remain
        // deterministic after its post-process and focus settings have been copied.
        RoomCameraActor->ConfigureStablePostProcess();
    }

    // Automatic camera management is disabled by the PlayerController constructor. ClientRestart,
    // spectator creation, and replicated Pawn changes therefore cannot replace this target later.
    if (Controller->GetViewTarget() != RoomCameraActor)
    {
        Controller->SetViewTarget(RoomCameraActor);
    }
}

void UGuiyangClientControllerBridgeImpl::ConnectToServer(
    const FString& ServerIP, const int32 Port, const FString& PlayerName)
{
    if (!Controller) return;
    const FString CleanIP = ServerIP.TrimStartAndEnd();
    const FString CleanName = PlayerName.TrimStartAndEnd();
    if (CleanIP.IsEmpty() || CleanName.IsEmpty() || CleanName.Len() > 24 || Port < 1 || Port > 65535)
    {
        Controller->Client_ShowErrorMessage(TEXT("服务器地址、端口或昵称格式不正确"));
        return;
    }
    Controller->SetPendingPlayerName(CleanName);
    if (UGuiyangReconnectSubsystem* Reconnect = Controller->GetGameInstance()
        ? Controller->GetGameInstance()->GetSubsystem<UGuiyangReconnectSubsystem>() : nullptr)
    {
        Reconnect->RememberConnection(CleanIP, Port, CleanName);
    }
    Controller->ClientTravel(FString::Printf(TEXT("%s:%d"), *CleanIP, Port), TRAVEL_Absolute);
}

void UGuiyangClientControllerBridgeImpl::ConnectToAllocatedServer(const FGuiyangGameServerRoute& Route)
{
    // 加载层立即显示，并保证最短可见时间后再携带票据执行 ClientTravel。
    if (!Controller) return;
    const FString PlayerId = Route.PlayerId.TrimStartAndEnd();
    if (!Route.HasValidEndpoint() || PlayerId.IsEmpty() || PlayerId.Len() > 80
        || Route.JoinTicket.Len() < 32 || Route.JoinTicket.Len() > 4096
        || Route.TicketExpireAtUtc <= FDateTime::UtcNow())
    {
        CreatingRoomLoadingShownAtSeconds = 0.0;
        PendingAllocatedRoute = {};
        Controller->GetWorldTimerManager().ClearTimer(CreatingRoomTravelDelayTimer);
        if (RootHUDInstance) RootHUDInstance->ShowLobby();
        Controller->Client_ShowErrorMessage(TEXT("牌桌路由或入场票据无效"));
        return;
    }
    if (RootHUDInstance) RootHUDInstance->UpdateCreatingRoomStage(TEXT("服务器已就绪，正在进入房间……"));
    const double RemainingSeconds = MinimumCreatingRoomLoadingSeconds
        - (FPlatformTime::Seconds() - CreatingRoomLoadingShownAtSeconds);
    if (CreatingRoomLoadingShownAtSeconds > 0.0 && RemainingSeconds > 0.0)
    {
        PendingAllocatedRoute = Route;
        Controller->GetWorldTimerManager().ClearTimer(CreatingRoomTravelDelayTimer);
        Controller->GetWorldTimerManager().SetTimer(CreatingRoomTravelDelayTimer, this,
            &ThisClass::CompleteDelayedAllocatedServerConnection, static_cast<float>(RemainingSeconds), false);
        return;
    }
    CreatingRoomLoadingShownAtSeconds = 0.0;
    TravelToAllocatedServer(Route);
}

void UGuiyangClientControllerBridgeImpl::CompleteDelayedAllocatedServerConnection()
{
    FGuiyangGameServerRoute Route = MoveTemp(PendingAllocatedRoute);
    PendingAllocatedRoute = {};
    CreatingRoomLoadingShownAtSeconds = 0.0;
    TravelToAllocatedServer(MoveTemp(Route));
}

void UGuiyangClientControllerBridgeImpl::TravelToAllocatedServer(FGuiyangGameServerRoute Route)
{
    // 票据只放在一次旅行 URL 中，并在生成 URL 后清除待处理路由中的副本。
    if (!Controller) return;
    if (UGuiyangReconnectSubsystem* Reconnect = Controller->GetGameInstance()
        ? Controller->GetGameInstance()->GetSubsystem<UGuiyangReconnectSubsystem>() : nullptr)
    {
        Reconnect->RememberRemoteRoute(Route.RoomId, Route.MatchId);
    }
    const FString URL = FString::Printf(TEXT("%s:%d?PlayerId=%s?JoinTicket=%s"),
        *Route.ServerIP, Route.ServerPort,
        *FGenericPlatformHttp::UrlEncode(Route.PlayerId.TrimStartAndEnd()),
        *FGenericPlatformHttp::UrlEncode(Route.JoinTicket));
    Controller->ClientTravel(URL, TRAVEL_Absolute);
}

void UGuiyangClientControllerBridgeImpl::RetryLastConnection()
{
    // 远程房间优先向 Lobby 换取新票据，只有本地模式才复用旧 IP/端口。
    if (!Controller) return;
    UGuiyangReconnectSubsystem* Reconnect = Controller->GetGameInstance()
        ? Controller->GetGameInstance()->GetSubsystem<UGuiyangReconnectSubsystem>() : nullptr;
    UGuiyangLobbySubsystem* Lobby = Controller->GetGameInstance()
        ? Controller->GetGameInstance()->GetSubsystem<UGuiyangLobbySubsystem>() : nullptr;
    if (Reconnect && Lobby && Lobby->GetBackendMode() == EGuiyangLobbyBackendMode::RemoteLobby)
    {
        if (!Reconnect->CanRetry())
        {
            Controller->Client_ShowErrorMessage(TEXT("重连保留时间已结束或牌桌标识不可用"));
            return;
        }
        Reconnect->MarkRetrying();
        const FGuiyangLobbyOperationResult Result = Lobby->RequestReconnect(Controller);
        if (!Result.bAccepted) Reconnect->MarkRetryFailed(Result.ChineseMessage);
        return;
    }
    FString IP;
    FString Name;
    int32 Port = 7777;
    if (!Reconnect || !Reconnect->GetLastConnection(IP, Port, Name) || !Reconnect->CanRetry())
    {
        Controller->Client_ShowErrorMessage(TEXT("重连地址不可用或重连保留时间已结束"));
        return;
    }
    Reconnect->MarkRetrying();
    ConnectToServer(IP, Port, Name);
}

void UGuiyangClientControllerBridgeImpl::ReturnToConnectScreen()
{
    if (!Controller) return;
    if (UGuiyangReconnectSubsystem* Reconnect = Controller->GetGameInstance()
        ? Controller->GetGameInstance()->GetSubsystem<UGuiyangReconnectSubsystem>() : nullptr)
    {
        Reconnect->CancelReconnect();
    }
    const UGuiyangLobbySubsystem* Lobby = Controller->GetGameInstance()
        ? Controller->GetGameInstance()->GetSubsystem<UGuiyangLobbySubsystem>() : nullptr;
    if (RootHUDInstance)
    {
        if (Lobby && Lobby->GetBackendMode() == EGuiyangLobbyBackendMode::RemoteLobby) RootHUDInstance->ShowLobby();
        else RootHUDInstance->ShowConnectServer();
    }
}

void UGuiyangClientControllerBridgeImpl::ReturnToLobby()
{
    // 房主先请求关闭远程房间；非房主只离开游戏服并保留房间供二次进入。
    if (!Controller) return;
    UGuiyangLobbySubsystem* Lobby = Controller->GetGameInstance()
        ? Controller->GetGameInstance()->GetSubsystem<UGuiyangLobbySubsystem>() : nullptr;
    if (!Lobby || Lobby->GetBackendMode() == EGuiyangLobbyBackendMode::LocalLegacy)
    {
        Controller->Server_RequestLeaveRoom();
        return;
    }
    const AGuiyangMahjongGameState* State = GetWorld() ? GetWorld()->GetGameState<AGuiyangMahjongGameState>() : nullptr;
    const AGuiyangMahjongPlayerState* Player = Controller->GetPlayerState<AGuiyangMahjongPlayerState>();
    const bool bOwner = State && Player && State->RoomState.RoomInfo.OwnerPlayerId == Player->MahjongPlayerId;
    if (!bOwner)
    {
        CompleteRemoteReturnToLobby();
        return;
    }
    const FGuiyangLobbyOperationResult Result = Lobby->RequestCloseOwnedRoom(Controller);
    if (!Result.bAccepted) Controller->Client_ShowErrorMessage(Result.ChineseMessage);
}

void UGuiyangClientControllerBridgeImpl::ShowCreatingRoomLoading()
{
    if (!Controller) return;
    RequestRoomPresentationClassLoad();
    CreatingRoomLoadingShownAtSeconds = FPlatformTime::Seconds();
    PendingAllocatedRoute = {};
    Controller->GetWorldTimerManager().ClearTimer(CreatingRoomTravelDelayTimer);
    if (RootHUDInstance) RootHUDInstance->ShowCreatingRoom();
}

void UGuiyangClientControllerBridgeImpl::RequestCreateRoomWithLoading(const FMahjongCreateRoomRequest& Request)
{
    if (!Controller) return;
    ShowCreatingRoomLoading();
    Controller->GetWorldTimerManager().SetTimerForNextTick(FTimerDelegate::CreateWeakLambda(this, [this, Request]()
    {
        if (!Controller) return;
        UGuiyangLobbySubsystem* Lobby = Controller->GetGameInstance()
            ? Controller->GetGameInstance()->GetSubsystem<UGuiyangLobbySubsystem>() : nullptr;
        if (!Lobby)
        {
            if (RootHUDInstance) RootHUDInstance->ShowLobby();
            Controller->Client_ShowErrorMessage(TEXT("大厅服务尚未初始化，请稍后重试"));
            return;
        }
        const FGuiyangLobbyOperationResult Result = Lobby->RequestCreateRoom(Controller, Request);
        if (!Result.bAccepted && RootHUDInstance) RootHUDInstance->ShowLobby();
    }));
}

void UGuiyangClientControllerBridgeImpl::CompleteRemoteReturnToLobby()
{
    if (!Controller) return;
    CreatingRoomLoadingShownAtSeconds = 0.0;
    PendingAllocatedRoute = {};
    Controller->GetWorldTimerManager().ClearTimer(CreatingRoomTravelDelayTimer);
    if (UGuiyangReconnectSubsystem* Reconnect = Controller->GetGameInstance()
        ? Controller->GetGameInstance()->GetSubsystem<UGuiyangReconnectSubsystem>() : nullptr)
    {
        Reconnect->CancelReconnect();
    }
    Controller->ClientTravel(TEXT("/Engine/Maps/Entry"), TRAVEL_Absolute);
}

void UGuiyangClientControllerBridgeImpl::NotifyReconnectRestored(const FMahjongReconnectSnapshot& Snapshot)
{
    if (Controller)
    {
        if (UGuiyangReconnectSubsystem* Reconnect = Controller->GetGameInstance()
            ? Controller->GetGameInstance()->GetSubsystem<UGuiyangReconnectSubsystem>() : nullptr)
        {
            Reconnect->MarkRestored();
        }
    }
}

void UGuiyangClientControllerBridgeImpl::NotifyFinalSettlement(const FMahjongFinalSettlementResult& Result)
{
    if (Controller)
    {
        if (UGuiyangMatchHistorySubsystem* History = Controller->GetGameInstance()
            ? Controller->GetGameInstance()->GetSubsystem<UGuiyangMatchHistorySubsystem>() : nullptr)
        {
            History->RecordFinalSettlement(Result);
        }
    }
}

void UGuiyangClientControllerBridgeImpl::HandleIntegrationPrivateState(const FMahjongPrivatePlayerState& PrivateState)
{
    // Automated integration remains opt-in and is intentionally excluded from production behavior.
}
