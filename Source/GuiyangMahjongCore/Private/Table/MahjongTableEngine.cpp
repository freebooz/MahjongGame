#include "Table/MahjongTableEngine.h"


#include "Rules/MahjongChiPengChecker.h"
#include "Rules/MahjongGangChecker.h"
#include "Rules/MahjongHuChecker.h"
#include "Rules/GuiyangJiCalculator.h"
#include "Rules/MahjongScoreCalculator.h"
#include "Table/MahjongDeckManager.h"

bool UMahjongTableEngine::StartRound(const FGuiyangRuleSnapshot& RuleSnapshot, const TArray<FMahjongSeatInfo>& Seats,
    const int32 DealerSeat, const int32 ShuffleSeed, FString& OutError)
{
    // 开局前先验证冻结规则和四个有效座位，拒绝部分初始化。
    if (!UGuiyangRuleSnapshotLibrary::VerifySnapshot(RuleSnapshot) || Seats.Num() != 4 || DealerSeat < 0 || DealerSeat >= 4)
    {
        OutError = TEXT("规则快照、座位或庄家无效");
        return false;
    }
    for (const FMahjongSeatInfo& Seat : Seats)
    {
        if (!Seat.bOccupied || Seat.SeatIndex < 0 || Seat.SeatIndex >= 4)
        {
            OutError = TEXT("开局必须有四个有效座位");
            return false;
        }
    }

    // 服务端独占洗牌、发牌和唯一牌 ID 生成，客户端永远看不到牌墙顺序。
    const int32 NextRoundId = FMath::Max(1, PublicState.RoundId + 1);
    LockedRules = RuleSnapshot;
    DeckManager = NewObject<UMahjongDeckManager>(this);
    DeckManager->InitializeDeck(LockedRules.Config);
    DeckManager->ShuffleDeck(ShuffleSeed);
    if (!DeckManager->DealInitialHands(Hands, DealerSeat))
    {
        OutError = TEXT("服务端发牌失败");
        return false;
    }

    // 重建公共快照和所有本局私有累计器，保留座位带入的历史总分。
    PublicState = FMahjongPublicTableState();
    PublicState.RoundId = NextRoundId;
    PublicState.TurnId = 1;
    PublicState.ServerActionSequence = 1;
    PublicState.Phase = EMahjongTablePhase::PlayerTurn;
    PublicState.CurrentTurnSeat = DealerSeat;
    PublicState.RemainingTileCount = DeckManager->GetRemainingCount();
    PublicState.Seats = Seats;
    PublicState.StateSequence = 1;
    LastClientSequences.Init(-1, 4);
    CurrentScores.Init(0, 4);
    GangDeltas.Init(0, 4);
    SpecialJiDeltas.Init(0, 4);
    for (const FMahjongSeatInfo& Seat : Seats)
    {
        if (CurrentScores.IsValidIndex(Seat.SeatIndex)) CurrentScores[Seat.SeatIndex] = Seat.Score;
    }
    LastDiscardSeat = INDEX_NONE;
    FirstSpecialJiDiscardSequence = INDEX_NONE;
    LastDrawnTile = Hands[DealerSeat].Tiles.IsEmpty() ? FMahjongTile() : Hands[DealerSeat].Tiles.Last();
    bQiangGangWindow = false;
    PendingBuGangSeat = INDEX_NONE;
    PendingBuGangTileId = INDEX_NONE;
    PendingBuGangTile = FMahjongTile();
    SettlementResult = FMahjongSettlementResult();
    AvailableActionsBySeat.Reset();
    SubmittedReactions.Reset();
    RefreshSeatCounts();
    RebuildTurnActions();
    OutError.Reset();
    return true;
}

FMahjongActionResult UMahjongTableEngine::SubmitTurnAction(const int32 SeatIndex, const FMahjongActionRequest& Request)
{
    // 只接受服务器刚刚计算出的候选动作，客户端不能自行声明胡或杠。
    FString Error;
    if (!ValidateRequestCommon(SeatIndex, Request, Error)) return Fail(Error);
    if (PublicState.Phase != EMahjongTablePhase::PlayerTurn || PublicState.CurrentTurnSeat != SeatIndex)
        return Fail(TEXT("Current phase or seat does not allow a turn action"));

    const TArray<FMahjongAction> Available = GetAvailableActions(SeatIndex);
    const FMahjongAction* Candidate = Available.FindByPredicate([&Request](const FMahjongAction& Action)
    {
        return Action.Type == Request.Type
            && (Request.TargetTileId == INDEX_NONE || Action.TargetTile.UniqueId == Request.TargetTileId);
    });
    if (!Candidate) return Fail(TEXT("Turn action is not in the authoritative candidate list"));

    LastClientSequences[SeatIndex] = Request.ClientSequence;
    FMahjongActionResult Result;
    Result.bSuccess = true;
    Result.Action = *Candidate;

    // 自摸、补杠和暗杠分别进入结算、抢杠窗口或补牌流程。
    if (Request.Type == EMahjongActionType::Hu)
    {
        Result.Message = TEXT("Self draw accepted");
        SettleWin({ SeatIndex }, INDEX_NONE, true, LastDrawnTile);
        return Result;
    }
    if (Request.Type == EMahjongActionType::BuGang)
    {
        Result.Message = TEXT("Supplemental gang declared");
        BeginBuGang(SeatIndex, Candidate->TargetTile);
        return Result;
    }
    if (Request.Type != EMahjongActionType::AnGang) return Fail(TEXT("Unsupported turn action"));

    const int32 RuleIndex = Candidate->TargetTile.GetRuleIndex();
    TArray<FMahjongTile> Removed;
    if (!RemoveTilesByRuleIndex(Hands[SeatIndex], RuleIndex, 4, Removed))
        return Fail(TEXT("The concealed gang tiles are no longer available"));

    FMahjongMeld Meld;
    Meld.Type = EMahjongMeldType::AnGang;
    Meld.OwnerSeat = SeatIndex;
    Meld.FromSeat = SeatIndex;
    Meld.Tiles = Removed;
    Hands[SeatIndex].Melds.Add(Meld);
    FMahjongMeld PublicMeld;
    PublicMeld.Type = EMahjongMeldType::AnGang;
    PublicMeld.OwnerSeat = SeatIndex;
    PublicMeld.FromSeat = SeatIndex;
    // 暗杠公共副露只复制四张无效占位牌，客户端据此显示牌背而不泄露牌面。
    PublicMeld.Tiles.SetNum(4);
    PublicState.PublicMelds.Add(PublicMeld);
    ApplyGangScore(SeatIndex);
    ++PublicState.TurnId;
    ++PublicState.ServerActionSequence;

    FMahjongTile Replacement;
    if (!DeckManager->DrawTile(Replacement))
    {
        SettleDrawGame();
    }
    else
    {
        Hands[SeatIndex].Tiles.Add(Replacement);
        Hands[SeatIndex].Sort();
        LastDrawnTile = Replacement;
        PublicState.RemainingTileCount = DeckManager->GetRemainingCount();
        ++PublicState.StateSequence;
        RefreshSeatCounts();
        RebuildTurnActions();
    }
    Result.Message = TEXT("Concealed gang accepted and replacement tile drawn");
    return Result;
}

