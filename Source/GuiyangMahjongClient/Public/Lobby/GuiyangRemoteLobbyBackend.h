#pragma once

#include "CoreMinimal.h"
#include "Config/GuiyangPlatformEndpointSettings.h"
#include "Lobby/GuiyangLobbyBackend.h"

class FJsonObject;
class UGuiyangLobbySubsystem;
struct FMahjongCreateRoomRequest;

struct GUIYANGMAHJONGCLIENT_API FGuiyangRemoteLobbySettings
{
    /** 统一网关端点及客户端契约头；旧直连状态由该结构内部标记。 */
    FGuiyangPlatformEndpointSettings PlatformEndpoints;
    float RequestTimeoutSeconds = 10.0f;
    float RoutePollIntervalSeconds = 0.25f;
    int32 RoutePollMaxAttempts = 120;
};

/** 无网络副作用的 RemoteLobby v1 编解码器，供运行时和契约测试共用。 */
struct GUIYANGMAHJONGCLIENT_API FGuiyangRemoteLobbyCodec
{
    /** 兼容旧自动化测试的纯地址规范化入口；新运行时使用统一平台端点。 */
    static bool NormalizeBaseUrl(const FString& Value, FString& OutBaseUrl);
    static FString SerializeCreateRoom(const FMahjongCreateRoomRequest& Request);
    static FString SerializeReconnectRouteRequest(const FString& RoomId, const FString& MatchId);
    static bool TryParseBootstrap(const FString& Json, FGuiyangLobbyBootstrap& OutBootstrap);
    static bool TryParseRoute(const FString& Json, const FString& ExpectedPlayerId,
        FGuiyangGameServerRoute& OutRoute);
    static EGuiyangLobbyErrorCode MapErrorCode(const FString& StableCode);
};

GUIYANGMAHJONGCLIENT_API TSharedPtr<ILobbyBackend> CreateRemoteLobbyBackend(
    UGuiyangLobbySubsystem& Owner, const FGuiyangRemoteLobbySettings& Settings);
