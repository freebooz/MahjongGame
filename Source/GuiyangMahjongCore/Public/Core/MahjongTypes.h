#pragma once

#include "CoreMinimal.h"
#include "MahjongTypes.generated.h"

/** 麻将花色；Winds/Dragons 仅在 136 张牌配置中使用。 */
UENUM(BlueprintType)
enum class EMahjongSuit : uint8
{
    Characters UMETA(DisplayName="万"),
    Bamboo UMETA(DisplayName="条"),
    Dots UMETA(DisplayName="筒"),
    Winds UMETA(DisplayName="风"),
    Dragons UMETA(DisplayName="箭牌")
};

/** 牌的语义类型；序数牌的具体点数存放在 Rank。 */
UENUM(BlueprintType)
enum class EMahjongTileType : uint8
{
    Number,
    East, South, West, North,
    RedDragon, GreenDragon, WhiteDragon,
    Invalid
};

/** 客户端可请求、规则引擎可解析的统一牌桌动作。 */
UENUM(BlueprintType)
enum class EMahjongActionType : uint8
{
    Draw, Play, Peng, MingGang, AnGang, BuGang, Hu, Pass
};

/** 权威牌桌状态机阶段。 */
UENUM(BlueprintType)
enum class EMahjongTablePhase : uint8
{
    WaitingForPlayers,
    PreparingGame,
    Dealing,
    PlayerTurn,
    WaitingForAction,
    ResolvingAction,
    Settlement,
    GameOver,
    Restarting
};

/** 已公开副露的规则类型；Chi 为规则扩展预留，当前贵阳主流规则主要使用碰和三类杠。 */
UENUM(BlueprintType)
enum class EMahjongMeldType : uint8
{
    Chi, Peng, MingGang, AnGang, BuGang
};

/** 统计普通鸡时纳入的牌区范围；房间创建后随规则快照冻结。 */
UENUM(BlueprintType)
enum class EMahjongJiCountingScope : uint8
{
    HandOnly,
    HandAndMeld,
    HandAndDiscard,
    HandMeldAndDiscard
};

/** 需要独立留痕和结算归责的贵阳鸡事件类型。 */
UENUM(BlueprintType)
enum class EMahjongJiEventType : uint8
{
    ChongFeng,
    ZeRen
};

/** 牌墙组成。贵阳主流规则默认只使用万、条、筒三门 108 张牌。 */
UENUM(BlueprintType)
enum class EMahjongTileSetMode : uint8
{
    Suited108 UMETA(DisplayName="三门数牌（108 张）"),
    Standard136 UMETA(DisplayName="标准牌（136 张，含风牌和箭牌）")
};

/** 一张麻将牌。UniqueId 在单局牌墙中稳定且唯一，规则判断使用 Suit/Rank 而不是显示文本。 */
USTRUCT(BlueprintType)
struct GUIYANGMAHJONGCORE_API FMahjongTile
{
    GENERATED_BODY()

    /** 花色、字牌类型和序数牌点数共同描述牌面。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) EMahjongSuit Suit = EMahjongSuit::Characters;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) EMahjongTileType Type = EMahjongTileType::Invalid;
    UPROPERTY(EditAnywhere, BlueprintReadWrite, meta=(ClampMin="0", ClampMax="9")) int32 Rank = 0;
    /** 单局内唯一实例号，用于出牌请求准确定位同牌面的某一张实体牌。 */
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly) int32 UniqueId = INDEX_NONE;

    /** 基础有效性、34 种牌规则索引和诊断文本工具。 */
    bool IsValid() const { return UniqueId >= 0 && Type != EMahjongTileType::Invalid; }
    int32 GetRuleIndex() const;
    FString ToDebugString() const;
    bool operator==(const FMahjongTile& Other) const { return UniqueId == Other.UniqueId; }
};

