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

    /** 证据作用域；MatchId 全局隔离文件，RoomEpoch 防止旧实例续写新权威链。 */
    UPROPERTY() FString MatchId;
    UPROPERTY() FString RoomId;
    UPROPERTY() int64 RoomEpoch = 0;
    /** DS 接受顺序及应用前后状态版本，序号跨恢复 Epoch 单调延续。 */
    UPROPERTY() int64 ActionSequence = 0;
    UPROPERTY() int32 StateVersionBefore = 0;
    UPROPERTY() int32 StateVersionAfter = 0;
    /** 动作应用后完整权威状态的 SHA-256，用于新实例逐条重放校验。 */
    UPROPERTY() FString StateHashAfter;
    /** 已认证玩家、绑定座位及规范动作类型；PlayerId 不得替代连接认证。 */
    UPROPERTY() FString PlayerId;
    UPROPERTY() int32 SeatId = INDEX_NONE;
    UPROPERTY() FString ActionType;
    /** 脱敏后的规范负载和服务端接受时间；时间不参与麻将规则裁决。 */
    UPROPERTY() FString NormalizedPayload;
    UPROPERTY() FString OccurredAtUtc;
    /** SHA-256 前向哈希链；PreviousHash 为空仅允许作为第一条证据。 */
    UPROPERTY() FString PreviousHash;
    UPROPERTY() FString ActionHash;
    /** false 表示服务端聚合超时动作；它必须被随后完整快照覆盖，不能伪装成单一客户端重放。 */
    UPROPERTY() bool bReplayable = true;
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