FMahjongActionResult UMahjongTableEngine::SubmitPlayTile(const int32 SeatIndex, const FMahjongActionRequest& Request)
{
    // UniqueId 精确选择实体牌，避免同牌面多张牌造成歧义。
    FString Error;
    if (!ValidateRequestCommon(SeatIndex, Request, Error)) return Fail(Error);
    if (Request.Type != EMahjongActionType::Play || PublicState.Phase != EMahjongTablePhase::PlayerTurn
        || PublicState.CurrentTurnSeat != SeatIndex)
    {
        return Fail(TEXT("当前阶段或座位不允许出牌"));
    }
    const int32 TileIndex = Hands[SeatIndex].Tiles.IndexOfByPredicate(
        [&Request](const FMahjongTile& Tile) { return Tile.UniqueId == Request.TargetTileId; });
    if (!Hands[SeatIndex].Tiles.IsValidIndex(TileIndex)) return Fail(TEXT("目标牌不在该玩家手牌中"));

    LastClientSequences[SeatIndex] = Request.ClientSequence;
    const FMahjongTile PlayedTile = Hands[SeatIndex].Tiles[TileIndex];
    Hands[SeatIndex].Tiles.RemoveAt(TileIndex);
    FMahjongDiscardRecord Record;
    Record.SeatIndex = SeatIndex;
    Record.Tile = PlayedTile;
    Record.Sequence = ++PublicState.ServerActionSequence;
    PublicState.Discards.Add(Record);
    // 先记录可能的冲锋鸡，再打开其他玩家的响应窗口。
    RecordSpecialJiDiscard(SeatIndex, Record);
    PublicState.LastDiscard = PlayedTile;
    LastDiscardSeat = SeatIndex;
    ++PublicState.StateSequence;
    RefreshSeatCounts();
    OpenReactionWindow(PlayedTile, SeatIndex);

    FMahjongActionResult Result;
    Result.bSuccess = true;
    Result.Message = TEXT("服务端已接受出牌");
    Result.Action.Type = EMahjongActionType::Play;
    Result.Action.SourceSeat = SeatIndex;
    Result.Action.TargetTile = PlayedTile;
    return Result;
}

FMahjongActionResult UMahjongTableEngine::SubmitReaction(const int32 SeatIndex, const FMahjongActionRequest& Request)
{
    // 每个有响应资格的座位在当前窗口只能提交一次。
    FString Error;
    if (!ValidateRequestCommon(SeatIndex, Request, Error)) return Fail(Error);
    if (PublicState.Phase != EMahjongTablePhase::WaitingForAction || !AvailableActionsBySeat.Contains(SeatIndex))
        return Fail(TEXT("当前没有该玩家的响应窗口"));
    if (SubmittedReactions.Contains(SeatIndex)) return Fail(TEXT("该响应已经提交"));

    const TArray<FMahjongAction>& Available = AvailableActionsBySeat[SeatIndex];
    const bool bAllowed = Request.Type == EMahjongActionType::Pass
        || Available.ContainsByPredicate([&Request](const FMahjongAction& Action) { return Action.Type == Request.Type; });
    if (!bAllowed) return Fail(TEXT("请求动作不在服务端候选列表中"));

    const FMahjongTile ReactionTile = bQiangGangWindow ? PendingBuGangTile : PublicState.LastDiscard;
    LastClientSequences[SeatIndex] = Request.ClientSequence;
    SubmittedReactions.Add(SeatIndex, Request);
    ++PublicState.ServerActionSequence;
    // 全部相关玩家响应后立即解析；未响应玩家由服务端超时路径自动过。
    if (SubmittedReactions.Num() == AvailableActionsBySeat.Num()) ResolveSubmittedReactions();

    FMahjongActionResult Result;
    Result.bSuccess = true;
    Result.Message = TEXT("响应已记录");
    Result.Action.Type = Request.Type;
    Result.Action.SourceSeat = SeatIndex;
    Result.Action.TargetTile = ReactionTile;
    return Result;
}

