#pragma once

#include "CoreMinimal.h"
#include "GameFramework/GameModeBase.h"
#include "GuiyangMahjongClientGameMode.generated.h"

/**
 * 仅供客户端目标使用的轻量启动 GameMode。
 * 引擎 Entry 关卡没有项目专用 WorldSettings，因此由本类创建共享 PlayerController
 * 并启动客户端 UI 桥接；独立服务器使用自己的服务端 GameMode。
 */
UCLASS(Config=Game)
class GUIYANGMAHJONGCLIENT_API AGuiyangMahjongClientGameMode final : public AGameModeBase
{
    GENERATED_BODY()

public:
    /** 配置默认控制器和客户端启动参数。 */
    AGuiyangMahjongClientGameMode();
};
