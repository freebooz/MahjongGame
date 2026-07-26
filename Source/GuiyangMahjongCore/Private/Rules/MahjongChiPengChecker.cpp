#include "Rules/MahjongChiPengChecker.h"


bool UMahjongChiPengChecker::CanPeng(const FMahjongHand& Hand, const FMahjongTile& Discard)
{
    // 碰牌要求手中至少有两张与弃牌规则索引相同的牌。
    const int32 Target = Discard.GetRuleIndex();
    if (Target == INDEX_NONE) return false;
    int32 MatchCount = 0;
    for (const FMahjongTile& Tile : Hand.Tiles) if (Tile.GetRuleIndex() == Target) ++MatchCount;
    return MatchCount >= 2;
}

bool UMahjongChiPengChecker::CanChi(const FMahjongHand& Hand, const FMahjongTile& Discard)
{
    const int32 Target = Discard.GetRuleIndex();
    if (Target < 0 || Target >= 27) return false;
    // 吃牌只关心某个序数牌是否存在，集合可同时去除重复牌。
    TSet<int32> Indices;
    for (const FMahjongTile& Tile : Hand.Tiles) Indices.Add(Tile.GetRuleIndex());
    const int32 Rank = Target % 9;
    // 依次检查弃牌位于顺子右端、中间和左端的三种组合。
    return (Rank >= 2 && Indices.Contains(Target - 2) && Indices.Contains(Target - 1))
        || (Rank >= 1 && Rank <= 7 && Indices.Contains(Target - 1) && Indices.Contains(Target + 1))
        || (Rank <= 6 && Indices.Contains(Target + 1) && Indices.Contains(Target + 2));
}