FMahjongActionResult UMahjongTableEngine::ResolveActionTimeout(const int32 ExpectedRoundId,
    const int32 ExpectedTurnId, const EMahjongTablePhase ExpectedPhase)
{
    // 定时器携带版本令牌，旧回调不能影响已经推进的新阶段。
    if (PublicState.RoundId != ExpectedRoundId || PublicState.TurnId != ExpectedTurnId
        || PublicState.Phase != ExpectedPhase)
        return Fail(TEXT("Timeout token is stale"));
    if (!LockedRules.Config.bEnableTimeoutAutoPlay)
        return Fail(TEXT("Timeout auto play is disabled by the locked rules"));

    if (PublicState.Phase == EMahjongTablePhase::PlayerTurn)
    {
        const int32 SeatIndex = PublicState.CurrentTurnSeat;
        if (!Hands.IsValidIndex(SeatIndex) || Hands[SeatIndex].Tiles.IsEmpty())
            return Fail(TEXT("Current player has no tile available for auto play"));
        const FMahjongTile* AutoTile = Hands[SeatIndex].Tiles.FindByPredicate([this](const FMahjongTile& Tile)
        {
            return LastDrawnTile.IsValid() && Tile.UniqueId == LastDrawnTile.UniqueId;
        });
        // 默认优先打出刚摸的牌，找不到时稳定回退到排序后的最后一张。
        if (!AutoTile) AutoTile = &Hands[SeatIndex].Tiles.Last();

        FMahjongActionRequest Request;
        Request.Type = EMahjongActionType::Play;
        Request.RoundId = PublicState.RoundId;
        Request.TurnId = PublicState.TurnId;
        Request.TargetTileId = AutoTile->UniqueId;
        Request.ClientSequence = LastClientSequences[SeatIndex] + 1;
        FMahjongActionResult Result = SubmitPlayTile(SeatIndex, Request);
        if (Result.bSuccess) Result.Message = TEXT("Turn timeout auto-played one tile");
        return Result;
    }

    if (PublicState.Phase == EMahjongTablePhase::WaitingForAction)
    {
        const TArray<int32> CandidateSeats = [&]()
        {
            TArray<int32> Seats;
            AvailableActionsBySeat.GetKeys(Seats);
            return Seats;
        }();
        int32 AutoPassCount = 0;
        for (const int32 SeatIndex : CandidateSeats)
        {
            if (SubmittedReactions.Contains(SeatIndex)) continue;
            FMahjongActionRequest Pass;
            Pass.Type = EMahjongActionType::Pass;
            Pass.RoundId = PublicState.RoundId;
            Pass.TurnId = PublicState.TurnId;
            Pass.ClientSequence = LastClientSequences[SeatIndex] + 1;
            LastClientSequences[SeatIndex] = Pass.ClientSequence;
            SubmittedReactions.Add(SeatIndex, Pass);
            ++PublicState.ServerActionSequence;
            ++AutoPassCount;
        }
        if (AutoPassCount == 0) return Fail(TEXT("Reaction timeout had no pending responders"));
        ResolveSubmittedReactions();

        FMahjongActionResult Result;
        Result.bSuccess = true;
        Result.Message = FString::Printf(TEXT("Reaction timeout auto-passed %d seats"), AutoPassCount);
        Result.Action.Type = EMahjongActionType::Pass;
        return Result;
    }
    return Fail(TEXT("Current phase has no action timeout"));
}

void UMahjongTableEngine::SetActionDeadlineForServer(const double DeadlineServerTimeSeconds, const int32 TimeoutSeconds)
{
    PublicState.ActionDeadlineServerTimeSeconds = FMath::Max(0.0, DeadlineServerTimeSeconds);
    PublicState.ActionTimeoutSeconds = FMath::Max(0, TimeoutSeconds);
}

bool UMahjongTableEngine::GetPrivateState(const int32 SeatIndex, FMahjongPrivatePlayerState& OutState) const
{
    if (!Hands.IsValidIndex(SeatIndex)) return false;
    OutState.RoundId = PublicState.RoundId;
    OutState.TurnId = PublicState.TurnId;
    OutState.SeatIndex = SeatIndex;
    OutState.LastAcceptedClientSequence = LastClientSequences.IsValidIndex(SeatIndex)
        ? LastClientSequences[SeatIndex] : -1;
    OutState.Hand = Hands[SeatIndex];
    OutState.StateSequence = PublicState.StateSequence;
    return true;
}

TArray<FMahjongAction> UMahjongTableEngine::GetAvailableActions(const int32 SeatIndex) const
{
    const TArray<FMahjongAction>* Actions = AvailableActionsBySeat.Find(SeatIndex);
    return Actions ? *Actions : TArray<FMahjongAction>();
}

bool UMahjongTableEngine::GetSettlementResult(FMahjongSettlementResult& OutResult) const
{
    if (PublicState.Phase != EMahjongTablePhase::Settlement) return false;
    OutResult = SettlementResult;
    return true;
}

