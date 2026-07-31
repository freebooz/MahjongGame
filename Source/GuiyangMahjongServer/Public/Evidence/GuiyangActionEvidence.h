#pragma once

#include "CoreMinimal.h"
#include "Core/MahjongTypes.h"
#include "GuiyangActionEvidence.generated.h"

/**
 * 一次已接受权威动作的防篡改证据记录。
 * NormalizedPayload 只包含规则意图的规范字段，不包含 Access Token、Join Ticket、密钥或私有手牌。
 */
USTRUCT()
struct GUIYANGMAHJONGSERVER_API FGuiyangActionEvidenceRecord
{
    GENERATED_BODY()

    UPROPERTY() FString MatchId;
    UPROPERTY() FString RoomId;
    UPROPERTY() int64 RoomEpoch = 0;
    UPROPERTY() int64 ActionSequence = 0;
    UPROPERTY() int32 StateVersionBefore = 0;
    UPROPERTY() int32 StateVersionAfter = 0;
    /** 动作应用后完整权威状态的 SHA-256，用于新实例逐条重放校验。 */
    UPROPERTY() FString StateHashAfter;
    UPROPERTY() FString PlayerId;
    UPROPERTY() int32 SeatId = INDEX_NONE;
    UPROPERTY() FString ActionType;
    UPROPERTY() FString NormalizedPayload;
    UPROPERTY() FString OccurredAtUtc;
    UPROPERTY() FString PreviousHash;
    UPROPERTY() FString ActionHash;
    /** 回放只使用已验证后的规范请求；网络凭据不会进入该结构。 */
    UPROPERTY() FMahjongActionRequest Request;
};

/** 生成动作规范文本和 SHA-256 链摘要的无状态工具。 */
class GUIYANGMAHJONGSERVER_API FGuiyangActionEvidence final
{
public:
    /** 按固定字段顺序构造不含私有牌的规范负载。 */
    static FString NormalizeRequest(const FMahjongActionRequest& Request);
    /** 计算绑定上一记录、作用域、版本和规范负载的动作哈希。 */
    static FString CalculateHash(const FGuiyangActionEvidenceRecord& Record);
};
