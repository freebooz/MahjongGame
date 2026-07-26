#include "Rules/GuiyangJiCalculator.h"


bool UGuiyangJiCalculator::IsBasicJi(const FMahjongTile& Tile)
{
    // 贵阳捉鸡的固定基础鸡为幺鸡（一条）。
    return Tile.Type == EMahjongTileType::Number && Tile.Suit == EMahjongSuit::Bamboo && Tile.Rank == 1;
}

int32 UGuiyangJiCalculator::GetFlippedJiRuleIndex(const FMahjongTile& FlippedTile)
{
    const int32 Index = FlippedTile.GetRuleIndex();
    if (Index < 0) return INDEX_NONE;
    // 序数牌在同花色内循环，风牌和三元牌分别在各自分组内循环。
    if (Index < 27) return (Index / 9) * 9 + (Index + 1) % 9;
    if (Index <= 30) return 27 + (Index - 27 + 1) % 4;
    return 31 + (Index - 31 + 1) % 3;
}

int32 UGuiyangJiCalculator::CountJi(const FMahjongHand& Hand, const FMahjongTile& FlippedTile)
{
    const int32 FlippedJiIndex = GetFlippedJiRuleIndex(FlippedTile);
    int32 Count = 0;
    // 手牌与副露使用同一判断，避免两条路径产生计数差异。
    auto CountTile = [&Count, FlippedJiIndex](const FMahjongTile& Tile)
    {
        if (IsBasicJi(Tile) || Tile.GetRuleIndex() == FlippedJiIndex) ++Count;
    };
    for (const FMahjongTile& Tile : Hand.Tiles) CountTile(Tile);
    for (const FMahjongMeld& Meld : Hand.Melds) for (const FMahjongTile& Tile : Meld.Tiles) CountTile(Tile);
    return Count;
}

bool UGuiyangJiCalculator::IsWuGuJi(const FMahjongTile& Tile)
{
    return Tile.Type == EMahjongTileType::Number && Tile.Suit == EMahjongSuit::Dots && Tile.Rank == 8;
}

int32 UGuiyangJiCalculator::CountTileJiUnits(const FMahjongTile& Tile, const FMahjongTile& FlippedTile,
    const FMahjongRuleConfig& Config)
{
    int32 Units = 0;
    // 同一张牌命中多种鸡时取最高单位，避免叠加重复计分。
    if (IsBasicJi(Tile)) Units = FMath::Max(Units, Config.BasicJiValue);
    if (Config.bEnableWuGuJi && IsWuGuJi(Tile)) Units = FMath::Max(Units, Config.WuGuJiValue);
    if (Tile.GetRuleIndex() == GetFlippedJiRuleIndex(FlippedTile))
        Units = FMath::Max(Units, Config.FlippedJiValue);
    return Units;
}

int32 UGuiyangJiCalculator::CountJiUnits(const FMahjongHand& Hand, const FMahjongTile& FlippedTile,
    const FMahjongRuleConfig& Config)
{
    int32 Units = 0;
    for (const FMahjongTile& Tile : Hand.Tiles) Units += CountTileJiUnits(Tile, FlippedTile, Config);
    // 计数范围由规则快照决定，保证一场比赛中途修改配置不会影响结果。
    const bool bCountMelds = Config.JiCountingScope == EMahjongJiCountingScope::HandAndMeld
        || Config.JiCountingScope == EMahjongJiCountingScope::HandMeldAndDiscard;
    if (bCountMelds)
    {
        for (const FMahjongMeld& Meld : Hand.Melds)
            for (const FMahjongTile& Tile : Meld.Tiles) Units += CountTileJiUnits(Tile, FlippedTile, Config);
    }
    return Units;
}