#if WITH_DEV_AUTOMATION_TESTS
bool UMahjongTableEngine::SetHandForServerTest(const int32 SeatIndex, const FMahjongHand& Hand)
{
    if (!Hands.IsValidIndex(SeatIndex)) return false;
    Hands[SeatIndex] = Hand;
    for (FMahjongMeld& Meld : Hands[SeatIndex].Melds) Meld.OwnerSeat = SeatIndex;
    Hands[SeatIndex].Sort();
    PublicState.PublicMelds.Reset();
    for (int32 OwnerSeat = 0; OwnerSeat < Hands.Num(); ++OwnerSeat)
    {
        for (const FMahjongMeld& PrivateMeld : Hands[OwnerSeat].Melds)
        {
            FMahjongMeld PublicMeld = PrivateMeld;
            PublicMeld.OwnerSeat = OwnerSeat;
            if (PublicMeld.Type == EMahjongMeldType::AnGang)
            {
                PublicMeld.Tiles.Reset();
                PublicMeld.Tiles.SetNum(4);
            }
            PublicState.PublicMelds.Add(MoveTemp(PublicMeld));
        }
    }
    RefreshSeatCounts();
    if (PublicState.Phase == EMahjongTablePhase::PlayerTurn && PublicState.CurrentTurnSeat == SeatIndex)
        RebuildTurnActions();
    return true;
}
#endif

bool UMahjongTableEngine::ValidateRequestCommon(const int32 SeatIndex, const FMahjongActionRequest& Request, FString& OutError)
{
    // 三项检查共同阻断非法座位、过期状态和重复/倒退序号。
    if (!Hands.IsValidIndex(SeatIndex)) { OutError = TEXT("座位无效"); return false; }
    if (Request.RoundId != PublicState.RoundId || Request.TurnId != PublicState.TurnId)
    { OutError = TEXT("请求所属牌局或回合已过期"); return false; }
    if (Request.ClientSequence <= LastClientSequences[SeatIndex])
    { OutError = TEXT("客户端序号重复或倒退"); return false; }
    return true;
}

void UMahjongTableEngine::OpenReactionWindow(const FMahjongTile& Discard, const int32 DiscardSeat)
{
    // 权威服务器逐家重新计算胡、明杠和碰资格。
    bQiangGangWindow = false;
    AvailableActionsBySeat.Reset();
    SubmittedReactions.Reset();
    for (int32 Seat = 0; Seat < Hands.Num(); ++Seat)
    {
        if (Seat == DiscardSeat) continue;
        TArray<FMahjongAction> Actions;
        FMahjongHand HuHand = Hands[Seat];
        HuHand.Tiles.Add(Discard);
        if (UMahjongHuChecker::CanHu(HuHand, LockedRules.Config.bEnableQiDui))
            Actions.Add(BuildReactionAction(Seat, EMahjongActionType::Hu, Discard));
        if (UMahjongGangChecker::CanMingGang(Hands[Seat], Discard))
            Actions.Add(BuildReactionAction(Seat, EMahjongActionType::MingGang, Discard));
        if (UMahjongChiPengChecker::CanPeng(Hands[Seat], Discard))
            Actions.Add(BuildReactionAction(Seat, EMahjongActionType::Peng, Discard));
        if (!Actions.IsEmpty()) AvailableActionsBySeat.Add(Seat, MoveTemp(Actions));
    }
    // 无人可响应时不创建空等待窗口，直接轮转摸牌。
    if (AvailableActionsBySeat.IsEmpty()) AdvanceTurnAndDraw();
    else PublicState.Phase = EMahjongTablePhase::WaitingForAction;
}

void UMahjongTableEngine::BeginBuGang(const int32 SeatIndex, const FMahjongTile& Tile)
{
    // 补杠先保留原状态并开启抢杠胡窗口，确认无人胡后才真正修改副露。
    bQiangGangWindow = true;
    PendingBuGangSeat = SeatIndex;
    PendingBuGangTileId = Tile.UniqueId;
    PendingBuGangTile = Tile;
    AvailableActionsBySeat.Reset();
    SubmittedReactions.Reset();
    ++PublicState.ServerActionSequence;

    if (LockedRules.Config.bEnableQiangGangHu)
    {
        for (int32 Seat = 0; Seat < Hands.Num(); ++Seat)
        {
            if (Seat == SeatIndex) continue;
            FMahjongHand HuHand = Hands[Seat];
            HuHand.Tiles.Add(Tile);
            if (!UMahjongHuChecker::CanHu(HuHand, LockedRules.Config.bEnableQiDui)) continue;
            FMahjongAction Action;
            Action.Type = EMahjongActionType::Hu;
            Action.SourceSeat = Seat;
            Action.TargetSeat = SeatIndex;
            Action.TargetTile = Tile;
            AvailableActionsBySeat.Add(Seat, { Action });
        }
    }

    if (AvailableActionsBySeat.IsEmpty()) CompleteBuGang();
    else
    {
        PublicState.Phase = EMahjongTablePhase::WaitingForAction;
        ++PublicState.StateSequence;
    }
}

