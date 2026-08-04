#include "Lobby/GuiyangRemoteLobbyBackend.h"

#include "Auth/GuiyangLoginSubsystem.h"
#include "Containers/Ticker.h"
#include "Dom/JsonObject.h"
#include "Game/GuiyangMahjongPlayerController.h"
#include "GuiyangMahjong.h"
#include "HttpModule.h"
#include "Interfaces/IHttpRequest.h"
#include "Interfaces/IHttpResponse.h"
#include "Lobby/GuiyangLobbySubsystem.h"
#include "Network/MahjongNetworkTypes.h"
#include "Network/GuiyangReconnectSubsystem.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

namespace GuiyangRemoteLobbyPrivate
{
    FString SerializeObject(const TSharedRef<FJsonObject>& Object)
    {
        FString Json;
        const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Json);
        FJsonSerializer::Serialize(Object, Writer);
        return Json;
    }

    bool DeserializeObject(const FString& Json, TSharedPtr<FJsonObject>& OutObject)
    {
        const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
        return FJsonSerializer::Deserialize(Reader, OutObject) && OutObject.IsValid();
    }

    FGuiyangLobbyOperationResult MakeResult(const FString& RequestId, const bool bAccepted,
        const EGuiyangLobbyErrorCode ErrorCode, const FString& Message)
    {
        FGuiyangLobbyOperationResult Result;
        Result.bAccepted = bAccepted;
        Result.RequestId = RequestId;
        Result.ErrorCode = ErrorCode;
        Result.ChineseMessage = Message;
        return Result;
    }

    const TCHAR* JiScopeName(const EMahjongJiCountingScope Scope)
    {
        switch (Scope)
        {
        case EMahjongJiCountingScope::HandOnly: return TEXT("HandOnly");
        case EMahjongJiCountingScope::HandAndDiscard: return TEXT("HandAndDiscard");
        case EMahjongJiCountingScope::HandMeldAndDiscard: return TEXT("HandMeldAndDiscard");
        default: return TEXT("HandAndMeld");
        }
    }

    bool TryGetOperationRoomCode(const FString& Json, FString& OutRoomCode)
    {
        TSharedPtr<FJsonObject> Object;
        return DeserializeObject(Json, Object)
            && Object->TryGetStringField(TEXT("roomCode"), OutRoomCode)
            && OutRoomCode.Len() == 6 && OutRoomCode.IsNumeric();
    }

    class FRemoteLobbyBackend final : public ILobbyBackend,
        public TSharedFromThis<FRemoteLobbyBackend>
    {
    public:
        FRemoteLobbyBackend(UGuiyangLobbySubsystem& InOwner, const FGuiyangRemoteLobbySettings& InSettings)
            : Owner(&InOwner), Settings(InSettings)
        {
        }

        virtual EGuiyangLobbyBackendMode GetMode() const override
        {
            return EGuiyangLobbyBackendMode::RemoteLobby;
        }

        virtual FGuiyangLobbyOperationResult Bootstrap(
            AGuiyangMahjongPlayerController& PlayerController, const FString& RequestId) override
        {
            FString Token;
            FString PlayerId;
            FGuiyangLobbyOperationResult Rejection;
            if (!ResolveAuthorization(PlayerController, RequestId, Token, PlayerId, Rejection)) return Rejection;
            StartRequest(TEXT("GET"), TEXT("/v1/lobby/bootstrap"), Token, RequestId, FString(), false,
                [WeakThis = TWeakPtr<FRemoteLobbyBackend>(AsShared()), RequestId](FHttpResponsePtr Response, const bool bSucceeded)
                {
                    const TSharedPtr<FRemoteLobbyBackend> Self = WeakThis.Pin();
                    if (!Self) return;
                    if (!Self->IsSuccess(Response, bSucceeded))
                    {
                        Self->NotifyFailure(RequestId, Response, bSucceeded, TEXT("无法获取大厅信息"));
                        return;
                    }
                    FGuiyangLobbyBootstrap Bootstrap;
                    if (!FGuiyangRemoteLobbyCodec::TryParseBootstrap(Response->GetContentAsString(), Bootstrap))
                    {
                        Self->NotifyFailure(RequestId, nullptr, false, TEXT("大厅启动信息格式无效"));
                        return;
                    }
                    if (UGuiyangLobbySubsystem* OwnerSubsystem = Self->Owner.Get())
                        OwnerSubsystem->HandleRemoteBootstrap(Bootstrap);
                });
            return MakeResult(RequestId, true, EGuiyangLobbyErrorCode::None, TEXT("正在同步大厅信息"));
        }

        virtual FGuiyangLobbyOperationResult QuickStart(
            AGuiyangMahjongPlayerController& PlayerController, const FString& RequestId) override
        {
            FString Token;
            FString PlayerId;
            FGuiyangLobbyOperationResult Rejection;
            if (!ResolveAuthorization(PlayerController, RequestId, Token, PlayerId, Rejection)) return Rejection;
            const TWeakObjectPtr<AGuiyangMahjongPlayerController> Controller(&PlayerController);
            StartRequest(TEXT("GET"), TEXT("/v1/rooms"), Token, RequestId, FString(), false,
                [WeakThis = TWeakPtr<FRemoteLobbyBackend>(AsShared()), Controller, Token, PlayerId, RequestId]
                (FHttpResponsePtr Response, const bool bSucceeded)
                {
                    const TSharedPtr<FRemoteLobbyBackend> Self = WeakThis.Pin();
                    if (!Self || !Controller.IsValid()) return;
                    if (!Self->IsSuccess(Response, bSucceeded))
                    {
                        Self->NotifyFailure(RequestId, Response, bSucceeded, TEXT("无法获取可用房间"));
                        return;
                    }
                    TArray<TSharedPtr<FJsonValue>> Rooms;
                    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Response->GetContentAsString());
                    if (!FJsonSerializer::Deserialize(Reader, Rooms))
                    {
                        Self->NotifyFailure(RequestId, nullptr, false, TEXT("房间目录格式无效"));
                        return;
                    }
                    for (const TSharedPtr<FJsonValue>& Value : Rooms)
                    {
                        const TSharedPtr<FJsonObject> Room = Value.IsValid() ? Value->AsObject() : nullptr;
                        FString RoomCode;
                        FString Lifecycle;
                        bool bPasswordProtected = true;
                        int32 PlayerCount = 0;
                        int32 MaximumPlayers = 0;
                        if (Room.IsValid() && Room->TryGetStringField(TEXT("roomCode"), RoomCode)
                            && Room->TryGetStringField(TEXT("lifecycle"), Lifecycle)
                            && Room->TryGetBoolField(TEXT("passwordProtected"), bPasswordProtected)
                            && Room->TryGetNumberField(TEXT("playerCount"), PlayerCount)
                            && Room->TryGetNumberField(TEXT("maximumPlayers"), MaximumPlayers)
                            && Lifecycle.Equals(TEXT("Waiting"), ESearchCase::IgnoreCase)
                            && !bPasswordProtected && PlayerCount < MaximumPlayers)
                        {
                            FMahjongJoinRoomRequest JoinRequest;
                            JoinRequest.RoomCode = RoomCode;
                            Self->StartJoin(*Controller.Get(), JoinRequest, Token, PlayerId, RequestId);
                            return;
                        }
                    }
                    FMahjongCreateRoomRequest CreateRequest;
                    Self->StartCreate(*Controller.Get(), CreateRequest, Token, PlayerId, RequestId);
                });
            return MakeResult(RequestId, true, EGuiyangLobbyErrorCode::None, TEXT("正在匹配可用房间"));
        }

        virtual FGuiyangLobbyOperationResult CreateRoom(
            AGuiyangMahjongPlayerController& PlayerController, const FMahjongCreateRoomRequest& Request,
            const FString& RequestId) override
        {
            FString Token;
            FString PlayerId;
            FGuiyangLobbyOperationResult Rejection;
            if (!ResolveAuthorization(PlayerController, RequestId, Token, PlayerId, Rejection)) return Rejection;
            StartCreate(PlayerController, Request, Token, PlayerId, RequestId);
            return MakeResult(RequestId, true, EGuiyangLobbyErrorCode::None, TEXT("正在创建并分配牌桌"));
        }

        virtual FGuiyangLobbyOperationResult JoinRoom(
            AGuiyangMahjongPlayerController& PlayerController, const FMahjongJoinRoomRequest& Request,
            const FString& RequestId) override
        {
            FString Token;
            FString PlayerId;
            FGuiyangLobbyOperationResult Rejection;
            if (!ResolveAuthorization(PlayerController, RequestId, Token, PlayerId, Rejection)) return Rejection;
            StartJoin(PlayerController, Request, Token, PlayerId, RequestId);
            return MakeResult(RequestId, true, EGuiyangLobbyErrorCode::None, TEXT("正在验证房间并获取牌桌路由"));
        }

        virtual FGuiyangLobbyOperationResult Reconnect(
            AGuiyangMahjongPlayerController& PlayerController, const FString& RequestId) override
        {
            FString Token;
            FString PlayerId;
            FGuiyangLobbyOperationResult Rejection;
            if (!ResolveAuthorization(PlayerController, RequestId, Token, PlayerId, Rejection)) return Rejection;
            FString RoomId;
            FString MatchId;
            if (const UGuiyangReconnectSubsystem* Reconnect = PlayerController.GetGameInstance()
                ? PlayerController.GetGameInstance()->GetSubsystem<UGuiyangReconnectSubsystem>() : nullptr)
                Reconnect->GetLastRemoteRoute(RoomId, MatchId);
            const TWeakObjectPtr<AGuiyangMahjongPlayerController> WeakController(&PlayerController);
            StartRequest(TEXT("POST"), TEXT("/v1/reconnect/route"), Token, RequestId,
                FGuiyangRemoteLobbyCodec::SerializeReconnectRouteRequest(RoomId, MatchId), true,
                [WeakThis = TWeakPtr<FRemoteLobbyBackend>(AsShared()), WeakController, PlayerId, RequestId]
                (FHttpResponsePtr Response, const bool bSucceeded)
                {
                    const TSharedPtr<FRemoteLobbyBackend> Self = WeakThis.Pin();
                    if (!Self || !WeakController.IsValid()) return;
                    FGuiyangGameServerRoute Route;
                    if (Self->IsSuccess(Response, bSucceeded)
                        && FGuiyangRemoteLobbyCodec::TryParseRoute(
                            Response->GetContentAsString(), PlayerId, Route))
                    {
                        Self->NotifyRoute(WeakController.Get(), Route);
                        return;
                    }
                    if (UGameInstance* GameInstance = WeakController->GetGameInstance())
                        if (UGuiyangReconnectSubsystem* Reconnect =
                            GameInstance->GetSubsystem<UGuiyangReconnectSubsystem>())
                            Reconnect->MarkRetryFailed(TEXT("向大厅申请新入场票据失败，请重试"));
                    Self->NotifyFailure(RequestId, Response, bSucceeded, TEXT("无法获取重连路由"));
                });
            return MakeResult(RequestId, true, EGuiyangLobbyErrorCode::None,
                TEXT("正在向大厅申请新的重连票据"));
        }

        virtual FGuiyangLobbyOperationResult LeaveCurrentRoom(
            AGuiyangMahjongPlayerController& PlayerController,
            const FString& RequestId) override
        {
            FString Token;
            FString PlayerId;
            FGuiyangLobbyOperationResult Rejection;
            if (!ResolveAuthorization(
                    PlayerController, RequestId, Token, PlayerId, Rejection))
            {
                return Rejection;
            }
            const TWeakObjectPtr<AGuiyangMahjongPlayerController> WeakController(
                &PlayerController);
            StartRequest(TEXT("POST"), TEXT("/v1/rooms/current/leave"),
                Token, RequestId, TEXT("{}"), true,
                [WeakThis = TWeakPtr<FRemoteLobbyBackend>(AsShared()),
                 WeakController, RequestId]
                (FHttpResponsePtr Response, const bool bSucceeded)
                {
                    const TSharedPtr<FRemoteLobbyBackend> Self = WeakThis.Pin();
                    if (!Self || !WeakController.IsValid())
                    {
                        return;
                    }
                    if (!Self->IsSuccess(Response, bSucceeded))
                    {
                        Self->NotifyFailure(RequestId, Response, bSucceeded,
                            TEXT("退出远程房间失败"));
                        return;
                    }
                    UE_LOG(LogMahjongNet, Log,
                        TEXT("大厅已释放玩家房间成员关系：RequestId=%s"),
                        *RequestId);
                    WeakController->CompleteRemoteReturnToLobby();
                });
            return MakeResult(RequestId, true,
                EGuiyangLobbyErrorCode::None, TEXT("正在退出房间"));
        }

        virtual FGuiyangLobbyOperationResult CloseOwnedRoom(
            AGuiyangMahjongPlayerController& PlayerController, const FString& RequestId) override
        {
            FString Token;
            FString PlayerId;
            FGuiyangLobbyOperationResult Rejection;
            if (!ResolveAuthorization(PlayerController, RequestId, Token, PlayerId, Rejection)) return Rejection;
            const TWeakObjectPtr<AGuiyangMahjongPlayerController> WeakController(&PlayerController);
            StartRequest(TEXT("POST"), TEXT("/v1/rooms/current/close"), Token, RequestId,
                TEXT("{}"), true,
                [WeakThis = TWeakPtr<FRemoteLobbyBackend>(AsShared()), WeakController, RequestId]
                (FHttpResponsePtr Response, const bool bSucceeded)
                {
                    const TSharedPtr<FRemoteLobbyBackend> Self = WeakThis.Pin();
                    if (!Self || !WeakController.IsValid()) return;
                    if (!Self->IsSuccess(Response, bSucceeded))
                    {
                        Self->NotifyFailure(RequestId, Response, bSucceeded, TEXT("关闭远程房间失败"));
                        return;
                    }
                    UE_LOG(LogMahjongNet, Log, TEXT("房主远程房间已关闭并提交释放：RequestId=%s"), *RequestId);
                    WeakController->CompleteRemoteReturnToLobby();
                });
            return MakeResult(RequestId, true, EGuiyangLobbyErrorCode::None,
                TEXT("正在关闭并释放远程房间"));
        }

    private:
        using FResponseHandler = TFunction<void(FHttpResponsePtr, bool)>;

        bool ResolveAuthorization(AGuiyangMahjongPlayerController& Controller, const FString& RequestId,
            FString& OutToken, FString& OutPlayerId, FGuiyangLobbyOperationResult& OutRejection) const
        {
            const UGuiyangLoginSubsystem* Login = Controller.GetGameInstance()
                ? Controller.GetGameInstance()->GetSubsystem<UGuiyangLoginSubsystem>() : nullptr;
            if (!Login || !Login->IsSessionValid())
            {
                OutRejection = MakeResult(RequestId, false, EGuiyangLobbyErrorCode::SessionExpired,
                    TEXT("登录会话无效，请重新登录"));
                return false;
            }
            OutToken = Login->GetSessionTokenForNetwork();
            OutPlayerId = Login->GetCurrentProfile().PlayerId;
            if (OutToken.IsEmpty() || OutToken.Len() > 4096 || OutPlayerId.IsEmpty())
            {
                OutRejection = MakeResult(RequestId, false, EGuiyangLobbyErrorCode::SessionExpired,
                    TEXT("登录凭据格式无效"));
                return false;
            }
            return true;
        }

        void StartCreate(AGuiyangMahjongPlayerController& Controller, const FMahjongCreateRoomRequest& Request,
            const FString& Token, const FString& PlayerId, const FString& RequestId)
        {
            const TWeakObjectPtr<AGuiyangMahjongPlayerController> WeakController(&Controller);
            StartRequest(TEXT("POST"), TEXT("/v1/rooms"), Token, RequestId,
                FGuiyangRemoteLobbyCodec::SerializeCreateRoom(Request), true,
                [WeakThis = TWeakPtr<FRemoteLobbyBackend>(AsShared()), WeakController, Token, PlayerId, RequestId]
                (FHttpResponsePtr Response, const bool bSucceeded)
                {
                    const TSharedPtr<FRemoteLobbyBackend> Self = WeakThis.Pin();
                    if (!Self || !WeakController.IsValid()) return;
                    if (!Self->IsSuccess(Response, bSucceeded))
                    {
                        TSharedPtr<FJsonObject> ErrorObject;
                        FString StableCode;
                        if (Response.IsValid() && Response->GetResponseCode() == 409
                            && DeserializeObject(Response->GetContentAsString(), ErrorObject)
                            && ErrorObject->TryGetStringField(TEXT("code"), StableCode)
                            && StableCode.Equals(TEXT("REQUEST_IN_PROGRESS"), ESearchCase::IgnoreCase))
                        {
                            const FString RecoveryRequestId = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
                            UE_LOG(LogMahjongNet, Log,
                                TEXT("创建房间时发现当前玩家已有活动牌桌，自动恢复该房间：CreateRequestId=%s RecoveryRequestId=%s"),
                                *RequestId, *RecoveryRequestId);
                            Self->Reconnect(*WeakController.Get(), RecoveryRequestId);
                            return;
                        }
                        Self->NotifyFailure(RequestId, Response, bSucceeded, TEXT("创建房间失败"));
                        return;
                    }
                    FString RoomCode;
                    if (!TryGetOperationRoomCode(Response->GetContentAsString(), RoomCode))
                    {
                        Self->NotifyFailure(RequestId, nullptr, false, TEXT("创建房间应答格式无效"));
                        return;
                    }
                    Self->PollRoute(WeakController, Token, PlayerId, RequestId, RoomCode, 0);
                });
        }

        void StartJoin(AGuiyangMahjongPlayerController& Controller, const FMahjongJoinRoomRequest& Request,
            const FString& Token, const FString& PlayerId, const FString& RequestId)
        {
            const TSharedRef<FJsonObject> Body = MakeShared<FJsonObject>();
            Body->SetStringField(TEXT("password"), Request.Password);
            Body->SetNumberField(TEXT("clientProtocolVersion"), Request.ClientProtocolVersion);
            const TWeakObjectPtr<AGuiyangMahjongPlayerController> WeakController(&Controller);
            StartRequest(TEXT("POST"), FString::Printf(TEXT("/v1/rooms/%s/join"), *Request.RoomCode),
                Token, RequestId, SerializeObject(Body), true,
                [WeakThis = TWeakPtr<FRemoteLobbyBackend>(AsShared()), WeakController, Token, PlayerId,
                    RequestId, RoomCode = Request.RoomCode](FHttpResponsePtr Response, const bool bSucceeded)
                {
                    const TSharedPtr<FRemoteLobbyBackend> Self = WeakThis.Pin();
                    if (!Self || !WeakController.IsValid()) return;
                    if (!Self->IsSuccess(Response, bSucceeded))
                    {
                        Self->NotifyFailure(RequestId, Response, bSucceeded, TEXT("加入房间失败"));
                        return;
                    }
                    FGuiyangGameServerRoute Route;
                    if (FGuiyangRemoteLobbyCodec::TryParseRoute(Response->GetContentAsString(), PlayerId, Route))
                    {
                        Self->NotifyRoute(WeakController.Get(), Route);
                        return;
                    }
                    Self->PollRoute(WeakController, Token, PlayerId, RequestId, RoomCode, 0);
                });
        }

        void PollRoute(const TWeakObjectPtr<AGuiyangMahjongPlayerController>& Controller,
            const FString& Token, const FString& PlayerId, const FString& RequestId,
            const FString& RoomCode, const int32 Attempt)
        {
            if (!Controller.IsValid()) return;
            if (Attempt >= Settings.RoutePollMaxAttempts)
            {
                NotifyFailure(RequestId, nullptr, false, TEXT("牌桌分配超时，请稍后重试"), EGuiyangLobbyErrorCode::Timeout);
                return;
            }
            StartRequest(TEXT("GET"), FString::Printf(TEXT("/v1/rooms/%s/route"), *RoomCode),
                Token, RequestId, FString(), false,
                [WeakThis = TWeakPtr<FRemoteLobbyBackend>(AsShared()), Controller, Token, PlayerId,
                    RequestId, RoomCode, Attempt](FHttpResponsePtr Response, const bool bSucceeded)
                {
                    const TSharedPtr<FRemoteLobbyBackend> Self = WeakThis.Pin();
                    if (!Self || !Controller.IsValid()) return;
                    FGuiyangGameServerRoute Route;
                    if (Self->IsSuccess(Response, bSucceeded)
                        && FGuiyangRemoteLobbyCodec::TryParseRoute(Response->GetContentAsString(), PlayerId, Route))
                    {
                        Self->NotifyRoute(Controller.Get(), Route);
                        return;
                    }
                    const int32 Status = Response.IsValid() ? Response->GetResponseCode() : 0;
                    if (Status == 409 || Status == 425 || Status == 503)
                    {
                        Self->ScheduleRoutePoll(Controller, Token, PlayerId, RequestId, RoomCode, Attempt + 1);
                        return;
                    }
                    Self->NotifyFailure(RequestId, Response, bSucceeded, TEXT("无法获取牌桌路由"));
                });
        }

        void ScheduleRoutePoll(const TWeakObjectPtr<AGuiyangMahjongPlayerController>& Controller,
            const FString& Token, const FString& PlayerId, const FString& RequestId,
            const FString& RoomCode, const int32 Attempt)
        {
            FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateLambda(
                [WeakThis = TWeakPtr<FRemoteLobbyBackend>(AsShared()), Controller, Token, PlayerId,
                    RequestId, RoomCode, Attempt](float)
                {
                    if (const TSharedPtr<FRemoteLobbyBackend> Self = WeakThis.Pin())
                        Self->PollRoute(Controller, Token, PlayerId, RequestId, RoomCode, Attempt);
                    return false;
                }), Settings.RoutePollIntervalSeconds);
        }

        void StartRequest(const FString& Verb, const FString& Path, const FString& Token,
            const FString& RequestId, const FString& Body, const bool bIdempotent,
            FResponseHandler&& Handler) const
        {
            const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Request = FHttpModule::Get().CreateRequest();
            Request->SetURL(
                Settings.PlatformEndpoints.BuildApiUrl(Path));
            Request->SetVerb(Verb);
            Request->SetHeader(TEXT("Accept"), TEXT("application/json"));
            Request->SetHeader(TEXT("Authorization"), TEXT("Bearer ") + Token);
            Settings.PlatformEndpoints.ApplyStandardHeaders(
                Request.Get(),
                RequestId);
            if (bIdempotent) Request->SetHeader(TEXT("Idempotency-Key"), RequestId);
            if (!Body.IsEmpty())
            {
                Request->SetHeader(TEXT("Content-Type"), TEXT("application/json"));
                Request->SetContentAsString(Body);
            }
            Request->SetTimeout(Settings.RequestTimeoutSeconds);
            const TSharedRef<FResponseHandler, ESPMode::ThreadSafe> Completion =
                MakeShared<FResponseHandler, ESPMode::ThreadSafe>(MoveTemp(Handler));
            Request->OnProcessRequestComplete().BindLambda(
                [Completion](FHttpRequestPtr, FHttpResponsePtr Response, const bool bSucceeded)
                {
                    if (*Completion)
                    {
                        FResponseHandler HandlerOnce = MoveTemp(*Completion);
                        HandlerOnce(Response, bSucceeded);
                    }
                });
            if (!Request->ProcessRequest() && *Completion)
            {
                FResponseHandler HandlerOnce = MoveTemp(*Completion);
                HandlerOnce(nullptr, false);
            }
        }

        bool IsSuccess(const FHttpResponsePtr& Response, const bool bSucceeded) const
        {
            return bSucceeded && Response.IsValid()
                && Response->GetResponseCode() >= 200 && Response->GetResponseCode() < 300;
        }

        void NotifyRoute(AGuiyangMahjongPlayerController* Controller, const FGuiyangGameServerRoute& Route) const
        {
            if (UGuiyangLobbySubsystem* OwnerSubsystem = Owner.Get())
                OwnerSubsystem->HandleRemoteRouteReady(Controller, Route);
        }

        void NotifyFailure(const FString& RequestId, const FHttpResponsePtr& Response,
            const bool bSucceeded, const FString& DefaultMessage,
            EGuiyangLobbyErrorCode DefaultCode = EGuiyangLobbyErrorCode::ServerUnavailable) const
        {
            EGuiyangLobbyErrorCode ErrorCode = DefaultCode;
            FString Message = DefaultMessage;
            FString ResponseRequestId;
            TSharedPtr<FJsonObject> ErrorObject;
            if (Response.IsValid() && DeserializeObject(Response->GetContentAsString(), ErrorObject))
            {
                FString StableCode;
                if (ErrorObject->TryGetStringField(TEXT("code"), StableCode))
                    ErrorCode = FGuiyangRemoteLobbyCodec::MapErrorCode(StableCode);
                ErrorObject->TryGetStringField(TEXT("message"), Message);
                ErrorObject->TryGetStringField(TEXT("requestId"), ResponseRequestId);
            }
            if (!bSucceeded && !Response.IsValid()) ErrorCode = EGuiyangLobbyErrorCode::Timeout;
            Message = Message.Left(256);
            if (Response.IsValid() && Response->GetResponseCode() == 401)
            {
                ErrorCode = EGuiyangLobbyErrorCode::SessionExpired;
                if (UGuiyangLobbySubsystem* OwnerSubsystem = Owner.Get())
                {
                    if (UGameInstance* GameInstance = OwnerSubsystem->GetGameInstance())
                        if (UGuiyangLoginSubsystem* Login = GameInstance->GetSubsystem<UGuiyangLoginSubsystem>())
                            Login->ExpireSession(Message);
                }
            }
            if (UGuiyangLobbySubsystem* OwnerSubsystem = Owner.Get())
                OwnerSubsystem->HandleRemoteFailure(
                    ResponseRequestId.IsEmpty() ? RequestId : ResponseRequestId, ErrorCode, Message);
        }

        TWeakObjectPtr<UGuiyangLobbySubsystem> Owner;
        FGuiyangRemoteLobbySettings Settings;
    };
}

