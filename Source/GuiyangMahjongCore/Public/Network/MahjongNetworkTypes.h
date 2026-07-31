#pragma once

#include "CoreMinimal.h"
#include "Core/MahjongTypes.h"
#include "Rules/GuiyangRuleSnapshot.h"
#include "MahjongNetworkTypes.generated.h"

/** 从创建到关闭的房间级生命周期。 */
UENUM(BlueprintType)
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

/** 可安全返回客户端的房间操作错误分类。 */
UENUM(BlueprintType)
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

/** 创建房间请求；密码仅在传输链路短暂存在。 */
USTRUCT(BlueprintType)
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

/** 按六位房间码加入的请求。 */
USTRUCT(BlueprintType)
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

/** 可公开复制的一个座位摘要，不包含手牌内容。 */
USTRUCT(BlueprintType)
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

/** 房间不随单次出牌频繁变化的公共元数据。 */
USTRUCT(BlueprintType)
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

/** 客户端大厅和房间 UI 使用的公共房间快照。 */
USTRUCT(BlueprintType)
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

/** 最终大结算中的单个玩家排名。 */
USTRUCT(BlueprintType)
struct GUIYANGMAHJONGCORE_API FMahjongFinalPlayerResult
{
    GENERATED_BODY()
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString PlayerId;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 Rank = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 SeatIndex = INDEX_NONE;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString PlayerName;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 TotalScore = 0;
};

/** 完整比赛结束后可持久化和上报控制面的最终结算。 */
USTRUCT(BlueprintType)
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
    /** 当前权威房间代际；客户端动作必须原样回传，旧 DS 的代际永远不能复用。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int64 RoomEpoch = 0;
    /** 局/回合/服务端动作序号共同定义公共状态版本。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 RoundId = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 TurnId = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 ServerActionSequence = 0;
    /** 当前阶段、行动座位、剩余牌数和服务器截止时间。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) EMahjongTablePhase Phase = EMahjongTablePhase::WaitingForPlayers;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 CurrentTurnSeat = INDEX_NONE;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 RemainingTileCount = 0;
    /** 开门信息：逆时针数到牌墙、从右向左数墩，之后牌墙顺时针消耗。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 WallBreakDiceTotal = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 WallBreakSide = INDEX_NONE;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 WallBreakStackFromRight = 0;
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
    /** 不含私有手牌的公共状态摘要，用于客户端重连确认；完整权威哈希只保存在服务端证据中。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString PublicStateHash;
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

/** 重连成功后一次性恢复的公共、私有和房间组合快照。 */
USTRUCT(BlueprintType)
struct GUIYANGMAHJONGCORE_API FMahjongReconnectSnapshot
{
    GENERATED_BODY()
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FMahjongRoomState RoomState;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FMahjongPublicTableState TableState;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FMahjongPrivatePlayerState PrivateState;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 RemainingReconnectSeconds = 0;
    /** 新 DS 恢复完成后生成的控制令牌；客户端确认前不接受新的牌桌动作。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString ControlToken;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 MissingActionCount = 0;
};

/** 单座位候选动作/已提交响应的可序列化容器，避免直接持久化整数键 TMap。 */
USTRUCT()
struct GUIYANGMAHJONGCORE_API FMahjongSeatActionRecoveryState
{
    GENERATED_BODY()
    UPROPERTY() int32 SeatIndex = INDEX_NONE;
    UPROPERTY() TArray<FMahjongAction> AvailableActions;
    UPROPERTY() bool bHasSubmittedReaction = false;
    UPROPERTY() FMahjongActionRequest SubmittedReaction;
};

/**
 * 牌桌引擎的完整权威恢复状态。
 * 只允许 Dedicated Server 的 Snapshot 模块持久化；其中包含私有手牌和完整牌墙，
 * 不能进入 GameState 复制、普通日志、管理列表或玩家 HTTP 响应。
 */
USTRUCT()
struct GUIYANGMAHJONGCORE_API FMahjongTableRecoveryState
{
    GENERATED_BODY()
    UPROPERTY() FGuiyangRuleSnapshot LockedRules;
    UPROPERTY() FMahjongPublicTableState PublicState;
    UPROPERTY() TArray<FMahjongHand> Hands;
    UPROPERTY() FMahjongSettlementResult SettlementResult;
    UPROPERTY() TArray<FMahjongSeatActionRecoveryState> SeatActions;
    UPROPERTY() TArray<int32> LastClientSequences;
    UPROPERTY() TArray<int32> CurrentScores;
    UPROPERTY() TArray<int32> GangDeltas;
    UPROPERTY() TArray<int32> SpecialJiDeltas;
    UPROPERTY() int32 LastDiscardSeat = INDEX_NONE;
    UPROPERTY() int32 FirstSpecialJiDiscardSequence = INDEX_NONE;
    UPROPERTY() FMahjongTile LastDrawnTile;
    UPROPERTY() bool bQiangGangWindow = false;
    UPROPERTY() int32 PendingBuGangSeat = INDEX_NONE;
    UPROPERTY() int32 PendingBuGangTileId = INDEX_NONE;
    UPROPERTY() FMahjongTile PendingBuGangTile;
    UPROPERTY() FMahjongDeckRecoveryState DeckState;
};