void UMahjongTableEngine::CompleteBuGang()
{
    // 再次检查手牌与碰副露仍存在，防止等待窗口期间状态漂移。
    if (!Hands.IsValidIndex(PendingBuGangSeat)) return;
    FMahjongHand& Hand = Hands[PendingBuGangSeat];
    const int32 TileIndex = Hand.Tiles.IndexOfByPredicate([this](const FMahjongTile& Tile)
    {
        return Tile.UniqueId == PendingBuGangTileId;
    });
    const int32 RuleIndex = PendingBuGangTile.GetRuleIndex();
    const int32 MeldIndex = Hand.Melds.IndexOfByPredicate([RuleIndex](const FMahjongMeld& Meld)
    {
        return Meld.Type == EMahjongMeldType::Peng && !Meld.Tiles.IsEmpty()
            && Meld.Tiles[0].GetRuleIndex() == RuleIndex;
    });
    if (!Hand.Tiles.IsValidIndex(TileIndex) || !Hand.Melds.IsValidIndex(MeldIndex))
    {
        SettleDrawGame();
        return;
    }

    const int32 GangSeat = PendingBuGangSeat;
    const FMahjongTile AddedTile = Hand.Tiles[TileIndex];
    Hand.Tiles.RemoveAt(TileIndex);
    Hand.Melds[MeldIndex].Type = EMahjongMeldType::BuGang;
    Hand.Melds[MeldIndex].Tiles.Add(AddedTile);
    if (FMahjongMeld* PublicMeld = PublicState.PublicMelds.FindByPredicate([RuleIndex, GangSeat](const FMahjongMeld& Meld)
    {
        return Meld.Type == EMahjongMeldType::Peng && Meld.OwnerSeat == GangSeat
            && !Meld.Tiles.IsEmpty() && Meld.Tiles[0].GetRuleIndex() == RuleIndex;
    }))
    {
        PublicMeld->Type = EMahjongMeldType::BuGang;
        PublicMeld->Tiles.Add(AddedTile);
    }

    bQiangGangWindow = false;
    PendingBuGangSeat = INDEX_NONE;
    PendingBuGangTileId = INDEX_NONE;
    PendingBuGangTile = FMahjongTile();
    AvailableActionsBySeat.Reset();
    SubmittedReactions.Reset();
    ApplyGangScore(GangSeat);
    PublicState.CurrentTurnSeat = GangSeat;
    ++PublicState.TurnId;

    FMahjongTile Replacement;
    if (!DeckManager->DrawTile(Replacement)) SettleDrawGame();
    else
    {
        Hand.Tiles.Add(Replacement);
        Hand.Sort();
        LastDrawnTile = Replacement;
        PublicState.Phase = EMahjongTablePhase::PlayerTurn;
        PublicState.RemainingTileCount = DeckManager->GetRemainingCount();
        ++PublicState.StateSequence;
        RefreshSeatCounts();
        RebuildTurnActions();
    }
}

void UMahjongTableEngine::ResolveQiangGangReactions(const TArray<int32>& HuSeats)
{
    // 无人抢杠则提交补杠；有人胡则按距杠家顺序决定一炮多响或最近赢家。
    if (HuSeats.IsEmpty())
    {
        CompleteBuGang();
        return;
    }

    TArray<int32> Winners = HuSeats;
    Winners.Sort([this](const int32 Left, const int32 Right)
    {
        return (Left - PendingBuGangSeat + 4) % 4 < (Right - PendingBuGangSeat + 4) % 4;
    });
    if (!LockedRules.Config.bEnableYiPaoDuoXiang) Winners.SetNum(1);

    const int32 GangSeat = PendingBuGangSeat;
    const FMahjongTile RobbedTile = PendingBuGangTile;
    FMahjongHand& GangHand = Hands[GangSeat];
    GangHand.Tiles.RemoveAll([this](const FMahjongTile& Tile) { return Tile.UniqueId == PendingBuGangTileId; });
    bQiangGangWindow = false;
    PendingBuGangSeat = INDEX_NONE;
    PendingBuGangTileId = INDEX_NONE;
    PendingBuGangTile = FMahjongTile();
    SettleWin(Winners, GangSeat, false, RobbedTile);
    RefreshSeatCounts();
}

void UMahjongTableEngine::ResolveSubmittedReactions()
{
    // 胡优先于杠/碰；其余动作再按稳定优先级和座位距离选出唯一执行者。
    TArray<int32> HuSeats;
    for (const TPair<int32, FMahjongActionRequest>& Pair : SubmittedReactions)
    {
        if (Pair.Value.Type == EMahjongActionType::Hu) HuSeats.Add(Pair.Key);
    }
    if (bQiangGangWindow)
    {
        ResolveQiangGangReactions(HuSeats);
        return;
    }
    if (!HuSeats.IsEmpty())
    {
        ResolveHuReactions(HuSeats);
        return;
    }
    const int32 WinnerSeat = FindBestReactionSeat();
    if (WinnerSeat == INDEX_NONE)
    {
        AdvanceTurnAndDraw();
        return;
    }
    ApplyClaim(WinnerSeat, SubmittedReactions[WinnerSeat].Type);
}

void UMahjongTableEngine::ResolveHuReactions(const TArray<int32>& HuSeats)
{
    // 一炮多响关闭时，仅保留出牌者下家方向最近的胡牌者。
    PublicState.WinningSeats.Reset();
    if (LockedRules.Config.bEnableYiPaoDuoXiang)
    {
        PublicState.WinningSeats = HuSeats;
        PublicState.WinningSeats.Sort([this](const int32 Left, const int32 Right)
        {
            return (Left - LastDiscardSeat + 4) % 4 < (Right - LastDiscardSeat + 4) % 4;
        });
    }
    else
    {
        int32 ClosestSeat = HuSeats[0];
        for (const int32 Seat : HuSeats)
        {
            if ((Seat - LastDiscardSeat + 4) % 4 < (ClosestSeat - LastDiscardSeat + 4) % 4) ClosestSeat = Seat;
        }
        PublicState.WinningSeats.Add(ClosestSeat);
    }
    SettleWin(PublicState.WinningSeats, LastDiscardSeat, false, PublicState.LastDiscard);
}