bool FGuiyangRemoteLobbyCodec::NormalizeBaseUrl(const FString& Value, FString& OutBaseUrl)
{
    // 测试入口与生产统一使用同一 HTTPS/loopback HTTP 校验，防止两套规则长期漂移。
    return FGuiyangPlatformEndpointSettings::NormalizeHttpBaseUrl(
        Value,
        false,
        OutBaseUrl);
}

FString FGuiyangRemoteLobbyCodec::SerializeCreateRoom(const FMahjongCreateRoomRequest& Request)
{
    // 密码只写入本次 TLS 请求体，不进入日志、Bootstrap 或本地缓存。
    const FMahjongRuleConfig& Config = Request.Rules;
    const TSharedRef<FJsonObject> Rules = MakeShared<FJsonObject>();
    Rules->SetStringField(TEXT("ruleId"), Config.RuleId.ToString());
    Rules->SetNumberField(TEXT("ruleVersion"), Config.RuleVersion);
    Rules->SetStringField(TEXT("tileSetMode"), TEXT("Suited108"));
    Rules->SetStringField(TEXT("jiCountingScope"), GuiyangRemoteLobbyPrivate::JiScopeName(Config.JiCountingScope));
#define WRITE_RULE_INT(JsonName, Member) Rules->SetNumberField(TEXT(JsonName), Config.Member)
#define WRITE_RULE_BOOL(JsonName, Member) Rules->SetBoolField(TEXT(JsonName), Config.Member)
    WRITE_RULE_INT("baseScore", BaseScore);
    WRITE_RULE_INT("jiScore", JiScore);
    WRITE_RULE_INT("basicJiValue", BasicJiValue);
    WRITE_RULE_INT("flippedJiValue", FlippedJiValue);
    WRITE_RULE_INT("wuGuJiValue", WuGuJiValue);
    WRITE_RULE_INT("chongFengJiValue", ChongFengJiValue);
    WRITE_RULE_INT("wuGuChongFengJiValue", WuGuChongFengJiValue);
    WRITE_RULE_INT("zeRenJiValue", ZeRenJiValue);
    WRITE_RULE_INT("wuGuZeRenJiValue", WuGuZeRenJiValue);
    WRITE_RULE_INT("gangScore", GangScore);
    WRITE_RULE_INT("ziMoMultiplier", ZiMoMultiplier);
    WRITE_RULE_INT("dianPaoMultiplier", DianPaoMultiplier);
    WRITE_RULE_INT("reconnectTimeoutSeconds", ReconnectTimeoutSeconds);
    WRITE_RULE_INT("turnTimeoutSeconds", TurnTimeoutSeconds);
    WRITE_RULE_INT("reactionTimeoutSeconds", ReactionTimeoutSeconds);
    WRITE_RULE_BOOL("enableChongFengJi", bEnableChongFengJi);
    WRITE_RULE_BOOL("enableZeRenJi", bEnableZeRenJi);
    WRITE_RULE_BOOL("enableWuGuJi", bEnableWuGuJi);
    WRITE_RULE_BOOL("wuGuCanChongFeng", bWuGuCanChongFeng);
    WRITE_RULE_BOOL("wuGuCanZeRen", bWuGuCanZeRen);
    WRITE_RULE_BOOL("enableQiangGangHu", bEnableQiangGangHu);
    // 通行证规则必须随建房请求进入冻结快照，避免客户端显示允许点炮而牌桌服采用不同判定。
    WRITE_RULE_BOOL("requireGangForDiscardHu", bRequireGangForDiscardHu);
    WRITE_RULE_BOOL("enableYiPaoDuoXiang", bEnableYiPaoDuoXiang);
    WRITE_RULE_BOOL("enableQiDui", bEnableQiDui);
    WRITE_RULE_BOOL("drawGameDealerContinues", bDrawGameDealerContinues);
    WRITE_RULE_BOOL("enableTimeoutAutoPlay", bEnableTimeoutAutoPlay);
#undef WRITE_RULE_BOOL
#undef WRITE_RULE_INT

    const TSharedRef<FJsonObject> Body = MakeShared<FJsonObject>();
    Body->SetNumberField(TEXT("roundCount"), Request.RoundCount);
    Body->SetBoolField(TEXT("publicRoom"), Request.bPublicRoom);
    Body->SetBoolField(TEXT("autoStart"), Request.bAutoStart);
    Body->SetBoolField(TEXT("passwordProtected"), Request.bEnablePassword);
    if (Request.bEnablePassword) Body->SetStringField(TEXT("password"), Request.Password);
    else Body->SetField(TEXT("password"), MakeShared<FJsonValueNull>());
    Body->SetObjectField(TEXT("ruleSnapshot"), Rules);
    return GuiyangRemoteLobbyPrivate::SerializeObject(Body);
}

