#include "Lobby/GuiyangLobbySubsystem.h"

#include "Auth/GuiyangLoginSubsystem.h"
#include "Game/GuiyangMahjongPlayerController.h"
#include "GuiyangMahjong.h"
#include "Lobby/GuiyangLobbyBackend.h"
#include "Lobby/GuiyangRemoteLobbyBackend.h"
#include "Misc/CommandLine.h"
#include "Misc/ConfigCacheIni.h"
#include "Misc/Parse.h"

namespace GuiyangLobbyPrivate
{
    constexpr const TCHAR* ConfigSection =
        TEXT("/Script/GuiyangMahjongClient.GuiyangLobbySubsystem");

    class FLocalLegacyLobbyBackend final : public ILobbyBackend
    {
    public:
        virtual EGuiyangLobbyBackendMode GetMode() const override
        {
            return EGuiyangLobbyBackendMode::LocalLegacy;
        }

        virtual FGuiyangLobbyOperationResult Bootstrap(
            AGuiyangMahjongPlayerController& PlayerController, const FString& RequestId) override
        {
            return MakeAccepted(RequestId);
        }

        virtual FGuiyangLobbyOperationResult QuickStart(
            AGuiyangMahjongPlayerController& PlayerController, const FString& RequestId) override
        {
            PlayerController.Server_RequestQuickStart();
            return MakeAccepted(RequestId);
        }

        virtual FGuiyangLobbyOperationResult CreateRoom(
            AGuiyangMahjongPlayerController& PlayerController, const FMahjongCreateRoomRequest& Request,
            const FString& RequestId) override
        {
            PlayerController.Server_RequestCreateRoomWithConfig(Request);
            return MakeAccepted(RequestId);
        }

        virtual FGuiyangLobbyOperationResult JoinRoom(
            AGuiyangMahjongPlayerController& PlayerController, const FMahjongJoinRoomRequest& Request,
            const FString& RequestId) override
        {
            PlayerController.Server_RequestJoinRoomByCode(Request);
            return MakeAccepted(RequestId);
        }

        virtual FGuiyangLobbyOperationResult Reconnect(
            AGuiyangMahjongPlayerController& PlayerController, const FString& RequestId) override
        {
            return MakeAccepted(RequestId);
        }

        virtual FGuiyangLobbyOperationResult LeaveCurrentRoom(
            AGuiyangMahjongPlayerController& PlayerController,
            const FString& RequestId) override
        {
            return MakeAccepted(RequestId);
        }

        virtual FGuiyangLobbyOperationResult CloseOwnedRoom(
            AGuiyangMahjongPlayerController& PlayerController, const FString& RequestId) override
        {
            PlayerController.Server_RequestLeaveRoom();
            return MakeAccepted(RequestId);
        }

    private:
        static FGuiyangLobbyOperationResult MakeAccepted(const FString& RequestId)
        {
            FGuiyangLobbyOperationResult Result;
            Result.bAccepted = true;
            Result.RequestId = RequestId;
            Result.ErrorCode = EGuiyangLobbyErrorCode::None;
            Result.ChineseMessage = TEXT("请求已提交");
            return Result;
        }
    };
}

