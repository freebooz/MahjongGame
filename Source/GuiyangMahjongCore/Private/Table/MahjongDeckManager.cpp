#include "Table/MahjongDeckManager.h"
#include "GuiyangMahjongCore.h"

namespace
{
    constexpr int32 WallSideCount = 4;
    // Absolute seats are South, East, North, West. A 108-tile wall must use
    // complete two-tile stacks, so South/North have 14 stacks and East/West 13.
    constexpr int32 WallTilesByAbsoluteSide[WallSideCount] = { 28, 26, 28, 26 };
    // Physical clockwise segment order is South, West, North, East.
    constexpr int32 WallTilesByClockwiseSegment[WallSideCount] = { 28, 26, 28, 26 };

    int32 GetClockwiseSegmentStart(const int32 Segment)
    {
        int32 Start = 0;
        for (int32 Index = 0; Index < Segment; ++Index)
        {
            Start += WallTilesByClockwiseSegment[Index];
        }
        return Start;
    }
}

void UMahjongDeckManager::InitializeDeck(const FMahjongRuleConfig& RuleConfig)
{
    if (RuleConfig.TileSetMode != EMahjongTileSetMode::Suited108)
    {
        UE_LOG(LogMahjongCore, Warning, TEXT("Guiyang rules force Suited108; ignoring legacy tile-set mode"));
    }
    Deck.Reset(108);
    ClockwiseDrawStartIndex = 0;
    ClockwiseDrawOffset = 0;
    WallBreakSide = 0;
    WallBreakStackFromRight = 0;
    int32 UniqueId = 0;

    for (const EMahjongSuit Suit : { EMahjongSuit::Characters, EMahjongSuit::Bamboo, EMahjongSuit::Dots })
    {
        for (int32 Rank = 1; Rank <= 9; ++Rank)
        {
            for (int32 Copy = 0; Copy < 4; ++Copy)
            {
                FMahjongTile& Tile = Deck.AddDefaulted_GetRef();
                Tile.Suit = Suit;
                Tile.Type = EMahjongTileType::Number;
                Tile.Rank = Rank;
                Tile.UniqueId = UniqueId++;
            }
        }
    }

    UE_LOG(LogMahjongCore, Log, TEXT("牌墙初始化完成，共 %d 张牌"), Deck.Num());
}

void UMahjongDeckManager::InitializeStandardDeck()
{
    InitializeDeck(FMahjongRuleConfig());
}

void UMahjongDeckManager::ShuffleDeck(const int32 Seed)
{
    FRandomStream Random(Seed);
    for (int32 Index = Deck.Num() - 1; Index > 0; --Index)
    {
        const int32 SwapIndex = Random.RandRange(0, Index);
        Deck.Swap(Index, SwapIndex);
    }
    ClockwiseDrawStartIndex = 0;
    ClockwiseDrawOffset = 0;
    UE_LOG(LogMahjongCore, Log, TEXT("服务端洗牌完成，随机种子=%d"), Seed);
}

void UMahjongDeckManager::ConfigureWallBreak(const int32 DealerSeat, const int32 DiceTotal)
{
    if (Deck.IsEmpty() || DealerSeat < 0 || DealerSeat >= 4 || DiceTotal <= 0)
    {
        ClockwiseDrawStartIndex = 0;
        ClockwiseDrawOffset = 0;
        WallBreakSide = 0;
        WallBreakStackFromRight = 0;
        return;
    }

    // Count people counter-clockwise from the dealer (dealer is 1).
    WallBreakSide = (DealerSeat + DiceTotal - 1) % WallSideCount;
    // Count stacks from that wall's right end toward its left.
    const int32 StacksPerSide =
        WallTilesByAbsoluteSide[WallBreakSide] / 2;
    WallBreakStackFromRight = (DiceTotal - 1) % StacksPerSide + 1;

    // Physical clockwise side order is South -> West -> North -> East, whereas
    // player seat indices increase South -> East -> North -> West.
    const int32 ClockwiseSideSegment =
        (WallSideCount - WallBreakSide) % WallSideCount;
    ClockwiseDrawStartIndex =
        (GetClockwiseSegmentStart(ClockwiseSideSegment)
            + WallBreakStackFromRight * 2)
        % Deck.Num();
    ClockwiseDrawOffset = 0;
}

bool UMahjongDeckManager::DrawTile(FMahjongTile& OutTile)
{
    if (ClockwiseDrawOffset < 0 || ClockwiseDrawOffset >= Deck.Num())
    {
        UE_LOG(LogMahjongCore, Log, TEXT("牌墙已空，触发流局检测"));
        return false;
    }
    // 顺抓逆打：该游标从开门缺口顺时针推进，玩家轮流使用另一套逆时针座次游标。
    const int32 PhysicalWallIndex =
        (ClockwiseDrawStartIndex + ClockwiseDrawOffset) % Deck.Num();
    OutTile = Deck[PhysicalWallIndex];
    ++ClockwiseDrawOffset;
    return true;
}

bool UMahjongDeckManager::DealInitialHands(TArray<FMahjongHand>& OutHands, const int32 DealerSeat)
{
    if (DealerSeat < 0 || DealerSeat >= 4 || GetRemainingCount() < 53)
    {
        UE_LOG(LogMahjongCore, Warning, TEXT("发牌失败：庄家座位或剩余牌数非法，庄家=%d，剩余=%d"), DealerSeat, GetRemainingCount());
        return false;
    }
    OutHands.SetNum(4);
    for (FMahjongHand& Hand : OutHands)
    {
        Hand.Tiles.Reset();
        Hand.Melds.Reset();
    }
    // 庄家先抓；玩家按逆时针座次轮流，每轮抓两墩（四张），三轮后各抓一张。
    for (int32 Pass = 0; Pass < 3; ++Pass)
    {
        for (int32 SeatOffset = 0; SeatOffset < 4; ++SeatOffset)
        {
            const int32 Seat = (DealerSeat + SeatOffset) % 4;
            for (int32 TileInGroup = 0; TileInGroup < 4; ++TileInGroup)
            {
                FMahjongTile Tile;
                DrawTile(Tile);
                OutHands[Seat].Tiles.Add(Tile);
            }
        }
    }
    for (int32 SeatOffset = 0; SeatOffset < 4; ++SeatOffset)
    {
        const int32 Seat = (DealerSeat + SeatOffset) % 4;
        FMahjongTile Tile;
        DrawTile(Tile);
        OutHands[Seat].Tiles.Add(Tile);
    }
    FMahjongTile DealerExtra;
    DrawTile(DealerExtra);
    OutHands[DealerSeat].Tiles.Add(DealerExtra);
    for (FMahjongHand& Hand : OutHands) Hand.Sort();
    UE_LOG(LogMahjongCore, Log, TEXT("初始发牌完成：庄家座位=%d，庄家14张，其余玩家13张"), DealerSeat);
    return true;
}
