#pragma once

#include "CoreMinimal.h"
#include "Core/MahjongTypes.h"
#include "Rules/GuiyangRuleSnapshot.h"
#include "MahjongNetworkTypes.generated.h"

UENUM(BlueprintType)
/** 从创建到关闭的房间级生命周期。 */
enum class EMahjongRoomLifecycle : uint8
{
    Creating,
    WaitingForPlayers,
    ReadyCheck,
    Starting,
    Playing,
    Settlement,
    WaitingNextRound,
    Closing,
    Closed
};

UENUM(BlueprintType)
/** 可安全返回客户端的房间操作错误分类。 */
enum class EMahjongRoomError : uint8
{
    None,
    SessionExpired,
    InvalidRequest,
    AlreadyInRoom,
    RoomNotFound,
    RoomClosed,
    RoomFull,
    GameAlreadyStarted,
    PasswordRequired,
    WrongPassword,
    TooManyPasswordAttempts,
    NotRoomOwner,
    NotInRoom,
    VersionMismatch
};

USTRUCT(BlueprintType)
/** 创建房间请求；密码仅在传输链路短暂存在。 */
struct GUIYANGMAHJONGCORE_API FMahjongCreateRoomRequest
{
    GENERATED_BODY()
    /** 局数、冻结规则、可见性和自动开始选项。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 RoundCount = 4;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FMahjongRuleConfig Rules;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bPublicRoom = true;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bAutoStart = true;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bEnablePassword = false;
    /** 仅用于 Client->Server 请求，不得复制到 GameState 或写入日志。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString Password;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 ClientSequence = 0;
};

USTRUCT(BlueprintType)
/** 按六位房间码加入的请求。 */
struct GUIYANGMAHJONGCORE_API FMahjongJoinRoomRequest
{
    GENERATED_BODY()
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString RoomCode;
    /** 仅用于 Client->Server 请求，不得复制到 GameState 或写入日志。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString Password;
    /** 幂等序号和客户端协议版本。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 ClientSequence = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 ClientProtocolVersion = 1;
};

USTRUCT(BlueprintType)
/** 可公开复制的一个座位摘要，不包含手牌内容。 */
struct GUIYANGMAHJONGCORE_API FMahjongSeatInfo
{
    GENERATED_BODY()
    /** 座位、玩家身份及房主/占用/准备/在线状态。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 SeatIndex = INDEX_NONE;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString PlayerId;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString PlayerName;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bOwner = false;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bOccupied = false;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bReady = false;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bOnline = false;
    /** 仅公开手牌张数、积分和延迟。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 HandTileCount = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 Score = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 PingMilliseconds = 0;
};

USTRUCT(BlueprintType)
/** 房间不随单次出牌频繁变化的公共元数据。 */
struct GUIYANGMAHJONGCORE_API FMahjongRoomInfo
{
    GENERATED_BODY()
    /** 控制面比赛/房间标识、显示摘要和房主。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString MatchId;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString RoomId = TEXT("100001");
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString RuleSummary = TEXT("贵阳捉鸡·四人房");
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 MaxPlayers = 4;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString OwnerPlayerId;
    /** 局数进度、庄家、底分及房间访问策略。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 RoundCount = 4;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 CurrentRound = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 DealerSeat = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 BaseScore = 1;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bPublicRoom = true;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bAutoStart = true;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bPasswordProtected = false;
};

USTRUCT(BlueprintType)
/** 客户端大厅和房间 UI 使用的公共房间快照。 */
struct GUIYANGMAHJONGCORE_API FMahjongRoomState
{
    GENERATED_BODY()
    /** 元数据、不可变规则快照、生命周期和座位数组。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FMahjongRoomInfo RoomInfo;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FGuiyangRuleSnapshot RuleSnapshot;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) EMahjongRoomLifecycle Lifecycle = EMahjongRoomLifecycle::WaitingForPlayers;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<FMahjongSeatInfo> Seats;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bGameStarting = false;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 StateSequence = 0;
};

USTRUCT(BlueprintType)
/** 最终大结算中的单个玩家排名。 */
struct GUIYANGMAHJONGCORE_API FMahjongFinalPlayerResult
{
    GENERATED_BODY()
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString PlayerId;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 Rank = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 SeatIndex = INDEX_NONE;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString PlayerName;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 TotalScore = 0;
};

USTRUCT(BlueprintType)
/** 完整比赛结束后可持久化和上报控制面的最终结算。 */
struct GUIYANGMAHJONGCORE_API FMahjongFinalSettlementResult
{
    GENERATED_BODY()
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString MatchId;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString RoomId;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 CompletedRounds = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<FMahjongFinalPlayerResult> Players;
};

/** 可公开复制给所有客户端的牌桌快照，严格不包含手牌内容和牌墙顺序。 */
USTRUCT(BlueprintType)
struct GUIYANGMAHJONGCORE_API FMahjongPublicTableState
{
    GENERATED_BODY()
    /** 局/回合/服务端动作序号共同定义公共状态版本。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 RoundId = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 TurnId = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 ServerActionSequence = 0;
    /** 当前阶段、行动座位、剩余牌数和服务器截止时间。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) EMahjongTablePhase Phase = EMahjongTablePhase::WaitingForPlayers;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 CurrentTurnSeat = INDEX_NONE;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 RemainingTileCount = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 ActionTimeoutSeconds = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) double ActionDeadlineServerTimeSeconds = 0.0;
    /** 公开座位、弃牌、副露、赢家及鸡事件。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<FMahjongSeatInfo> Seats;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<FMahjongDiscardRecord> Discards;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<FMahjongMeld> PublicMelds;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<int32> WinningSeats;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FMahjongTile LastDiscard;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FMahjongTile FlippedJiTile;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<FMahjongJiEvent> JiEvents;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 StateSequence = 0;
};

/** 仅通过所属 PlayerController 的 Client RPC 下发给单个玩家。 */
USTRUCT(BlueprintType)
struct GUIYANGMAHJONGCORE_API FMahjongPrivatePlayerState
{
    GENERATED_BODY()
    /** 快照版本及所属座位。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 RoundId = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 TurnId = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 SeatIndex = INDEX_NONE;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 LastAcceptedClientSequence = -1;
    /** 只有所属客户端可见的完整手牌。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FMahjongHand Hand;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 StateSequence = 0;
};

USTRUCT(BlueprintType)
/** 重连成功后一次性恢复的公共、私有和房间组合快照。 */
struct GUIYANGMAHJONGCORE_API FMahjongReconnectSnapshot
{
    GENERATED_BODY()
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FMahjongRoomState RoomState;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FMahjongPublicTableState TableState;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FMahjongPrivatePlayerState PrivateState;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 RemainingReconnectSeconds = 0;
};