FString FGuiyangRemoteLobbyCodec::SerializeReconnectRouteRequest(
    const FString& RoomId, const FString& MatchId)
{
    const TSharedRef<FJsonObject> Body = MakeShared<FJsonObject>();
    if (!RoomId.IsEmpty()) Body->SetStringField(TEXT("roomId"), RoomId);
    if (!MatchId.IsEmpty()) Body->SetStringField(TEXT("matchId"), MatchId);
    return GuiyangRemoteLobbyPrivate::SerializeObject(Body);
}

bool FGuiyangRemoteLobbyCodec::TryParseBootstrap(const FString& Json, FGuiyangLobbyBootstrap& OutBootstrap)
{
    // 严格解析登录后大厅 Bootstrap，缺少稳定字段时整体拒绝。
    OutBootstrap = FGuiyangLobbyBootstrap();
    TSharedPtr<FJsonObject> Object;
    const TArray<TSharedPtr<FJsonValue>>* Announcements = nullptr;
    if (!GuiyangRemoteLobbyPrivate::DeserializeObject(Json, Object)
        || !Object->TryGetStringField(TEXT("requestId"), OutBootstrap.RequestId)
        || !Object->TryGetStringField(TEXT("playerId"), OutBootstrap.PlayerId)
        || !Object->TryGetStringField(TEXT("displayName"), OutBootstrap.DisplayName)
        || !Object->TryGetNumberField(TEXT("onlinePlayerCount"), OutBootstrap.OnlinePlayerCount)
        || !Object->TryGetNumberField(TEXT("protocolVersion"), OutBootstrap.ProtocolVersion)
        || !Object->TryGetArrayField(TEXT("announcements"), Announcements)
        || OutBootstrap.PlayerId.IsEmpty() || OutBootstrap.PlayerId.Len() > 80
        || OutBootstrap.DisplayName.IsEmpty() || OutBootstrap.DisplayName.Len() > 24
        || OutBootstrap.OnlinePlayerCount < 0 || OutBootstrap.ProtocolVersion != 1)
        return false;
    for (const TSharedPtr<FJsonValue>& Value : *Announcements)
    {
        FString Announcement;
        if (!Value.IsValid() || !Value->TryGetString(Announcement) || Announcement.Len() > 256) return false;
        OutBootstrap.Announcements.Add(MoveTemp(Announcement));
    }
    return OutBootstrap.Announcements.Num() <= 32;
}

