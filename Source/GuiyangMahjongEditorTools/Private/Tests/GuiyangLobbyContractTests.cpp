#include "Misc/AutomationTest.h"


#include "Lobby/GuiyangLobbySubsystem.h"
#include "Lobby/GuiyangLobbyTypes.h"
#include "Lobby/GuiyangRemoteLobbyBackend.h"
#include "Config/GuiyangPlatformEndpointSettings.h"
#include "Network/MahjongNetworkTypes.h"
#include "Dom/JsonObject.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

#if WITH_DEV_AUTOMATION_TESTS

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FGuiyangLobbyBackendModeContractTest,
    "GuiyangMahjong.Lobby.BackendModeContract",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::EngineFilter)

bool FGuiyangLobbyBackendModeContractTest::RunTest(const FString& Parameters)
{
    EGuiyangLobbyBackendMode Mode = EGuiyangLobbyBackendMode::RemoteLobby;
    TestTrue(TEXT("必须识别 LocalLegacy"),
        UGuiyangLobbySubsystem::TryParseBackendMode(TEXT("LocalLegacy"), Mode));
    TestEqual(TEXT("LocalLegacy 映射必须稳定"), Mode, EGuiyangLobbyBackendMode::LocalLegacy);

    TestTrue(TEXT("模式解析必须忽略大小写和两端空白"),
        UGuiyangLobbySubsystem::TryParseBackendMode(TEXT("  remotelobby  "), Mode));
    TestEqual(TEXT("RemoteLobby 映射必须稳定"), Mode, EGuiyangLobbyBackendMode::RemoteLobby);

    TestFalse(TEXT("未知模式必须被拒绝"),
        UGuiyangLobbySubsystem::TryParseBackendMode(TEXT("SilentFallback"), Mode));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FGuiyangLobbyDtoContractTest,
    "GuiyangMahjong.Lobby.DtoContract",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::EngineFilter)

bool FGuiyangLobbyDtoContractTest::RunTest(const FString& Parameters)
{
    FGuiyangGameServerRoute Route;
    Route.ServerIP = TEXT("127.0.0.1");
    Route.ServerPort = 7777;
    TestTrue(TEXT("合法服务器端点必须可用"), Route.HasValidEndpoint());

    Route.ServerPort = 0;
    TestFalse(TEXT("端口 0 必须被拒绝"), Route.HasValidEndpoint());
    Route.ServerPort = 7777;
    Route.ServerIP.Reset();
    TestFalse(TEXT("空服务器地址必须被拒绝"), Route.HasValidEndpoint());

    FGuiyangLobbyOperationResult Result;
    TestFalse(TEXT("请求结果默认必须为未接受"), Result.bAccepted);
    TestEqual(TEXT("请求结果默认错误码必须为 None"), Result.ErrorCode, EGuiyangLobbyErrorCode::None);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FGuiyangRemoteLobbyCodecTest,
    "GuiyangMahjong.Lobby.RemoteCodec",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::EngineFilter)