/** 已公开的吃、碰或杠副露。 */
USTRUCT(BlueprintType)
struct GUIYANGMAHJONGCORE_API FMahjongMeld
{
    GENERATED_BODY()
    /** 副露类型、包含牌、归属座位及来源座位。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) EMahjongMeldType Type = EMahjongMeldType::Peng;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<FMahjongTile> Tiles;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 OwnerSeat = INDEX_NONE;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 FromSeat = INDEX_NONE;
};

/** 单个玩家的私有手牌与已公开副露集合。 */
USTRUCT(BlueprintType)
struct GUIYANGMAHJONGCORE_API FMahjongHand
{
    GENERATED_BODY()
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<FMahjongTile> Tiles;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<FMahjongMeld> Melds;
    /** 输出诊断文本，并按规则索引稳定排序手牌。 */
    FString ToDebugString() const;
    void Sort();
};

/** 一次弃牌及其公共序号和是否已被其他动作认领。 */
USTRUCT(BlueprintType)
struct GUIYANGMAHJONGCORE_API FMahjongDiscardRecord
{
    GENERATED_BODY()
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 SeatIndex = INDEX_NONE;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FMahjongTile Tile;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 Sequence = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bClaimed = false;
};

/** 服务端验证后形成的规范化动作。 */
USTRUCT(BlueprintType)
struct GUIYANGMAHJONGCORE_API FMahjongAction
{
    GENERATED_BODY()
    /** 动作类型、来源/目标座位、目标牌和从手中消耗的牌。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) EMahjongActionType Type = EMahjongActionType::Pass;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 SourceSeat = INDEX_NONE;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 TargetSeat = INDEX_NONE;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FMahjongTile TargetTile;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<FMahjongTile> ConsumedTiles;
    FString ToDebugString() const;
};

/** 客户端发往权威牌桌的动作请求；包含状态版本和幂等序号。 */
USTRUCT(BlueprintType)
struct GUIYANGMAHJONGCORE_API FMahjongActionRequest
{
    GENERATED_BODY()
    /** 客户端为一次用户意图生成的 UUID；服务端按玩家去重，不能由网络重试重新生成。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString ClientActionId;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) EMahjongActionType Type = EMahjongActionType::Pass;
    /** 客户端观察到的局号和回合号，服务端用其拒绝过期操作。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 RoundId = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 TurnId = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 TargetTileId = INDEX_NONE;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<int32> ConsumedTileIds;
    /** 当前连接内单调递增的客户端动作序号。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 ClientSequence = 0;
    /** 客户端观察到的权威状态版本和房间代际，旧状态或旧实例请求必须拒绝。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 ExpectedStateVersion = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int64 RoomEpoch = 0;
    /** 客户端发送时的 Unix 毫秒只用于窗口校验和诊断，绝不参与规则计时。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int64 ClientSentAtUnixMilliseconds = 0;
};

/** 一次动作的成功标志、错误信息及规范化结果。 */
USTRUCT(BlueprintType)
struct GUIYANGMAHJONGCORE_API FMahjongActionResult
{
    GENERATED_BODY()
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bSuccess = false;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FString Message;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FMahjongAction Action;
};

/**
 * Dedicated Server 崩溃恢复使用的完整牌墙状态。
 * 该结构不得复制到客户端或普通日志；Deck 顺序和游标共同保证恢复后摸牌完全确定。
 */
USTRUCT()
struct GUIYANGMAHJONGCORE_API FMahjongDeckRecoveryState
{
    GENERATED_BODY()
    /** 按权威摸牌顺序保存完整牌墙；包含未公开牌，只能进入受限恢复快照。 */
    UPROPERTY() TArray<FMahjongTile> Deck;
    /** 牌墙顺时针起点和开门位置；三者共同决定恢复后的物理牌墙语义。 */
    UPROPERTY() int32 ClockwiseDrawStartIndex = 0;
    UPROPERTY() int32 WallBreakSide = 0;
    UPROPERTY() int32 WallBreakStackFromRight = 0;
    /** 已从顺时针起点消耗的牌数；恢复后下一次摸牌必须从该偏移继续。 */
    UPROPERTY() int32 ClockwiseDrawOffset = 0;
};

/** 单个座位的本局分项增减及累计总分。 */
USTRUCT(BlueprintType)
struct GUIYANGMAHJONGCORE_API FMahjongPlayerScoreResult
{
    GENERATED_BODY()
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 SeatIndex = INDEX_NONE;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 BaseScoreDelta = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 JiScoreDelta = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 SpecialJiScoreDelta = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 GangScoreDelta = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 TotalDelta = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 TotalScore = 0;
};

/** 冲锋鸡或责任鸡事件，保留触发牌、座位和弃牌序号用于复核。 */
USTRUCT(BlueprintType)
struct GUIYANGMAHJONGCORE_API FMahjongJiEvent
{
    GENERATED_BODY()
    UPROPERTY(EditAnywhere, BlueprintReadWrite) EMahjongJiEventType Type = EMahjongJiEventType::ChongFeng;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FMahjongTile Tile;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 ActorSeat = INDEX_NONE;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 TargetSeat = INDEX_NONE;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 ValueUnits = 0;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 DiscardSequence = INDEX_NONE;
};

/** 单局结算快照；同时包含胜负、鸡事件和各座位分项。 */
USTRUCT(BlueprintType)
struct GUIYANGMAHJONGCORE_API FMahjongSettlementResult
{
    GENERATED_BODY()
    /** 流局/自摸标志及赢家、放炮者和获胜牌。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bDrawGame = false;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bSelfDraw = false;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 WinnerSeat = INDEX_NONE;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<int32> WinningSeats;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 LoserSeat = INDEX_NONE;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FMahjongTile WinningTile;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FMahjongTile FlippedJiTile;
    /**
     * 各座位鸡牌单位总数及结算分项。
     * 内鸡只统计结算时仍在手中的牌；外鸡统计副露、未被认领的弃牌以及冲锋鸡升级单位。
     * 黑八和冲锋鸡数组是总数的可重叠审计子集，不能再次相加到 PlayerJiCounts。
     */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<int32> PlayerJiCounts;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<int32> PlayerInnerJiCounts;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<int32> PlayerOuterJiCounts;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<int32> PlayerWuGuJiCounts;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<int32> PlayerChongFengJiCounts;
    /** 冲锋鸡、责任鸡事件明细和最终逐座位分数。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<FMahjongJiEvent> JiEvents;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<FMahjongTile> JiTiles;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) TArray<FMahjongPlayerScoreResult> PlayerResults;
    FString ToDebugString() const;
};

/** 创建房间时冻结的完整规则配置。 */
USTRUCT(BlueprintType)
struct GUIYANGMAHJONGCORE_API FMahjongRuleConfig
{
    GENERATED_BODY()
    /** 规则族标识和版本会写入房间规则快照，开局后不得修改。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) FName RuleId = TEXT("GuiyangMainstreamV1");
    UPROPERTY(EditAnywhere, BlueprintReadWrite, meta=(ClampMin="1")) int32 RuleVersion = 2;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) EMahjongTileSetMode TileSetMode = EMahjongTileSetMode::Suited108;
    /** 贵阳特殊鸡、抢杠胡、一炮多响、七对及超时托管开关。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bEnableChongFengJi = true;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bEnableZeRenJi = true;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bEnableWuGuJi = true;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bWuGuCanChongFeng = true;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bWuGuCanZeRen = true;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bEnableQiangGangHu = true;
    /**
     * 贵阳平胡通行证：接他家弃牌或抢杠胡前，赢家必须已经完成至少一次明杠、暗杠或补杠。
     * 自摸不受该限制；仅持有四张同牌但尚未执行杠牌不算取得通行证。
     */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bRequireGangForDiscardHu = true;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bEnableYiPaoDuoXiang = true;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bEnableQiDui = true;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bDrawGameDealerContinues = true;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) bool bEnableTimeoutAutoPlay = true;
    /** 基础分、各种鸡的单位值、杠分和胡牌倍率。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 BaseScore = 1;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 JiScore = 1;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 BasicJiValue = 1;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 FlippedJiValue = 1;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 WuGuJiValue = 2;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 ChongFengJiValue = 2;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 WuGuChongFengJiValue = 4;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 ZeRenJiValue = 1;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 WuGuZeRenJiValue = 1;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) EMahjongJiCountingScope JiCountingScope = EMahjongJiCountingScope::HandMeldAndDiscard;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 GangScore = 1;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 ZiMoMultiplier = 2;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 DianPaoMultiplier = 1;
    /** 重连、出牌和响应窗口的服务端超时秒数。 */
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 ReconnectTimeoutSeconds = 120;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 TurnTimeoutSeconds = 15;
    UPROPERTY(EditAnywhere, BlueprintReadWrite) int32 ReactionTimeoutSeconds = 8;
};