void UMahjongTableEngine::ApplyClaim(const int32 SeatIndex, const EMahjongActionType Type)
{
    // 从手牌移除真实牌，再把完整副露发布到公共状态并标记弃牌已被认领。
    if (Type == EMahjongActionType::Hu)
    {
        PublicState.WinningSeats = { SeatIndex };
        PublicState.Phase = EMahjongTablePhase::Settlement;
        PublicState.CurrentTurnSeat = SeatIndex;
        AvailableActionsBySeat.Reset();
        SubmittedReactions.Reset();
        ++PublicState.StateSequence;
        return;
    }

    const int32 RemoveCount = Type == EMahjongActionType::MingGang ? 3 : 2;
    TArray<FMahjongTile> Removed;
    if (!RemoveTilesByRuleIndex(Hands[SeatIndex], PublicState.LastDiscard.GetRuleIndex(), RemoveCount, Removed))
    {
        AdvanceTurnAndDraw();
        return;
    }
    FMahjongMeld Meld;
    Meld.Type = Type == EMahjongActionType::MingGang ? EMahjongMeldType::MingGang : EMahjongMeldType::Peng;
    Meld.OwnerSeat = SeatIndex;
    Meld.FromSeat = LastDiscardSeat;
    Meld.Tiles = MoveTemp(Removed);
    Meld.Tiles.Add(PublicState.LastDiscard);
    Hands[SeatIndex].Melds.Add(Meld);
    PublicState.PublicMelds.Add(Meld);
    RecordZeRenJiClaim(SeatIndex, Type);
    if (!PublicState.Discards.IsEmpty()) PublicState.Discards.Last().bClaimed = true;
    PublicState.CurrentTurnSeat = SeatIndex;
    ++PublicState.TurnId;
    PublicState.Phase = EMahjongTablePhase::PlayerTurn;
    if (Type == EMahjongActionType::MingGang)
    {
        ApplyGangScore(SeatIndex);
        FMahjongTile Replacement;
        if (DeckManager->DrawTile(Replacement))
        {
            Hands[SeatIndex].Tiles.Add(Replacement);
            Hands[SeatIndex].Sort();
            LastDrawnTile = Replacement;
        }
        else SettleDrawGame();
    }
    AvailableActionsBySeat.Reset();
    SubmittedReactions.Reset();
    PublicState.RemainingTileCount = DeckManager->GetRemainingCount();
    ++PublicState.StateSequence;
    RefreshSeatCounts();
    if (PublicState.Phase == EMahjongTablePhase::PlayerTurn) RebuildTurnActions();
}

void UMahjongTableEngine::AdvanceTurnAndDraw()
{
    // 响应窗口结束后按顺时针推进；牌墙耗尽立即进入流局结算。
    AvailableActionsBySeat.Reset();
    SubmittedReactions.Reset();
    PublicState.CurrentTurnSeat = (LastDiscardSeat + 1) % 4;
    ++PublicState.TurnId;
    FMahjongTile Drawn;
    if (!DeckManager->DrawTile(Drawn))
    {
        SettleDrawGame();
    }
    else
    {
        Hands[PublicState.CurrentTurnSeat].Tiles.Add(Drawn);
        Hands[PublicState.CurrentTurnSeat].Sort();
        LastDrawnTile = Drawn;
        PublicState.Phase = EMahjongTablePhase::PlayerTurn;
    }
    PublicState.RemainingTileCount = DeckManager->GetRemainingCount();
    ++PublicState.StateSequence;
    RefreshSeatCounts();
    if (PublicState.Phase == EMahjongTablePhase::PlayerTurn) RebuildTurnActions();
}

void UMahjongTableEngine::RebuildTurnActions()
{
    // 当前回合候选列表仅包含自摸、暗杠和补杠；普通出牌由手牌 UniqueId 验证。
    AvailableActionsBySeat.Reset();
    if (PublicState.Phase != EMahjongTablePhase::PlayerTurn || !Hands.IsValidIndex(PublicState.CurrentTurnSeat)) return;

    const int32 SeatIndex = PublicState.CurrentTurnSeat;
    TArray<FMahjongAction> Actions;
    if (UMahjongHuChecker::CanHu(Hands[SeatIndex], LockedRules.Config.bEnableQiDui))
    {
        FMahjongAction Action;
        Action.Type = EMahjongActionType::Hu;
        Action.SourceSeat = SeatIndex;
        Action.TargetTile = LastDrawnTile;
        Actions.Add(Action);
    }
    for (const int32 RuleIndex : UMahjongGangChecker::FindAnGangRuleIndices(Hands[SeatIndex]))
    {
        const FMahjongTile* Tile = Hands[SeatIndex].Tiles.FindByPredicate(
            [RuleIndex](const FMahjongTile& Item) { return Item.GetRuleIndex() == RuleIndex; });
        if (!Tile) continue;
        FMahjongAction Action;
        Action.Type = EMahjongActionType::AnGang;
        Action.SourceSeat = SeatIndex;
        Action.TargetTile = *Tile;
        Actions.Add(Action);
    }
    for (const int32 RuleIndex : UMahjongGangChecker::FindBuGangRuleIndices(Hands[SeatIndex]))
    {
        const FMahjongTile* Tile = Hands[SeatIndex].Tiles.FindByPredicate(
            [RuleIndex](const FMahjongTile& Item) { return Item.GetRuleIndex() == RuleIndex; });
        if (!Tile) continue;
        FMahjongAction Action;
        Action.Type = EMahjongActionType::BuGang;
        Action.SourceSeat = SeatIndex;
        Action.TargetTile = *Tile;
        Actions.Add(Action);
    }
    if (!Actions.IsEmpty()) AvailableActionsBySeat.Add(SeatIndex, MoveTemp(Actions));
}