bool FGuiyangRemoteLobbyCodecTest::RunTest(const FString& Parameters)
{
    FString BaseUrl;
    TestTrue(TEXT("HTTPS 大厅地址应通过"),
        FGuiyangRemoteLobbyCodec::NormalizeBaseUrl(TEXT(" https://lobby.example.com/ "), BaseUrl));
    TestEqual(TEXT("地址尾斜杠应规范化"), BaseUrl, FString(TEXT("https://lobby.example.com")));
    TestTrue(TEXT("本机 HTTP 允许用于开发"),
        FGuiyangRemoteLobbyCodec::NormalizeBaseUrl(TEXT("http://127.0.0.1:18080"), BaseUrl));
    TestFalse(TEXT("局域网明文 HTTP 必须拒绝"),
        FGuiyangRemoteLobbyCodec::NormalizeBaseUrl(TEXT("http://192.168.1.8:18080"), BaseUrl));
    TestFalse(TEXT("带用户信息的 URL 必须拒绝"),
        FGuiyangRemoteLobbyCodec::NormalizeBaseUrl(TEXT("https://user@lobby.example.com"), BaseUrl));

    FGuiyangPlatformEndpointSettings EdgeSettings;
    EdgeSettings.ApiBaseUrl = TEXT("https://api.example.com");
    TestEqual(
        TEXT("大厅外部路径必须增加 /api 前缀"),
        EdgeSettings.BuildApiUrl(TEXT("/v1/lobby/bootstrap")),
        FString(TEXT("https://api.example.com/api/v1/lobby/bootstrap")));
    TestEqual(
        TEXT("重连路径必须映射到外部 game 分组"),
        EdgeSettings.BuildApiUrl(TEXT("/v1/reconnect/route")),
        FString(TEXT("https://api.example.com/api/v1/game/reconnect/route")));
    EdgeSettings.bUsingLegacyDirectEndpoint = true;
    TestEqual(
        TEXT("旧直连回滚路径必须保持原 v1 契约"),
        EdgeSettings.BuildApiUrl(TEXT("/v1/reconnect/route")),
        FString(TEXT("https://api.example.com/v1/reconnect/route")));

    FMahjongCreateRoomRequest Request;
    Request.RoundCount = 8;
    Request.bPublicRoom = false;
    Request.bAutoStart = false;
    Request.bEnablePassword = true;
    Request.Password = TEXT("secret12");
    Request.Rules.BaseScore = 3;
    Request.Rules.TurnTimeoutSeconds = 21;
    TSharedPtr<FJsonObject> Serialized;
    const TSharedRef<TJsonReader<>> CreateReader = TJsonReaderFactory<>::Create(
        FGuiyangRemoteLobbyCodec::SerializeCreateRoom(Request));
    TestTrue(TEXT("建房 JSON 必须可解析"), FJsonSerializer::Deserialize(CreateReader, Serialized));
    const TSharedPtr<FJsonObject>* Rules = nullptr;
    TestTrue(TEXT("建房 JSON 必须包含规则快照"),
        Serialized.IsValid() && Serialized->TryGetObjectField(TEXT("ruleSnapshot"), Rules));
    TestFalse(TEXT("autoStart=false 必须保持"), Serialized->GetBoolField(TEXT("autoStart")));
    TestEqual(TEXT("密码必须只存在于请求 JSON"), Serialized->GetStringField(TEXT("password")), FString(TEXT("secret12")));
    if (Rules && Rules->IsValid())
    {
        TestEqual(TEXT("分数规则必须保持"), static_cast<int32>((*Rules)->GetNumberField(TEXT("baseScore"))), 3);
        TestEqual(TEXT("出牌超时必须保持"), static_cast<int32>((*Rules)->GetNumberField(TEXT("turnTimeoutSeconds"))), 21);
    }

    const FString ReconnectJson = FGuiyangRemoteLobbyCodec::SerializeReconnectRouteRequest(
        TEXT("11111111-1111-1111-1111-111111111111"),
        TEXT("33333333-3333-3333-3333-333333333333"));
    TSharedPtr<FJsonObject> ReconnectObject;
    const TSharedRef<TJsonReader<>> ReconnectReader = TJsonReaderFactory<>::Create(ReconnectJson);
    TestTrue(TEXT("重连提示 JSON 必须可解析"),
        FJsonSerializer::Deserialize(ReconnectReader, ReconnectObject));
    TestEqual(TEXT("重连提示保留房间标识"),
        ReconnectObject->GetStringField(TEXT("roomId")),
        FString(TEXT("11111111-1111-1111-1111-111111111111")));
    TestFalse(TEXT("重连请求不得携带旧 JoinTicket"), ReconnectObject->HasField(TEXT("joinTicket")));

    const FString BootstrapJson = TEXT("{\"requestId\":\"request-1\",\"playerId\":\"player-1\",")
        TEXT("\"displayName\":\"玩家一\",\"onlinePlayerCount\":12,\"announcements\":[\"欢迎\"],\"protocolVersion\":1}");
    FGuiyangLobbyBootstrap Bootstrap;
    TestTrue(TEXT("有效大厅启动信息应解析"),
        FGuiyangRemoteLobbyCodec::TryParseBootstrap(BootstrapJson, Bootstrap));
    TestEqual(TEXT("在线人数必须保持"), Bootstrap.OnlinePlayerCount, 12);

    const FString ExpireAt = (FDateTime::UtcNow() + FTimespan::FromMinutes(2)).ToIso8601();
    const TSharedRef<FJsonObject> RouteObject = MakeShared<FJsonObject>();
    RouteObject->SetStringField(TEXT("requestId"), TEXT("request-2"));
    RouteObject->SetStringField(TEXT("playerId"), TEXT("player-1"));
    RouteObject->SetStringField(TEXT("roomId"), TEXT("11111111-1111-1111-1111-111111111111"));
    RouteObject->SetStringField(TEXT("serverInstanceId"), TEXT("22222222-2222-2222-2222-222222222222"));
    RouteObject->SetStringField(TEXT("matchId"), TEXT("33333333-3333-3333-3333-333333333333"));
    RouteObject->SetStringField(TEXT("serverIp"), TEXT("127.0.0.1"));
    RouteObject->SetNumberField(TEXT("serverPort"), 19000);
    RouteObject->SetStringField(TEXT("joinTicket"), FString::ChrN(64, TEXT('a')));
    RouteObject->SetStringField(TEXT("ticketExpireAtUtc"), ExpireAt);
    FString RouteJson;
    const TSharedRef<TJsonWriter<>> RouteWriter = TJsonWriterFactory<>::Create(&RouteJson);
    FJsonSerializer::Serialize(RouteObject, RouteWriter);
    FGuiyangGameServerRoute Route;
    TestTrue(TEXT("范围正确的牌桌路由应解析"),
        FGuiyangRemoteLobbyCodec::TryParseRoute(RouteJson, TEXT("player-1"), Route));
    TestFalse(TEXT("路由不得切换玩家身份"),
        FGuiyangRemoteLobbyCodec::TryParseRoute(RouteJson, TEXT("player-2"), Route));
    RouteObject->SetStringField(TEXT("serverIp"), TEXT("127.0.0.1?JoinTicket=evil"));
    RouteJson.Reset();
    const TSharedRef<TJsonWriter<>> UnsafeWriter = TJsonWriterFactory<>::Create(&RouteJson);
    FJsonSerializer::Serialize(RouteObject, UnsafeWriter);
    TestFalse(TEXT("可注入 Travel URL 的端点必须拒绝"),
        FGuiyangRemoteLobbyCodec::TryParseRoute(RouteJson, TEXT("player-1"), Route));

    TestEqual(TEXT("稳定错误码必须映射"),
        FGuiyangRemoteLobbyCodec::MapErrorCode(TEXT("ROOM_FULL")), EGuiyangLobbyErrorCode::RoomFull);
    TestEqual(TEXT("未知错误码必须收敛为内部错误"),
        FGuiyangRemoteLobbyCodec::MapErrorCode(TEXT("NEW_SERVER_CODE")), EGuiyangLobbyErrorCode::InternalError);
    return true;
}

#endif
