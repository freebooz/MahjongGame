#pragma once

#include "CoreMinimal.h"

struct FGuiyangGameServerLaunchConfig;

/** Lobby 短期入场票据中携带并经独立服务器验证后的权威声明。 */
struct GUIYANGMAHJONGSERVER_API FGuiyangJoinTicketClaims
{
    /** 被允许进入房间的玩家标识。 */
    FString PlayerId;
    /** 票据限定的房间、比赛和服务器实例范围。 */
    FString RoomId;
    FString MatchId;
    FString ServerInstanceId;
    /** 一次性随机数，用于防止同一票据重放。 */
    FString Nonce;
    /** Unix 秒级过期时间。 */
    int64 ExpiresAtUnixSeconds = 0;
};

/** HMAC 票据校验器，同时检查作用域、过期时间和一次性 Nonce。 */
class GUIYANGMAHJONGSERVER_API FGuiyangJoinTicketValidator
{
public:
    /** 从不可变的游戏服启动配置建立预期作用域。 */
    explicit FGuiyangJoinTicketValidator(const FGuiyangGameServerLaunchConfig& Config);

    /** 校验票据并原子记录 Nonce；成功后同一票据不能再次使用。 */
    bool ValidateAndConsume(const FString& Ticket, const FString& SuppliedPlayerId,
        int64 NowUnixSeconds, FGuiyangJoinTicketClaims& OutClaims, FString& OutError);

private:
    /** 签名密钥及本服务器允许的固定作用域。 */
    FString SigningKey;
    FString ExpectedRoomId;
    FString ExpectedMatchId;
    FString ExpectedServerInstanceId;
    /** 已消费 Nonce 到过期时间的映射，兼作短期重放缓存。 */
    TMap<FString, int64> UsedNonces;
};
