#pragma once

#include "CoreMinimal.h"
#include "Core/MahjongTypes.h"
#include "UObject/Object.h"
#include "MahjongDeckManager.generated.h"

/** 服务端牌墙管理器。负责创建、洗牌、摸牌和初始发牌；客户端不得实例化它决定牌序。 */
UCLASS(BlueprintType)
class GUIYANGMAHJONGCORE_API UMahjongDeckManager : public UObject
{
    GENERATED_BODY()
public:
    /** 按规则配置重建牌墙。默认配置为贵阳三门数牌 108 张。执行端：服务端。 */
    UFUNCTION(BlueprintCallable, Category="麻将|牌墙") void InitializeDeck(const FMahjongRuleConfig& RuleConfig);
    /** 重建标准 136 张牌墙并把摸牌位置归零。执行端：服务端。 */
    UFUNCTION(BlueprintCallable, Category="麻将|牌墙") void InitializeStandardDeck();
    /** 使用服务端种子进行 Fisher-Yates 洗牌。测试可传固定种子以复现牌局。 */
    UFUNCTION(BlueprintCallable, Category="麻将|牌墙") void ShuffleDeck(int32 Seed);
    /** 按骰子点数确定开门：逆时针数到牌墙，从右向左数墩，第 N+1 墩开始顺时针抓。 */
    void ConfigureWallBreak(int32 DealerSeat, int32 DiceTotal);
    /** 从开门缺口沿牌墙顺时针摸牌；数墩方向始终从该面牌墙右端向左。 */
    UFUNCTION(BlueprintCallable, Category="麻将|牌墙") bool DrawTile(FMahjongTile& OutTile);

    bool DealInitialHands(TArray<FMahjongHand>& OutHands, int32 DealerSeat);
    int32 GetRemainingCount() const { return Deck.Num() - ClockwiseDrawOffset; }
    /** 从开门缺口起已经顺时针消耗的牌数。 */
    int32 GetClockwiseDrawOffset() const { return ClockwiseDrawOffset; }
    int32 GetWallBreakSide() const { return WallBreakSide; }
    int32 GetWallBreakStackFromRight() const { return WallBreakStackFromRight; }
    const TArray<FMahjongTile>& GetDeckForServerTest() const { return Deck; }

private:
    UPROPERTY() TArray<FMahjongTile> Deck;
    UPROPERTY() int32 ClockwiseDrawStartIndex = 0;
    UPROPERTY() int32 WallBreakSide = 0;
    UPROPERTY() int32 WallBreakStackFromRight = 0;
    /** 顺时针抓牌游标，与逆时针玩家行动座次游标相互独立。 */
    UPROPERTY() int32 ClockwiseDrawOffset = 0;
};
