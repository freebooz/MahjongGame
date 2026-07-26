#include "Rules/MahjongGangChecker.h"


bool UMahjongGangChecker::CanMingGang(const FMahjongHand& Hand, const FMahjongTile& Discard)
{
    // 明杠必须由手中三张相同牌加上他人的一张弃牌组成。
    const int32 Target = Discard.GetRuleIndex();
    if (Target == INDEX_NONE) return false;
    int32 MatchCount = 0;
    for (const FMahjongTile& Tile : Hand.Tiles) if (Tile.GetRuleIndex() == Target) ++MatchCount;
    return MatchCount >= 3;
}

TArray<int32> UMahjongGangChecker::FindAnGangRuleIndices(const FMahjongHand& Hand)
{
    // 使用固定 34 槽数组计数，规则索引可直接作为下标。
    int32 Counts[34] = {};
    for (const FMahjongTile& Tile : Hand.Tiles)
    {
        const int32 Index = Tile.GetRuleIndex();
        if (Index != INDEX_NONE) ++Counts[Index];
    }
    TArray<int32> Result;
    for (int32 Index = 0; Index < 34; ++Index) if (Counts[Index] == 4) Result.Add(Index);
    return Result;
}

TArray<int32> UMahjongGangChecker::FindBuGangRuleIndices(const FMahjongHand& Hand)
{
    // 先建立已碰牌索引集合，再查找手中可补成杠的第四张。
    TSet<int32> PengIndices;
    for (const FMahjongMeld& Meld : Hand.Melds)
    {
        if (Meld.Type == EMahjongMeldType::Peng && !Meld.Tiles.IsEmpty()) PengIndices.Add(Meld.Tiles[0].GetRuleIndex());
    }
    TArray<int32> Result;
    for (const FMahjongTile& Tile : Hand.Tiles)
    {
        const int32 Index = Tile.GetRuleIndex();
        if (PengIndices.Contains(Index)) Result.AddUnique(Index);
    }
    return Result;
}