void UMahjongTableEngine::SettleWin(const TArray<int32>& WinningSeats, const int32 LoserSeat,
    const bool bSelfDraw, const FMahjongTile& WinningTile)
{
    // 结算时由服务端从剩余牌墙翻鸡，并把所有分项一次性冻结进结果。
    FMahjongTile FlippedJiTile;
    if (DeckManager && DeckManager->DrawTile(FlippedJiTile))
    {
        PublicState.FlippedJiTile = FlippedJiTile;
        PublicState.RemainingTileCount = DeckManager->GetRemainingCount();
    }
    const TArray<int32> JiCounts = CountJiForSettlement(FlippedJiTile, WinningSeats, bSelfDraw, WinningTile);
    SettlementResult = UMahjongScoreCalculator::CalculateWinsWithSpecialJi(WinningSeats, LoserSeat, bSelfDraw,
        JiCounts, SpecialJiDeltas, GangDeltas, CurrentScores, LockedRules.Config);
    SettlementResult.WinningTile = WinningTile;
    SettlementResult.FlippedJiTile = FlippedJiTile;
    SettlementResult.PlayerJiCounts = JiCounts;
    SettlementResult.JiEvents = PublicState.JiEvents;
    PublicState.WinningSeats = WinningSeats;
    PublicState.CurrentTurnSeat = WinningSeats.IsEmpty() ? INDEX_NONE : WinningSeats[0];
    PublicState.Phase = EMahjongTablePhase::Settlement;
    AvailableActionsBySeat.Reset();
    SubmittedReactions.Reset();
    ++PublicState.ServerActionSequence;
    ++PublicState.StateSequence;
}

void UMahjongTableEngine::SettleDrawGame()
{
    // 流局仍保留已发生的鸡事件和杠/特殊鸡分，便于完整复核。
    SettlementResult = FMahjongSettlementResult();
    SettlementResult.bDrawGame = true;
    SettlementResult.JiEvents = PublicState.JiEvents;
    SettlementResult.PlayerResults.SetNum(4);
    for (int32 Seat = 0; Seat < 4; ++Seat)
    {
        FMahjongPlayerScoreResult& Player = SettlementResult.PlayerResults[Seat];
        Player.SeatIndex = Seat;
        Player.GangScoreDelta = GangDeltas.IsValidIndex(Seat) ? GangDeltas[Seat] : 0;
        Player.SpecialJiScoreDelta = SpecialJiDeltas.IsValidIndex(Seat) ? SpecialJiDeltas[Seat] : 0;
        Player.TotalDelta = Player.GangScoreDelta + Player.SpecialJiScoreDelta;
        Player.TotalScore = (CurrentScores.IsValidIndex(Seat) ? CurrentScores[Seat] : 0) + Player.TotalDelta;
    }
    PublicState.WinningSeats.Reset();
    PublicState.CurrentTurnSeat = INDEX_NONE;
    PublicState.Phase = EMahjongTablePhase::Settlement;
    PublicState.RemainingTileCount = DeckManager ? DeckManager->GetRemainingCount() : 0;
    AvailableActionsBySeat.Reset();
    SubmittedReactions.Reset();
    ++PublicState.ServerActionSequence;
    ++PublicState.StateSequence;
}

void UMahjongTableEngine::ApplyGangScore(const int32 GangSeat)
{
    if (!GangDeltas.IsValidIndex(GangSeat)) return;
    for (int32 Seat = 0; Seat < GangDeltas.Num(); ++Seat)
    {
        if (Seat == GangSeat) continue;
        GangDeltas[Seat] -= LockedRules.Config.GangScore;
        GangDeltas[GangSeat] += LockedRules.Config.GangScore;
    }
}

void UMahjongTableEngine::RecordSpecialJiDiscard(const int32 SeatIndex, const FMahjongDiscardRecord& Record)
{
    if (FirstSpecialJiDiscardSequence != INDEX_NONE || !IsSpecialJiTarget(Record.Tile, false)) return;
    FirstSpecialJiDiscardSequence = Record.Sequence;
    if (!LockedRules.Config.bEnableChongFengJi) return;

    FMahjongJiEvent Event;
    Event.Type = EMahjongJiEventType::ChongFeng;
    Event.Tile = Record.Tile;
    Event.ActorSeat = SeatIndex;
    Event.ValueUnits = UGuiyangJiCalculator::IsWuGuJi(Record.Tile)
        ? LockedRules.Config.WuGuChongFengJiValue : LockedRules.Config.ChongFengJiValue;
    Event.DiscardSequence = Record.Sequence;
    PublicState.JiEvents.Add(Event);
}

