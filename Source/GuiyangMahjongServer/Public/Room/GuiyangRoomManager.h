#pragma once

#include "CoreMinimal.h"
#include "Network/MahjongNetworkTypes.h"
#include "UObject/Object.h"
#include "GuiyangRoomManager.generated.h"

struct FGuiyangManagedRoomDefinition;

/**
 * 玩家连接状态的权威监控快照。
 * Sequence 在房间内按玩家单调递增，EventId 标识一次真实状态变化，重复心跳不得生成新事件。
 */
struct GUIYANGMAHJONGSERVER_API FGuiyangPlayerConnectionTelemetry
{
    /** 当前是否掉线；连接正常时 DisconnectedAtUtc 为空。 */
    bool bDisconnected = false;
    /** 最近状态变化、当前掉线开始和最近恢复时间。 */
    FDateTime ChangedAtUtc;
    FDateTime DisconnectedAtUtc;
    FDateTime ReconnectedAtUtc;
    /** NormalExit、NetworkInterrupted、ReconnectTimeout、Kicked 或 ServerShutdown。 */
    FString DisconnectReason;
    /** 状态单调序号与本次变化的全局幂等标识。 */
    int64 Sequence = 0;
    FString EventId;
};

/** Dedicated Server 持有的房间领域服务；客户端不得创建该对象或直接改写房间记录。 */
UCLASS()
class GUIYANGMAHJONGSERVER_API UGuiyangRoomManager : public UObject
{
    GENERATED_BODY()

public:
    /** 从控制面 Bootstrap 创建唯一的托管权威房间。 */
    bool CreateManagedRoom(const FGuiyangManagedRoomDefinition& Definition,
        FMahjongRoomState& OutState, EMahjongRoomError& OutError);
    /** 将持有效票据的玩家接纳到托管房间。 */
    bool AdmitManagedPlayer(const FString& RoomCode, const FString& PlayerId, const FString& DisplayName,
        FMahjongRoomState& OutState, EMahjongRoomError& OutError);
    /** 本地模式的创建、快速匹配和按码加入入口。 */
    bool CreateRoom(const FString& PlayerId, const FString& DisplayName, const FMahjongCreateRoomRequest& Request,
        FMahjongRoomState& OutState, EMahjongRoomError& OutError);
    bool QuickStart(const FString& PlayerId, const FString& DisplayName,
        FMahjongRoomState& OutState, EMahjongRoomError& OutError);
    bool JoinRoom(const FString& PlayerId, const FString& DisplayName, const FMahjongJoinRoomRequest& Request,
        FMahjongRoomState& OutState, EMahjongRoomError& OutError);
    /** 房间内准备、离开、开局、结算及下一局状态转换。 */
    bool ToggleReady(const FString& PlayerId, FMahjongRoomState& OutState, EMahjongRoomError& OutError);
    bool LeaveRoom(const FString& PlayerId, FMahjongRoomState& OutState, EMahjongRoomError& OutError);
    bool BeginPlaying(const FString& RoomCode, FMahjongRoomState& OutState, EMahjongRoomError& OutError);
    bool FinishRound(const FString& RoomCode, const FMahjongSettlementResult& Settlement,
        FMahjongRoomState& OutState, EMahjongRoomError& OutError);
    bool RequestNextRound(const FString& PlayerId, FMahjongRoomState& OutState, EMahjongRoomError& OutError);
    /** 标记掉线并在超时窗口内恢复原座位。 */
    bool MarkDisconnected(const FString& PlayerId, FMahjongRoomState& OutState,
        EMahjongRoomError& OutError, const FString& Reason = TEXT("NetworkInterrupted"));
    bool ReconnectPlayer(const FString& PlayerId, FMahjongRoomState& OutState,
        int32& OutRemainingSeconds, EMahjongRoomError& OutError);
    /** 只读查询房间/玩家索引，并生成比赛最终排名。 */
    bool GetRoomState(const FString& RoomCode, FMahjongRoomState& OutState) const;
    bool GetPlayerRoomCode(const FString& PlayerId, FString& OutRoomCode) const;
    /** 返回当前或最近连接状态；玩家不在房间或尚无状态记录时返回 false。 */
    bool GetPlayerConnectionTelemetry(
        const FString& PlayerId, FGuiyangPlayerConnectionTelemetry& OutTelemetry) const;
    static FMahjongFinalSettlementResult BuildFinalSettlement(const FMahjongRoomState& State);
    int32 GetRoomCount() const { return Rooms.Num(); }

private:
    struct FPasswordAttemptState
    {
        /** 连续失败次数及临时锁定截止时间。 */
        int32 FailureCount = 0;
        FDateTime LockedUntilUtc;
    };

    struct FRoomRecord
    {
        /** 可广播的公共状态。 */
        FMahjongRoomState PublicState;
        /** 仅服务端持有的加盐密码摘要及防暴力尝试状态。 */
        FString PasswordSalt;
        FString PasswordDigest;
        TMap<FString, FPasswordAttemptState> PasswordAttemptsByPlayer;
        /** 每名在座玩家最近连接状态，保留重连时间以支持 Admin 时间线审计。 */
        TMap<FString, FGuiyangPlayerConnectionTelemetry> ConnectionTelemetryByPlayer;
        /** 是否由外部控制面创建并拥有生命周期。 */
        bool bManagedAuthority = false;
    };

    /** 房间码主表及玩家到单一活动房间的反向索引。 */
    TMap<FString, FRoomRecord> Rooms;
    TMap<FString, FString> PlayerRoomCodes;
    FRandomStream RoomCodeRandom;
    bool bRandomInitialized = false;

    /** 密码尝试限制与 PBKDF2 等价迭代成本。 */
    static constexpr int32 MaxPasswordFailures = 5;
    static constexpr int32 PasswordLockSeconds = 30;
    static constexpr int32 PasswordHashRounds = 100000;

    /** 生成六位不冲突房间码并校验外部输入。 */
    FString GenerateUniqueRoomCode();
    static bool ValidateIdentity(const FString& PlayerId, const FString& DisplayName);
    static bool ValidatePassword(const FString& Password);
    static FString MakePasswordSalt();
    /** 对密码进行加盐慢哈希，并使用常量时间比较摘要。 */
    static FString HashPassword(const FString& Password, const FString& Salt);
    static bool ConstantTimeEquals(const FString& Left, const FString& Right);
    static FMahjongSeatInfo* FindSeat(FMahjongRoomState& State, const FString& PlayerId);
    static const FMahjongSeatInfo* FindSeat(const FMahjongRoomState& State, const FString& PlayerId);
    /** 根据人数、准备和局数重算公共生命周期。 */
    static void RefreshLifecycle(FMahjongRoomState& State);
};
