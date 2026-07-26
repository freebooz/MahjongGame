#pragma once

#include "CoreMinimal.h"
#include "Auth/GuiyangLoginTypes.h"
#include "Core/MahjongTypes.h"
#include "Network/MahjongNetworkTypes.h"
#include "GuiyangServerRequestHandler.generated.h"

class AGuiyangMahjongPlayerController;

UINTERFACE(MinimalAPI)
class UGuiyangServerRequestHandler : public UInterface
{
    GENERATED_BODY()
};

/** 共享 PlayerController 向服务端权威 GameMode 转发请求的目标接口。 */
class GUIYANGMAHJONG_API IGuiyangServerRequestHandler
{
    GENERATED_BODY()

public:
    /** 验证登录会话，并把身份绑定到当前网络连接。 */
    virtual void HandleAuthenticateSession(AGuiyangMahjongPlayerController* Controller,
        const FString& PlayerId, const FString& DisplayName, EGuiyangLoginProvider Provider,
        const FString& SessionToken) = 0;
    /** 创建、快速匹配或按房间号加入。 */
    virtual void HandleCreateRoom(AGuiyangMahjongPlayerController* Controller,
        const FMahjongCreateRoomRequest& Request) = 0;
    virtual void HandleQuickStart(AGuiyangMahjongPlayerController* Controller) = 0;
    virtual void HandleJoinRoom(AGuiyangMahjongPlayerController* Controller,
        const FMahjongJoinRoomRequest& Request) = 0;
    /** 切换准备、离开房间或推进下一局。 */
    virtual void HandleToggleReady(AGuiyangMahjongPlayerController* Controller) = 0;
    virtual void HandleLeaveRoom(AGuiyangMahjongPlayerController* Controller) = 0;
    virtual void HandleNextRound(AGuiyangMahjongPlayerController* Controller) = 0;
    /** 兼容旧客户端的出牌入口。 */
    virtual void HandleLegacyPlayTile(AGuiyangMahjongPlayerController* Controller,
        const FMahjongTile& Tile, int32 ClientSequence) = 0;
    /** 处理带序号的统一牌桌动作，服务端负责合法性和幂等校验。 */
    virtual void HandleTableAction(AGuiyangMahjongPlayerController* Controller,
        const FMahjongActionRequest& Request) = 0;
};