void UMahjongTableEngine::RecordZeRenJiClaim(const int32 ClaimSeat, const EMahjongActionType ClaimType)
{
    if (!LockedRules.Config.bEnableZeRenJi || (ClaimType != EMahjongActionType::Peng
        && ClaimType != EMahjongActionType::MingGang) || PublicState.Discards.IsEmpty()) return;
    const FMahjongDiscardRecord& Discard = PublicState.Discards.Last();
    if (Discard.Sequence != FirstSpecialJiDiscardSequence || !IsSpecialJiTarget(Discard.Tile, true)) return;

    const int32 Units = UGuiyangJiCalculator::IsWuGuJi(Discard.Tile)
        ? LockedRules.Config.WuGuZeRenJiValue : LockedRules.Config.ZeRenJiValue;
    const int32 Score = Units * LockedRules.Config.JiScore;
    if (!SpecialJiDeltas.IsValidIndex(ClaimSeat) || !SpecialJiDeltas.IsValidIndex(Discard.SeatIndex)) return;
    SpecialJiDeltas[ClaimSeat] += Score;
    SpecialJiDeltas[Discard.SeatIndex] -= Score;

    FMahjongJiEvent Event;
    Event.Type = EMahjongJiEventType::ZeRen;
    Event.Tile = Discard.Tile;
    Event.ActorSeat = ClaimSeat;
    Event.TargetSeat = Discard.SeatIndex;
    Event.ValueUnits = Units;
    Event.DiscardSequence = Discard.Sequence;
    PublicState.JiEvents.Add(Event);
}

bool UMahjongTableEngine::IsSpecialJiTarget(const FMahjongTile& Tile, const bool bForZeRen) const
{
    if (UGuiyangJiCalculator::IsBasicJi(Tile)) return true;
    if (!LockedRules.Config.bEnableWuGuJi || !UGuiyangJiCalculator::IsWuGuJi(Tile)) return false;
    return bForZeRen ? LockedRules.Config.bWuGuCanZeRen : LockedRules.Config.bWuGuCanChongFeng;
}

TArray<int32> UMahjongTableEngine::CountJiForSettlement(const FMahjongTile& FlippedJiTile,
    const TArray<int32>& WinningSeats, const bool bSelfDraw, const FMahjongTile& WinningTile) const
{
    TArray<int32> Counts;
    Counts.Reserve(Hands.Num());
    for (int32 Seat = 0; Seat < Hands.Num(); ++Seat)
    {
        FMahjongHand ScoringHand = Hands[Seat];
        if (!bSelfDraw && WinningSeats.Contains(Seat)) ScoringHand.Tiles.Add(WinningTile);
        int32 Units = UGuiyangJiCalculator::CountJiUnits(ScoringHand, FlippedJiTile, LockedRules.Config);
        const bool bCountDiscards = LockedRules.Config.JiCountingScope == EMahjongJiCountingScope::HandAndDiscard
            || LockedRules.Config.JiCountingScope == EMahjongJiCountingScope::HandMeldAndDiscard;
        if (bCountDiscards)
        {
            for (const FMahjongDiscardRecord& Discard : PublicState.Discards)
            {
                if (Discard.SeatIndex == Seat && !Discard.bClaimed)
                    Units += UGuiyangJiCalculator::CountTileJiUnits(Discard.Tile, FlippedJiTile, LockedRules.Config);
            }
        }
        for (const FMahjongJiEvent& Event : PublicState.JiEvents)
        {
            if (Event.Type == EMahjongJiEventType::ChongFeng && Event.ActorSeat == Seat) Units += Event.ValueUnits;
        }
        Counts.Add(Units);
    }
    return Counts;
}

void UMahjongTableEngine::RefreshSeatCounts()
{
    for (FMahjongSeatInfo& Seat : PublicState.Seats)
    {
        if (Hands.IsValidIndex(Seat.SeatIndex)) Seat.HandTileCount = Hands[Seat.SeatIndex].Tiles.Num();
    }
}

FMahjongAction UMahjongTableEngine::BuildReactionAction(const int32 SeatIndex, const EMahjongActionType Type, const FMahjongTile& Discard) const
{
    FMahjongAction Action;
    Action.Type = Type;
    Action.SourceSeat = SeatIndex;
    Action.TargetSeat = LastDiscardSeat;
    Action.TargetTile = Discard;
    return Action;
}

int32 UMahjongTableEngine::FindBestReactionSeat() const
{
    int32 BestSeat = INDEX_NONE;
    int32 BestPriority = 0;
    int32 BestDistance = 5;
    for (const TPair<int32, FMahjongActionRequest>& Pair : SubmittedReactions)
    {
        const int32 Priority = GetReactionPriority(Pair.Value.Type);
        const int32 Distance = (Pair.Key - LastDiscardSeat + 4) % 4;
        if (Priority > BestPriority || (Priority == BestPriority && Priority > 0 && Distance < BestDistance))
        {
            BestSeat = Pair.Key;
            BestPriority = Priority;
            BestDistance = Distance;
        }
    }
    return BestPriority > 0 ? BestSeat : INDEX_NONE;
}

int32 UMahjongTableEngine::GetReactionPriority(const EMahjongActionType Type)
{
    if (Type == EMahjongActionType::Hu) return 3;
    if (Type == EMahjongActionType::MingGang) return 2;
    if (Type == EMahjongActionType::Peng) return 1;
    return 0;
}

bool UMahjongTableEngine::RemoveTilesByRuleIndex(FMahjongHand& Hand, const int32 RuleIndex, const int32 Count, TArray<FMahjongTile>& OutRemoved)
{
    OutRemoved.Reset();
    for (int32 Index = Hand.Tiles.Num() - 1; Index >= 0 && OutRemoved.Num() < Count; --Index)
    {
        if (Hand.Tiles[Index].GetRuleIndex() == RuleIndex)
        {
            OutRemoved.Add(Hand.Tiles[Index]);
            Hand.Tiles.RemoveAt(Index);
        }
    }
    return OutRemoved.Num() == Count;
}

FMahjongActionResult UMahjongTableEngine::Fail(const FString& Message) const
{
    FMahjongActionResult Result;
    Result.Message = Message;
    return Result;
}
