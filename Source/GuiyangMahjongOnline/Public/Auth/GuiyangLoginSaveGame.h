#pragma once

#include "CoreMinimal.h"
#include "GameFramework/SaveGame.h"
#include "Auth/GuiyangLoginTypes.h"
#include "GuiyangLoginSaveGame.generated.h"

/** 本地安装标识。绝不持久化登录状态、会话令牌或第三方访问令牌。 */
UCLASS()
class GUIYANGMAHJONGONLINE_API UGuiyangLoginSaveGame : public USaveGame
{
    GENERATED_BODY()

public:
    /** 伪匿名安装标识，不是凭据；Auth 用它恢复稳定游客身份。 */
    UPROPERTY(SaveGame) FString InstallationId;

};
