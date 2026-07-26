#pragma once

#include "CoreMinimal.h"
#include "GameFramework/SaveGame.h"
#include "History/MahjongMatchHistoryTypes.h"
#include "MahjongMatchHistorySaveGame.generated.h"

/** 战绩 SaveGame 容器；只负责序列化，不承载业务逻辑。 */
UCLASS()
class GUIYANGMAHJONGCLIENT_API UMahjongMatchHistorySaveGame : public USaveGame
{
    GENERATED_BODY()
public:
    /** 按时间从新到旧保存的有限条战绩。 */
    UPROPERTY() TArray<FMahjongMatchHistoryRecord> Records;
};
