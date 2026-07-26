#include "Rules/MahjongScoreCalculator.h"
#include "GuiyangMahjongCore.h"

FMahjongSettlementResult UMahjongScoreCalculator::CalculateWin(const int32 WinnerSeat, const int32 LoserSeat,
    const bool bSelfDraw, const TArray<int32>& JiCounts, const TArray<int32>& GangDeltas,
    const TArray<int32>& CurrentScores, const FMahjongRuleConfig& Config)
{
    // 单赢家入口统一转为多赢家算法，避免两套结算逻辑分叉。
    return CalculateWins({ WinnerSeat }, LoserSeat, bSelfDraw, JiCounts, GangDeltas, CurrentScores, Config);
}

FMahjongSettlementResult UMahjongScoreCalculator::CalculateWins(const TArray<int32>& WinningSeats,
    const int32 LoserSeat, const bool bSelfDraw, const TArray<int32>& JiCounts,
    const TArray<int32>& GangDeltas, const TArray<int32>& CurrentScores, const FMahjongRuleConfig& Config)
{
    return CalculateWinsWithSpecialJi(WinningSeats, LoserSeat, bSelfDraw, JiCounts, {},
        GangDeltas, CurrentScores, Config);
}

FMahjongSettlementResult UMahjongScoreCalculator::CalculateWinsWithSpecialJi(const TArray<int32>& WinningSeats,
    const int32 LoserSeat, const bool bSelfDraw, const TArray<int32>& JiCounts,
    const TArray<int32>& SpecialJiDeltas, const TArray<int32>& GangDeltas,
    const TArray<int32>& CurrentScores, const FMahjongRuleConfig& Config)
{
    FMahjongSettlementResult Result;
    Result.WinningSeats = WinningSeats;
    Result.WinnerSeat = WinningSeats.IsEmpty() ? INDEX_NONE : WinningSeats[0];
    Result.LoserSeat = bSelfDraw ? INDEX_NONE : LoserSeat;
    Result.bSelfDraw = bSelfDraw;
    Result.PlayerResults.SetNum(4);

    // 在写入任何分数前完整校验座位组合；非法请求按流局返回。
    const bool bInvalidWinner = WinningSeats.IsEmpty() || WinningSeats.ContainsByPredicate(
        [](const int32 Seat) { return Seat < 0 || Seat >= 4; });
    if (bInvalidWinner || (bSelfDraw && WinningSeats.Num() != 1)
        || (!bSelfDraw && (LoserSeat < 0 || LoserSeat >= 4 || WinningSeats.Contains(LoserSeat))))
    {
        Result.bDrawGame = true;
        UE_LOG(LogMahjongCore, Warning, TEXT("Settlement rejected: invalid winners or loser"));
        return Result;
    }

    // 先写入每个座位的独立特殊鸡和杠分，缺失数组项按零处理。
    for (int32 Seat = 0; Seat < 4; ++Seat)
    {
        Result.PlayerResults[Seat].SeatIndex = Seat;
        Result.PlayerResults[Seat].SpecialJiScoreDelta = SpecialJiDeltas.IsValidIndex(Seat) ? SpecialJiDeltas[Seat] : 0;
        Result.PlayerResults[Seat].GangScoreDelta = GangDeltas.IsValidIndex(Seat) ? GangDeltas[Seat] : 0;
    }

    // 自摸由三家分别支付；点炮时放炮者对每名合法赢家支付三倍单位。
    const int32 Unit = Config.BaseScore * (bSelfDraw ? Config.ZiMoMultiplier : Config.DianPaoMultiplier);
    if (bSelfDraw)
    {
        const int32 WinnerSeat = WinningSeats[0];
        for (int32 Seat = 0; Seat < 4; ++Seat)
        {
            if (Seat == WinnerSeat) continue;
            Result.PlayerResults[Seat].BaseScoreDelta -= Unit;
            Result.PlayerResults[WinnerSeat].BaseScoreDelta += Unit;
        }
    }
    else
    {
        for (const int32 WinnerSeat : WinningSeats)
        {
            Result.PlayerResults[LoserSeat].BaseScoreDelta -= Unit * 3;
            Result.PlayerResults[WinnerSeat].BaseScoreDelta += Unit * 3;
        }
    }

    // 两两比较鸡数并进行零和转移，确保整桌鸡分增减总和为零。
    for (int32 A = 0; A < 4; ++A)
    {
        for (int32 B = A + 1; B < 4; ++B)
        {
            const int32 ACount = JiCounts.IsValidIndex(A) ? JiCounts[A] : 0;
            const int32 BCount = JiCounts.IsValidIndex(B) ? JiCounts[B] : 0;
            const int32 Delta = (ACount - BCount) * Config.JiScore;
            Result.PlayerResults[A].JiScoreDelta += Delta;
            Result.PlayerResults[B].JiScoreDelta -= Delta;
        }
    }

    // 汇总本局各分项，并叠加到进入本局前的累计分。
    for (int32 Seat = 0; Seat < 4; ++Seat)
    {
        FMahjongPlayerScoreResult& Player = Result.PlayerResults[Seat];
        Player.TotalDelta = Player.BaseScoreDelta + Player.JiScoreDelta
            + Player.SpecialJiScoreDelta + Player.GangScoreDelta;
        Player.TotalScore = (CurrentScores.IsValidIndex(Seat) ? CurrentScores[Seat] : 0) + Player.TotalDelta;
    }
    UE_LOG(LogMahjongCore, Log, TEXT("Round settlement completed: %s"), *Result.ToDebugString());
    return Result;
}