void UGuiyangLobbySubsystem::Initialize(FSubsystemCollectionBase& Collection)
{
    // 根据配置选择本地或远程后端，并把后端回调汇入稳定的子系统事件。
    Super::Initialize(Collection);

    FString ConfiguredMode = TEXT("LocalLegacy");
    if (GConfig)
    {
        GConfig->GetString(GuiyangLobbyPrivate::ConfigSection, TEXT("BackendMode"), ConfiguredMode, GGameIni);
    }

    FString CommandLineMode;
    if (FParse::Value(FCommandLine::Get(), TEXT("MahjongLobbyBackend="), CommandLineMode))
    {
        ConfiguredMode = MoveTemp(CommandLineMode);
    }

    if (!TryParseBackendMode(ConfiguredMode, BackendMode))
    {
        UE_LOG(LogMahjongNet, Error,
            TEXT("大厅后端配置无效：BackendMode=%s，已安全使用 LocalLegacy"), *ConfiguredMode);
        BackendMode = EGuiyangLobbyBackendMode::LocalLegacy;
    }

    if (BackendMode == EGuiyangLobbyBackendMode::LocalLegacy)
    {
        Backend = MakeShared<GuiyangLobbyPrivate::FLocalLegacyLobbyBackend>();
    }
    else
    {
        float RequestTimeoutSeconds = 10.0f;
        float RoutePollIntervalSeconds = 0.25f;
        int32 RoutePollMaxAttempts = 120;
        if (GConfig)
        {
            GConfig->GetFloat(GuiyangLobbyPrivate::ConfigSection, TEXT("RemoteRequestTimeoutSeconds"), RequestTimeoutSeconds, GGameIni);
            GConfig->GetFloat(GuiyangLobbyPrivate::ConfigSection, TEXT("RemoteRoutePollIntervalSeconds"), RoutePollIntervalSeconds, GGameIni);
            GConfig->GetInt(GuiyangLobbyPrivate::ConfigSection, TEXT("RemoteRoutePollMaxAttempts"), RoutePollMaxAttempts, GGameIni);
        }
        FGuiyangRemoteLobbySettings Settings;
        if (FGuiyangPlatformEndpointSettings::Load(
                EGuiyangLegacyEndpointRole::Lobby,
                Settings.PlatformEndpoints))
        {
            Settings.RequestTimeoutSeconds = FMath::Clamp(RequestTimeoutSeconds, 2.0f, 30.0f);
            Settings.RoutePollIntervalSeconds = FMath::Clamp(RoutePollIntervalSeconds, 0.1f, 2.0f);
            Settings.RoutePollMaxAttempts = FMath::Clamp(RoutePollMaxAttempts, 1, 600);
            Backend = CreateRemoteLobbyBackend(*this, Settings);
            // 端点值不写日志；人工审查只需要确认统一网关或旧兼容模式。
            UE_LOG(
                LogMahjongNet,
                Log,
                TEXT("RemoteLobby HTTP 后端已配置：EndpointMode=%s"),
                Settings.PlatformEndpoints.bUsingLegacyDirectEndpoint
                    ? TEXT("LegacyDirect")
                    : TEXT("EdgeGateway"));
        }
        else
        {
            Backend.Reset();
            UE_LOG(LogMahjongNet, Error,
                TEXT("RemoteLobby 地址无效或不安全；正式环境必须使用 HTTPS，本机开发仅允许 loopback HTTP"));
        }
    }

    UE_LOG(LogMahjongNet, Log, TEXT("大厅子系统初始化完成：BackendMode=%s"), GetBackendModeName(BackendMode));
}

FGuiyangLobbyOperationResult UGuiyangLobbySubsystem::RequestBootstrap(APlayerController* PlayerController)
{
    const FString RequestId = MakeRequestId();
    AGuiyangMahjongPlayerController* MahjongController = Cast<AGuiyangMahjongPlayerController>(PlayerController);
    if (!MahjongController)
        return RejectRequest(RequestId, EGuiyangLobbyErrorCode::InvalidRequest, TEXT("当前玩家控制器不可用"));
    if (!Backend)
        return RejectRequest(RequestId, EGuiyangLobbyErrorCode::BackendNotConfigured, TEXT("远程大厅尚未安全配置"));
    return FinalizeBackendResult(Backend->Bootstrap(*MahjongController, RequestId));
}

void UGuiyangLobbySubsystem::Deinitialize()
{
    Backend.Reset();
    Super::Deinitialize();
}

FGuiyangLobbyOperationResult UGuiyangLobbySubsystem::RequestQuickStart(APlayerController* PlayerController)
{
    const FString RequestId = MakeRequestId();
    AGuiyangMahjongPlayerController* MahjongController = Cast<AGuiyangMahjongPlayerController>(PlayerController);
    if (!MahjongController)
    {
        return RejectRequest(RequestId, EGuiyangLobbyErrorCode::InvalidRequest, TEXT("当前玩家控制器不可用"));
    }
    if (!Backend)
    {
        return RejectRequest(RequestId, EGuiyangLobbyErrorCode::BackendNotConfigured,
            TEXT("远程大厅尚未配置，请切换到本地兼容模式"));
    }

    return FinalizeBackendResult(Backend->QuickStart(*MahjongController, RequestId));
}

