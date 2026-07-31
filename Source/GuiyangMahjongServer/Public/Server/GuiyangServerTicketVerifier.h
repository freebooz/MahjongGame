#pragma once

#include "CoreMinimal.h"

struct FGuiyangGameServerLaunchConfig;

/** Lobby 短期入场票据中携带并经独立服务器验证后的权威声明。 */
struct GUIYANGMAHJONGSERVER_API FGuiyangJoinTicketClaims
{
    /** 每次签发唯一的审计标识；与一次性 nonce 分离，便于脱敏关联。 */
    FString TicketId;
    /** 被允许进入房间的玩家标识。 */
    FString PlayerId;
    /** Auth/Lobby-verified display name bound into the signed ticket. */
    FString DisplayName;
    /** 票据限定的房间、比赛和服务器实例范围。 */
    FString RoomId;
    FString MatchId;
    FString ServerInstanceId;
    /** 控制面分配的固定座位及已验证 Auth 会话快照。 */
    int32 SeatId = INDEX_NONE;
    FString SessionId;
    int64 SessionEpoch = 0;
    int64 SecurityEpoch = 0;
    /** Lobby 路由代际；必须与当前 DS 启动配置完全一致。 */
    int64 RoomEpoch = 0;
    /** 客户端/服务端兼容契约；必须与当前分配冻结值一致。 */
    FString ClientBuild;
    FString ProtocolVersion;
    FString RuleSetVersion;
    /** 一次性随机数，用于防止同一票据重放。 */
    FString Nonce;
    /** Unix 秒级签发与过期时间。 */
    int64 IssuedAtUnixSeconds = 0;
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
    TArray<FString> CompatibleClientBuilds;
    FString ExpectedProtocolVersion;
    FString ExpectedRuleSetVersion;
    /** 当前实例允许的唯一房间路由代际。 */
    int64 ExpectedRoomEpoch = 1;
    /** 只在受控滚动升级期间允许旧字段缺失；默认严格拒绝。 */
    bool bAllowLegacyTickets = false;
    /** 已消费 Nonce 到过期时间的映射，兼作短期重放缓存。 */
    TMap<FString, int64> UsedNonces;
};