bool FGuiyangRemoteLobbyCodec::TryParseRoute(const FString& Json, const FString& ExpectedPlayerId,
    FGuiyangGameServerRoute& OutRoute)
{
    // 路由必须绑定当前玩家，并包含有效地址、端口、房间/比赛 ID 和一次性票据。
    OutRoute = FGuiyangGameServerRoute();
    TSharedPtr<FJsonObject> Object;
    FString ExpireAt;
    if (!GuiyangRemoteLobbyPrivate::DeserializeObject(Json, Object)
        || !Object->TryGetStringField(TEXT("requestId"), OutRoute.RequestId)
        || !Object->TryGetStringField(TEXT("playerId"), OutRoute.PlayerId)
        || !Object->TryGetStringField(TEXT("roomId"), OutRoute.RoomId)
        || !Object->TryGetStringField(TEXT("serverInstanceId"), OutRoute.ServerInstanceId)
        || !Object->TryGetStringField(TEXT("matchId"), OutRoute.MatchId)
        || !Object->TryGetStringField(TEXT("serverIp"), OutRoute.ServerIP)
        || !Object->TryGetNumberField(TEXT("serverPort"), OutRoute.ServerPort)
        || !Object->TryGetStringField(TEXT("joinTicket"), OutRoute.JoinTicket)
        || !Object->TryGetStringField(TEXT("ticketExpireAtUtc"), ExpireAt)
        || OutRoute.PlayerId != ExpectedPlayerId
        || OutRoute.JoinTicket.Len() < 32 || OutRoute.JoinTicket.Len() > 4096
        || !OutRoute.HasValidEndpoint()
        || !FDateTime::ParseIso8601(*ExpireAt, OutRoute.TicketExpireAtUtc)
        || OutRoute.TicketExpireAtUtc <= FDateTime::UtcNow())
        return false;
    FGuid Guid;
    return FGuid::Parse(OutRoute.RoomId, Guid)
        && FGuid::Parse(OutRoute.ServerInstanceId, Guid)
        && FGuid::Parse(OutRoute.MatchId, Guid);
}