FGuiyangLobbyOperationResult UGuiyangLobbySubsystem::RequestCreateRoom(
    APlayerController* PlayerController, const FMahjongCreateRoomRequest& Request)
{
    // 先生成请求 ID 并做登录/并发检查，再交由后端执行；UI 可立即进入加载页。
    const FString RequestId = MakeRequestId();
    AGuiyangMahjongPlayerController* MahjongController = Cast<AGuiyangMahjongPlayerController>(PlayerController);
    if (!MahjongController)
    {
        return RejectRequest(RequestId, EGuiyangLobbyErrorCode::InvalidRequest, TEXT("当前玩家控制器不可用"));
    }
    if (!Backend)
    {
        return RejectRequest(RequestId, EGuiyangLobbyErrorCode::BackendNotConfigured,
            TEXT("远程大厅尚未配置，请切换到本地兼容模式"));
    }

    return FinalizeBackendResult(Backend->CreateRoom(*MahjongController, Request, RequestId));
}

FGuiyangLobbyOperationResult UGuiyangLobbySubsystem::RequestJoinRoom(
    APlayerController* PlayerController, const FMahjongJoinRoomRequest& Request)
{
    const FString RequestId = MakeRequestId();
    AGuiyangMahjongPlayerController* MahjongController = Cast<AGuiyangMahjongPlayerController>(PlayerController);
    if (!MahjongController)
    {
        return RejectRequest(RequestId, EGuiyangLobbyErrorCode::InvalidRequest, TEXT("当前玩家控制器不可用"));
    }
    if (!Backend)
    {
        return RejectRequest(RequestId, EGuiyangLobbyErrorCode::BackendNotConfigured,
            TEXT("远程大厅尚未配置，请切换到本地兼容模式"));
    }

    return FinalizeBackendResult(Backend->JoinRoom(*MahjongController, Request, RequestId));
}

FGuiyangLobbyOperationResult UGuiyangLobbySubsystem::RequestReconnect(APlayerController* PlayerController)
{
    // 重连请求使用已记住的 RoomId/MatchId 向 Lobby 重新换取短期路由和票据。
    const FString RequestId = MakeRequestId();
    AGuiyangMahjongPlayerController* MahjongController = Cast<AGuiyangMahjongPlayerController>(PlayerController);
    if (!MahjongController)
        return RejectRequest(RequestId, EGuiyangLobbyErrorCode::InvalidRequest,
            TEXT("当前玩家控制器不可用"));
    if (!Backend || BackendMode != EGuiyangLobbyBackendMode::RemoteLobby)
        return RejectRequest(RequestId, EGuiyangLobbyErrorCode::BackendNotConfigured,
            TEXT("远程大厅重连尚未配置"));
    return FinalizeBackendResult(Backend->Reconnect(*MahjongController, RequestId));
}

FGuiyangLobbyOperationResult UGuiyangLobbySubsystem::RequestLeaveCurrentRoom(
    APlayerController* PlayerController)
{
    const FString RequestId = MakeRequestId();
    AGuiyangMahjongPlayerController* MahjongController =
        Cast<AGuiyangMahjongPlayerController>(PlayerController);
    if (!MahjongController)
    {
        return RejectRequest(RequestId,
            EGuiyangLobbyErrorCode::InvalidRequest,
            TEXT("当前玩家控制器不可用"));
    }
    if (!Backend)
    {
        return RejectRequest(RequestId,
            EGuiyangLobbyErrorCode::BackendNotConfigured,
            TEXT("大厅服务尚未配置"));
    }
    return FinalizeBackendResult(
        Backend->LeaveCurrentRoom(*MahjongController, RequestId));
}

FGuiyangLobbyOperationResult UGuiyangLobbySubsystem::RequestCloseOwnedRoom(APlayerController* PlayerController)
{
    const FString RequestId = MakeRequestId();
    AGuiyangMahjongPlayerController* MahjongController = Cast<AGuiyangMahjongPlayerController>(PlayerController);
    if (!MahjongController)
        return RejectRequest(RequestId, EGuiyangLobbyErrorCode::InvalidRequest,
            TEXT("当前玩家控制器不可用"));
    if (!Backend || BackendMode != EGuiyangLobbyBackendMode::RemoteLobby)
        return RejectRequest(RequestId, EGuiyangLobbyErrorCode::BackendNotConfigured,
            TEXT("远程大厅尚未配置"));
    return FinalizeBackendResult(Backend->CloseOwnedRoom(*MahjongController, RequestId));
}

