#pragma once

#include "CoreMinimal.h"
#include "Core/MahjongTypes.h"
#include "UObject/Object.h"
#include "GuiyangJiCalculator.generated.h"

/**
 * 贵阳鸡牌基础计算器。
 * 负责识别幺鸡、翻鸡和黑八并按冻结配置换算单位；冲锋鸡、责任鸡及内外牌区归属由权威牌桌处理。
 */
UCLASS()
class GUIYANGMAHJONGCORE_API UGuiyangJiCalculator : public UObject
{
    GENERATED_BODY()
public:
    /** 判断单牌是否为固定幺鸡（一条）。 */
    UFUNCTION(BlueprintPure, Category="麻将|捉鸡") static bool IsBasicJi(const FMahjongTile& Tile);
    /** 根据结算翻牌返回同花色循环的下一张鸡牌规则索引；无效翻牌返回 INDEX_NONE。 */
    UFUNCTION(BlueprintPure, Category="麻将|捉鸡") static int32 GetFlippedJiRuleIndex(const FMahjongTile& FlippedTile);
    /** 兼容入口：按实体张数统计手牌与副露中的幺鸡、翻鸡，不应用单位倍率。 */
    UFUNCTION(BlueprintPure, Category="麻将|捉鸡") static int32 CountJi(const FMahjongHand& Hand, const FMahjongTile& FlippedTile);
    /** 判断单牌是否为黑八（八筒/乌骨鸡）。 */
    static bool IsWuGuJi(const FMahjongTile& Tile);
    /** 按配置计算一张牌的鸡单位；同牌命中多种鸡时取最高值，避免重复计分。 */
    static int32 CountTileJiUnits(const FMahjongTile& Tile, const FMahjongTile& FlippedTile,
        const FMahjongRuleConfig& Config);
    /** 按配置范围累计手牌以及可选副露中的鸡单位；弃牌区由牌桌结算统一归属。 */
    static int32 CountJiUnits(const FMahjongHand& Hand, const FMahjongTile& FlippedTile,
        const FMahjongRuleConfig& Config);
};