EGuiyangLobbyErrorCode FGuiyangRemoteLobbyCodec::MapErrorCode(const FString& StableCode)
{
    static const TMap<FString, EGuiyangLobbyErrorCode> Codes = {
        {TEXT("INVALID_REQUEST"), EGuiyangLobbyErrorCode::InvalidRequest},
        {TEXT("SESSION_EXPIRED"), EGuiyangLobbyErrorCode::SessionExpired},
        {TEXT("REQUEST_IN_PROGRESS"), EGuiyangLobbyErrorCode::RequestInProgress},
        {TEXT("ROOM_NOT_FOUND"), EGuiyangLobbyErrorCode::RoomNotFound},
        {TEXT("ROOM_FULL"), EGuiyangLobbyErrorCode::RoomFull},
        {TEXT("ROOM_CLOSED"), EGuiyangLobbyErrorCode::RoomClosed},
        {TEXT("PASSWORD_REQUIRED"), EGuiyangLobbyErrorCode::PasswordRequired},
        {TEXT("WRONG_PASSWORD"), EGuiyangLobbyErrorCode::WrongPassword},
        {TEXT("RATE_LIMITED"), EGuiyangLobbyErrorCode::RateLimited},
        {TEXT("SERVER_UNAVAILABLE"), EGuiyangLobbyErrorCode::ServerUnavailable},
        {TEXT("TICKET_EXPIRED"), EGuiyangLobbyErrorCode::TicketExpired},
        {TEXT("VERSION_MISMATCH"), EGuiyangLobbyErrorCode::VersionMismatch},
        {TEXT("TIMEOUT"), EGuiyangLobbyErrorCode::Timeout},
        {TEXT("CANCELLED"), EGuiyangLobbyErrorCode::Cancelled},
        {TEXT("BACKEND_NOT_CONFIGURED"), EGuiyangLobbyErrorCode::BackendNotConfigured}
    };
    if (const EGuiyangLobbyErrorCode* Code = Codes.Find(StableCode.ToUpper())) return *Code;
    return EGuiyangLobbyErrorCode::InternalError;
}

TSharedPtr<ILobbyBackend> CreateRemoteLobbyBackend(
    UGuiyangLobbySubsystem& Owner, const FGuiyangRemoteLobbySettings& Settings)
{
    return MakeShared<GuiyangRemoteLobbyPrivate::FRemoteLobbyBackend>(Owner, Settings);
}
