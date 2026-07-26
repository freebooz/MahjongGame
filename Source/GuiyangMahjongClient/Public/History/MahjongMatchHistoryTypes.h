#pragma once

#include "CoreMinimal.h"
#include "Network/MahjongNetworkTypes.h"
#include "MahjongMatchHistoryTypes.generated.h"

/** 客户端本地保存的一条完整比赛战绩。 */
USTRUCT(BlueprintType)
struct GUIYANGMAHJONGCLIENT_API FMahjongMatchHistoryRecord
{
    GENERATED_BODY()
    /** 服务端生成的比赛唯一标识，用于去重。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString MatchId;
    /** 采用 ISO-8601 UTC 文本保存，避免设备时区变化影响排序。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString RecordedAtUtc;
    /** 服务端权威最终结算快照。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FMahjongFinalSettlementResult FinalResult;
};
