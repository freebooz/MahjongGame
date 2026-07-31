#pragma once

#include "CoreMinimal.h"
#include "Network/MahjongNetworkTypes.h"
#include "UObject/Object.h"
#include "MahjongTableEngine.generated.h"

/** 单桌 Dedicated Server 的权威牌局状态机。客户端只能提交意图，不能指定牌墙或结算结果。 */
UCLASS()
class GUIYANGMAHJONGCORE_API UMahjongTableEngine : public UObject
{
    GENERATED_BODY()

public:
    /** 使用冻结规则、四个座位、庄家和洗牌种子启动一局。 */
    bool StartRound(const FGuiyangRuleSnapshot& RuleSnapshot, const TArray<FMahjongSeatInfo>& Seats,
        int32 DealerSeat, int32 ShuffleSeed, FString& OutError);
    /** 提交当前玩家的出牌、回合内动作或响应窗口动作。 */
    FMahjongActionResult SubmitPlayTile(int32 SeatIndex, const FMahjongActionRequest& Request);
    FMahjongActionResult SubmitTurnAction(int32 SeatIndex, const FMahjongActionRequest& Request);
    FMahjongActionResult SubmitReaction(int32 SeatIndex, const FMahjongActionRequest& Request);
    /** 仅当期望版本仍匹配时处理超时，避免旧定时器推进新回合。 */
    FMahjongActionResult ResolveActionTimeout(int32 ExpectedRoundId, int32 ExpectedTurnId,
        EMahjongTablePhase ExpectedPhase);
    void SetActionDeadlineForServer(double DeadlineServerTimeSeconds, int32 TimeoutSeconds);

    /** 读取公共快照、单座位私有快照和当前可用动作。 */
    const FMahjongPublicTableState& GetPublicState() const { return PublicState; }
    bool GetPrivateState(int32 SeatIndex, FMahjongPrivatePlayerState& OutState) const;
    TArray<FMahjongAction> GetAvailableActions(int32 SeatIndex) const;
    const FGuiyangRuleSnapshot& GetLockedRuleSnapshot() const { return LockedRules; }
    bool GetSettlementResult(FMahjongSettlementResult& OutResult) const;
    /** 导出完整权威状态；调用方必须按私有证据处理并在落盘前计算完整状态哈希。 */
    bool ExportRecoveryState(FMahjongTableRecoveryState& OutState) const;
    /** 从受验证快照恢复全部状态；失败不返回部分可运行牌桌。 */
    bool RestoreRecoveryState(const FMahjongTableRecoveryState& State, FString& OutError);
    /**
     * 返回本局完整牌墙的服务端审计只读视图。
     *
     * 调用方只能计算摘要或在受控争议调查中复核；牌局结束前不得复制到网络快照、日志或管理接口。
     */
    const TArray<FMahjongTile>* GetDeckOrderForServerAudit() const;
    /** Seat indices increase from dealer to right-hand player: counter-clockwise around the table. */
    static int32 GetNextTurnSeatCounterClockwise(int32 SeatIndex);
    static int32 GetCounterClockwiseSeatDistance(int32 FromSeat, int32 ToSeat);
#if WITH_DEV_AUTOMATION_TESTS
    bool SetHandForServerTest(int32 SeatIndex, const FMahjongHand& Hand);
#endif

private:
    /** 牌墙管理器和开局后锁定的规则快照。 */
    UPROPERTY(Transient) TObjectPtr<class UMahjongDeckManager> DeckManager;
    UPROPERTY() FGuiyangRuleSnapshot LockedRules;
    /** 可复制公共状态、四家私有手牌及最终结算。 */
    UPROPERTY() FMahjongPublicTableState PublicState;
    UPROPERTY() TArray<FMahjongHand> Hands;
    UPROPERTY() FMahjongSettlementResult SettlementResult;
    /** 每座位当前可选动作和已经提交的响应。 */
    TMap<int32, TArray<FMahjongAction>> AvailableActionsBySeat;
    TMap<int32, FMahjongActionRequest> SubmittedReactions;
    /** 客户端幂等序号、累计分及本局杠分/特殊鸡分。 */
    TArray<int32> LastClientSequences;
    TArray<int32> CurrentScores;
    TArray<int32> GangDeltas;
    TArray<int32> SpecialJiDeltas;
    /** 最近弃牌、首次特殊鸡弃牌和最近摸牌上下文。 */
    int32 LastDiscardSeat = INDEX_NONE;
    int32 FirstSpecialJiDiscardSequence = INDEX_NONE;
    FMahjongTile LastDrawnTile;
    /** 补杠期间临时保留的抢杠胡窗口状态。 */
    bool bQiangGangWindow = false;
    int32 PendingBuGangSeat = INDEX_NONE;
    int32 PendingBuGangTileId = INDEX_NONE;
    FMahjongTile PendingBuGangTile;

    /** 校验座位、局/回合版本和客户端单调序号。 */
    bool ValidateRequestCommon(int32 SeatIndex, const FMahjongActionRequest& Request, FString& OutError);
    /** 打开并解析普通弃牌或抢杠响应窗口。 */
    void OpenReactionWindow(const FMahjongTile& Discard, int32 DiscardSeat);
    void BeginBuGang(int32 SeatIndex, const FMahjongTile& Tile);
    void CompleteBuGang();
    void ResolveQiangGangReactions(const TArray<int32>& HuSeats);
    void ResolveSubmittedReactions();
    void ResolveHuReactions(const TArray<int32>& HuSeats);
    /** 应用碰/杠认领，或推进到下一位并摸牌。 */
    void ApplyClaim(int32 SeatIndex, EMahjongActionType Type);
    void AdvanceTurnAndDraw();
    void RebuildTurnActions();
    /** 生成和保存胡牌或流局结算。 */
    void SettleWin(const TArray<int32>& WinningSeats, int32 LoserSeat, bool bSelfDraw, const FMahjongTile& WinningTile);
    void SettleDrawGame();
    void ApplyGangScore(int32 GangSeat);
    /** 记录冲锋鸡/责任鸡并累计特殊分。 */
    void RecordSpecialJiDiscard(int32 SeatIndex, const FMahjongDiscardRecord& Record);
    void RecordZeRenJiClaim(int32 ClaimSeat, EMahjongActionType ClaimType);
    bool IsSpecialJiTarget(const FMahjongTile& Tile, bool bForZeRen) const;
    TArray<int32> CountJiForSettlement(const FMahjongTile& FlippedJiTile, const TArray<int32>& WinningSeats,
        bool bSelfDraw, const FMahjongTile& WinningTile) const;
    void RefreshSeatCounts();
    FMahjongAction BuildReactionAction(int32 SeatIndex, EMahjongActionType Type, const FMahjongTile& Discard) const;
    int32 FindBestReactionSeat() const;
    /** 定义胡、杠、碰等响应冲突的稳定优先级。 */
    static int32 GetReactionPriority(EMahjongActionType Type);
    static bool RemoveTilesByRuleIndex(FMahjongHand& Hand, int32 RuleIndex, int32 Count, TArray<FMahjongTile>& OutRemoved);
    FMahjongActionResult Fail(const FString& Message) const;
};
