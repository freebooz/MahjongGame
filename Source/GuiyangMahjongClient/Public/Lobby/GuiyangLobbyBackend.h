#pragma once

#include "CoreMinimal.h"
#include "Lobby/GuiyangLobbyTypes.h"

class AGuiyangMahjongPlayerController;
struct FMahjongCreateRoomRequest;
struct FMahjongJoinRoomRequest;

/**
 * 大厅实现边界。UI 只依赖 UGuiyangLobbySubsystem，不直接判断本地 RPC 或远程服务。
 * 实现不得记录房间密码、会话 Token 或 JoinTicket。
 */
class GUIYANGMAHJONGCLIENT_API ILobbyBackend
{
public:
    virtual ~ILobbyBackend() = default;

    virtual EGuiyangLobbyBackendMode GetMode() const = 0;
    virtual FGuiyangLobbyOperationResult Bootstrap(
        AGuiyangMahjongPlayerController& PlayerController, const FString& RequestId) = 0;
    virtual FGuiyangLobbyOperationResult QuickStart(
        AGuiyangMahjongPlayerController& PlayerController, const FString& RequestId) = 0;
    virtual FGuiyangLobbyOperationResult CreateRoom(
        AGuiyangMahjongPlayerController& PlayerController, const FMahjongCreateRoomRequest& Request,
        const FString& RequestId) = 0;
    virtual FGuiyangLobbyOperationResult JoinRoom(
        AGuiyangMahjongPlayerController& PlayerController, const FMahjongJoinRoomRequest& Request,
        const FString& RequestId) = 0;
    virtual FGuiyangLobbyOperationResult Reconnect(
        AGuiyangMahjongPlayerController& PlayerController, const FString& RequestId) = 0;
    virtual FGuiyangLobbyOperationResult LeaveCurrentRoom(
        AGuiyangMahjongPlayerController& PlayerController, const FString& RequestId) = 0;
    virtual FGuiyangLobbyOperationResult CloseOwnedRoom(
        AGuiyangMahjongPlayerController& PlayerController, const FString& RequestId) = 0;
};