bool UGuiyangLobbySubsystem::TryParseBackendMode(const FString& Value, EGuiyangLobbyBackendMode& OutMode)
{
    const FString Normalized = Value.TrimStartAndEnd();
    if (Normalized.Equals(TEXT("LocalLegacy"), ESearchCase::IgnoreCase))
    {
        OutMode = EGuiyangLobbyBackendMode::LocalLegacy;
        return true;
    }
    if (Normalized.Equals(TEXT("RemoteLobby"), ESearchCase::IgnoreCase))
    {
        OutMode = EGuiyangLobbyBackendMode::RemoteLobby;
        return true;
    }
    return false;
}

const TCHAR* UGuiyangLobbySubsystem::GetBackendModeName(const EGuiyangLobbyBackendMode Mode)
{
    return Mode == EGuiyangLobbyBackendMode::RemoteLobby ? TEXT("RemoteLobby") : TEXT("LocalLegacy");
}

FString UGuiyangLobbySubsystem::MakeRequestId() const
{
    return FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
}

void UGuiyangLobbySubsystem::HandleRemoteBootstrap(const FGuiyangLobbyBootstrap& Bootstrap)
{
    const UGuiyangLoginSubsystem* Login = GetGameInstance()
        ? GetGameInstance()->GetSubsystem<UGuiyangLoginSubsystem>() : nullptr;
    if (!Login || !Login->IsSessionValid() || Login->GetCurrentProfile().PlayerId != Bootstrap.PlayerId)
    {
        HandleRemoteFailure(Bootstrap.RequestId, EGuiyangLobbyErrorCode::SessionExpired,
            TEXT("大厅身份与本地登录会话不匹配"));
        return;
    }
    OnBootstrapUpdated.Broadcast(Bootstrap);
    UE_LOG(LogMahjongNet, Log, TEXT("大厅启动信息已更新：RequestId=%s，PlayerId=%s，Online=%d"),
        *Bootstrap.RequestId, *Bootstrap.PlayerId, Bootstrap.OnlinePlayerCount);
}

void UGuiyangLobbySubsystem::HandleRemoteRouteReady(
    AGuiyangMahjongPlayerController* PlayerController, const FGuiyangGameServerRoute& Route)
{
    // 只有匹配当前请求的路由才能触发 ClientTravel，防止旧 HTTP 回调覆盖新操作。
    if (!PlayerController)
    {
        HandleRemoteFailure(Route.RequestId, EGuiyangLobbyErrorCode::Cancelled, TEXT("玩家控制器已失效"));
        return;
    }
    OnRouteReady.Broadcast(Route);
    PlayerController->ConnectToAllocatedServer(Route);
}

void UGuiyangLobbySubsystem::HandleRemoteFailure(const FString& RequestId,
    const EGuiyangLobbyErrorCode ErrorCode, const FString& ChineseMessage)
{
    UE_LOG(LogMahjongNet, Warning, TEXT("RemoteLobby 请求失败：RequestId=%s，ErrorCode=%d，原因=%s"),
        *RequestId, static_cast<int32>(ErrorCode), *ChineseMessage);
    OnRequestFailed.Broadcast(RequestId, ErrorCode, ChineseMessage);
}

FGuiyangLobbyOperationResult UGuiyangLobbySubsystem::FinalizeBackendResult(
    const FGuiyangLobbyOperationResult& Result)
{
    if (Result.bAccepted)
    {
        OnRequestSubmitted.Broadcast(Result.RequestId, BackendMode);
    }
    else
    {
        OnRequestFailed.Broadcast(Result.RequestId, Result.ErrorCode, Result.ChineseMessage);
    }
    return Result;
}

FGuiyangLobbyOperationResult UGuiyangLobbySubsystem::RejectRequest(
    const FString& RequestId, const EGuiyangLobbyErrorCode ErrorCode, const FString& ChineseMessage)
{
    // 所有同步拒绝也使用统一错误事件，保证 UI 不需要区分同步/异步失败。
    FGuiyangLobbyOperationResult Result;
    Result.RequestId = RequestId;
    Result.ErrorCode = ErrorCode;
    Result.ChineseMessage = ChineseMessage;
    UE_LOG(LogMahjongNet, Warning, TEXT("大厅请求被拒绝：RequestId=%s，ErrorCode=%d，原因=%s"),
        *RequestId, static_cast<int32>(ErrorCode), *ChineseMessage);
    OnRequestFailed.Broadcast(RequestId, ErrorCode, ChineseMessage);
    return Result;
}
