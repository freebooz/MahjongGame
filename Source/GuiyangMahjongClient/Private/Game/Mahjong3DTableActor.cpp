#include "Game/Mahjong3DTableActor.h"

#include "Components/SceneComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/StaticMesh.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Materials/MaterialInterface.h"

namespace
{
    constexpr float TileWidth = 44.0f;
    constexpr float TileHeight = 62.0f;
    constexpr float TileDepth = 30.0f;
    // The mesh has rounded/beveled side edges, so exact bounding-box contact still looks gapped.
    // A small overlap produces the tightly packed physical-table appearance requested.
    constexpr float TileTightPitch = TileWidth - 2.0f;
    constexpr float TileTightLongPitch = TileHeight - 2.0f;
    constexpr float Mahjong50ModelWidth = 3.6f;

    FVector RotateAroundTable(const FVector& Position, const int32 RelativeSeat)
    {
        switch (RelativeSeat)
        {
        case 1: return FVector(-Position.Y, Position.X, Position.Z);
        case 2: return FVector(-Position.X, -Position.Y, Position.Z);
        case 3: return FVector(Position.Y, -Position.X, Position.Z);
        default: return Position;
        }
    }

    FRotator RotateAroundTable(const FRotator& Rotation, const int32 RelativeSeat)
    {
        return FRotator(Rotation.Pitch, Rotation.Yaw + 90.0f * RelativeSeat, Rotation.Roll);
    }
}

AMahjong3DTableActor::AMahjong3DTableActor()
{
    PrimaryActorTick.bCanEverTick = false;
    SceneRoot = CreateDefaultSubobject<USceneComponent>(TEXT("SceneRoot"));
    SetRootComponent(SceneRoot);
    InitializePresentationAssets();
}

void AMahjong3DTableActor::BeginPlay()
{
    Super::BeginPlay();

    // This actor is owned by the client room presentation. Its resource caches are transient and therefore
    // must not rely solely on values copied from the native constructor/CDO during map loading.
    // Rebuilding them here also replaces stale references saved by older room-map revisions.
    InitializePresentationAssets();

    int32 LoadedTileMeshCount = 0;
    for (const UStaticMesh* Mesh : TileMeshes)
    {
        LoadedTileMeshCount += Mesh ? 1 : 0;
    }
    UE_LOG(LogTemp, Log, TEXT("Mahjong table presentation assets initialized: tile meshes=%d/27"),
        LoadedTileMeshCount);
}

void AMahjong3DTableActor::InitializePresentationAssets()
{
    CubeMesh = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Cube.Cube"));
    static const TCHAR* TileAssetNames[] = {
        TEXT("Characters_1"), TEXT("Characters_2"), TEXT("Characters_3"),
        TEXT("Characters_4"), TEXT("Characters_5"), TEXT("Characters_6"),
        TEXT("Characters_7"), TEXT("Characters_8"), TEXT("Characters_9"),
        TEXT("Bamboo_1"), TEXT("Bamboo_2"), TEXT("Bamboo_3"),
        TEXT("Bamboo_4"), TEXT("Bamboo_5"), TEXT("Bamboo_6"),
        TEXT("Bamboo_7"), TEXT("Bamboo_8"), TEXT("Bamboo_9"),
        TEXT("Dots_1"), TEXT("Dots_2"), TEXT("Dots_3"),
        TEXT("Dots_4"), TEXT("Dots_5"), TEXT("Dots_6"),
        TEXT("Dots_7"), TEXT("Dots_8"), TEXT("Dots_9")
    };
    TileMeshes.Reset(UE_ARRAY_COUNT(TileAssetNames));
    TileMeshes.SetNum(UE_ARRAY_COUNT(TileAssetNames));
    for (int32 Index = 0; Index < TileMeshes.Num(); ++Index)
    {
        const FString AssetName = FString::Printf(TEXT("SM_Mahjong50_%s"), TileAssetNames[Index]);
        const FString AssetPath = FString::Printf(
            TEXT("/Game/Art/Mahjong/Mahjong50/Tiles/%s.%s"), *AssetName, *AssetName);
        TileMeshes[Index] = LoadObject<UStaticMesh>(nullptr, *AssetPath);
    }
    DefaultTileMesh = TileMeshes.IsValidIndex(0) ? TileMeshes[0] : nullptr;
    BasicMaterial = LoadObject<UMaterialInterface>(nullptr,
        TEXT("/Engine/BasicShapes/BasicShapeMaterial.BasicShapeMaterial"));
}

void AMahjong3DTableActor::UpdateLayout(const FMahjongPublicTableState& PublicState,
    const FMahjongPrivatePlayerState& PrivateState, const bool bHasPrivateState, const int32 LocalSeat)
{
    const int32 ClampedLocalSeat = FMath::Clamp(LocalSeat, 0, 3);
    const bool bLayoutUnchanged = bLayoutInitialized
        && CachedPublicState.StateSequence == PublicState.StateSequence
        && CachedPrivateState.StateSequence == PrivateState.StateSequence
        && bCachedPrivateState == bHasPrivateState
        && CachedLocalSeat == ClampedLocalSeat;
    CachedPublicState = PublicState;
    CachedPrivateState = PrivateState;
    bCachedPrivateState = bHasPrivateState;
    CachedLocalSeat = ClampedLocalSeat;
    if (bLayoutUnchanged) return;
    bLayoutInitialized = true;
    RebuildLayout();
}

void AMahjong3DTableActor::SetSelectedTile(const int32 UniqueId)
{
    if (SelectedTileId == UniqueId) return;
    SelectedTileId = UniqueId;
    RebuildLayout();
}

void AMahjong3DTableActor::RebuildLayout()
{
    ClearRuntimeComponents();
    AddRemainingWall();
    AddHands();
    AddDiscards();
    AddMelds();
}

void AMahjong3DTableActor::ClearRuntimeComponents()
{
    for (UActorComponent* Component : RuntimeComponents)
    {
        if (IsValid(Component)) Component->DestroyComponent();
    }
    RuntimeComponents.Reset();
}

UStaticMeshComponent* AMahjong3DTableActor::AddBox(const FVector& Location, const FVector& Size,
    const FRotator& Rotation, const FLinearColor& Color)
{
    if (!CubeMesh) return nullptr;
    UStaticMeshComponent* Component = NewObject<UStaticMeshComponent>(this);
    Component->SetStaticMesh(CubeMesh);
    Component->SetCollisionEnabled(ECollisionEnabled::NoCollision);
    Component->SetCastShadow(true);
    Component->SetRelativeLocation(Location);
    Component->SetRelativeRotation(Rotation);
    Component->SetRelativeScale3D(Size / 100.0f);
    Component->SetupAttachment(SceneRoot);
    AddInstanceComponent(Component);
    Component->RegisterComponent();
    if (BasicMaterial)
    {
        UMaterialInstanceDynamic* DynamicMaterial = Component->CreateDynamicMaterialInstance(0, BasicMaterial);
        if (DynamicMaterial)
        {
            DynamicMaterial->SetVectorParameterValue(TEXT("Color"), Color);
            DynamicMaterial->SetVectorParameterValue(TEXT("BaseColor"), Color);
        }
    }
    RuntimeComponents.Add(Component);
    return Component;
}

void AMahjong3DTableActor::AddTile(const FMahjongTile* Tile, const bool bFaceUp, const bool bUpright,
    const FVector& Location, const FRotator& Rotation, const bool bSelected)
{
    FVector TileLocation = Location;
    if (bSelected) TileLocation.Z += 16.0f;
    UStaticMesh* Mesh = ResolveTileMesh(Tile, bFaceUp);
    if (Mesh)
    {
        UStaticMeshComponent* Component = NewObject<UStaticMeshComponent>(this);
        Component->SetStaticMesh(Mesh);
        Component->SetCollisionEnabled(ECollisionEnabled::NoCollision);
        // UViewport 中同时存在上百张动态牌时，逐牌动态投影会产生闪烁和过亮边缘。
        Component->SetCastShadow(false);
        FRotator MeshRotation = Rotation;
        if (!bUpright)
        {
            // Mahjong50's imported face normal points opposite to the legacy mesh: +90 exposes
            // the atlas face, while -90 exposes the green back.
            MeshRotation.Roll += bFaceUp ? 90.0f : -90.0f;
        }
        else
        {
            // Blender 模型枢轴在底部中心；现有布局坐标以牌体中心为准。
            TileLocation.Z -= TileHeight * 0.5f;
        }
        Component->SetRelativeLocation(TileLocation);
        Component->SetRelativeRotation(MeshRotation);
        Component->SetRelativeScale3D(FVector(TileWidth / Mahjong50ModelWidth));
        Component->SetupAttachment(SceneRoot);
        AddInstanceComponent(Component);
        Component->RegisterComponent();
        // Face-up variants already own a validated Mahjong50 material instance. Replacing it with
        // a runtime MID can fall back to atlas cell (0,0), making every tile display North.
        RuntimeComponents.Add(Component);
    }
    else
    {
        const FVector Size = bUpright
            ? FVector(TileWidth, TileDepth, TileHeight)
            : FVector(TileHeight, TileWidth, TileDepth);
        AddBox(TileLocation, Size, Rotation,
            bSelected ? FLinearColor(0.95f, 0.68f, 0.16f) : FLinearColor(0.92f, 0.88f, 0.72f));
    }
}

UStaticMesh* AMahjong3DTableActor::ResolveTileMesh(const FMahjongTile* Tile, const bool bFaceUp) const
{
    if (!bFaceUp || !Tile || !Tile->IsValid()) return DefaultTileMesh;
    const int32 RuleIndex = Tile->GetRuleIndex();
    return RuleIndex >= 0 && RuleIndex < 27 && TileMeshes.IsValidIndex(RuleIndex) && TileMeshes[RuleIndex]
        ? TileMeshes[RuleIndex]
        : DefaultTileMesh;
}

void AMahjong3DTableActor::AddRemainingWall()
{
    const int32 Remaining = FMath::Clamp(CachedPublicState.RemainingTileCount, 0, 108);
    int32 Added = 0;
    for (int32 AbsoluteWallSide = 0; AbsoluteWallSide < 4 && Added < Remaining; ++AbsoluteWallSide)
    {
        // Remaining tiles are distributed in authoritative absolute wall order. Rotate only their
        // presentation so every client sees its own seat at the south edge of the same table.
        const int32 RelativeWallSide = GetRelativeWallSide(AbsoluteWallSide, CachedLocalSeat);
        const int32 SideCount = FMath::Min(28, Remaining - Added);
        const int32 ColumnCount = FMath::DivideAndRoundUp(SideCount, 2);
        const float StartX = -0.5f * (ColumnCount - 1) * TileTightPitch;
        for (int32 Index = 0; Index < SideCount; ++Index)
        {
            const int32 Column = Index / 2;
            const int32 Level = Index % 2;
            // Wall columns touch edge-to-edge and each upper tile sits on the full tile thickness.
            const FVector Base(StartX + Column * TileTightPitch, -WallDistanceFromCenter,
                TileDepth * 0.5f + Level * TileDepth);
            AddTile(nullptr, false, false, RotateAroundTable(Base, RelativeWallSide),
                RotateAroundTable(FRotator::ZeroRotator, RelativeWallSide));
            ++Added;
        }
    }
}

void AMahjong3DTableActor::AddHands()
{
    if (bCachedPrivateState)
    {
        const TArray<FMahjongTile>& Tiles = CachedPrivateState.Hand.Tiles;
        const float StartX = -0.5f * (Tiles.Num() - 1) * TileTightPitch;
        for (int32 Index = 0; Index < Tiles.Num(); ++Index)
        {
            AddTile(&Tiles[Index], true, true,
                FVector(StartX + Index * TileTightPitch, -HandDistanceFromCenter,
                    TileHeight * 0.5f + 9.0f),
                FRotator::ZeroRotator, Tiles[Index].UniqueId == SelectedTileId);
        }
    }

    for (const FMahjongSeatInfo& Seat : CachedPublicState.Seats)
    {
        const int32 RelativeSeat = GetRelativeSeat(Seat.SeatIndex);
        if (RelativeSeat == 0) continue;
        const int32 Count = FMath::Clamp(Seat.HandTileCount, 0, 14);
        const float StartX = -0.5f * (Count - 1) * TileTightPitch;
        for (int32 Index = 0; Index < Count; ++Index)
        {
            const FVector Base(StartX + Index * TileTightPitch, -HandDistanceFromCenter,
                TileHeight * 0.5f + 9.0f);
            AddTile(nullptr, false, true, RotateAroundTable(Base, RelativeSeat),
                RotateAroundTable(FRotator::ZeroRotator, RelativeSeat));
        }
    }
}

void AMahjong3DTableActor::AddDiscards()
{
    for (const FMahjongDiscardRecord& Record : CachedPublicState.Discards)
    {
        if (Record.bClaimed) continue;
        const int32 RelativeSeat = GetRelativeSeat(Record.SeatIndex);
        if (RelativeSeat == INDEX_NONE) continue;
        int32 SeatSequence = 0;
        for (const FMahjongDiscardRecord& Previous : CachedPublicState.Discards)
        {
            if (&Previous == &Record) break;
            if (!Previous.bClaimed && GetRelativeSeat(Previous.SeatIndex) == RelativeSeat) ++SeatSequence;
        }
        const int32 SafeDiscardColumns = FMath::Clamp(DiscardColumns, 5, 9);
        const int32 Column = SeatSequence % SafeDiscardColumns;
        const int32 Row = SeatSequence / SafeDiscardColumns;
        const float DiscardStartX =
            -0.5f * static_cast<float>(SafeDiscardColumns - 1) * TileTightPitch;
        const FVector Base(DiscardStartX + Column * TileTightPitch,
            -DiscardFirstRowDistanceFromCenter - Row * TileTightLongPitch, 14.0f);
        AddTile(&Record.Tile, true, false, RotateAroundTable(Base, RelativeSeat),
            RotateAroundTable(FRotator::ZeroRotator, RelativeSeat),
            Record.Sequence == CachedPublicState.Discards.Last().Sequence);
    }
}

void AMahjong3DTableActor::AddMelds()
{
    int32 MeldTileCountBySeat[4] = {};
    for (const FMahjongMeld& Meld : CachedPublicState.PublicMelds)
    {
        const int32 RelativeSeat = GetRelativeSeat(Meld.OwnerSeat);
        if (RelativeSeat >= 0 && RelativeSeat < 4)
        {
            MeldTileCountBySeat[RelativeSeat] += Meld.Tiles.Num();
        }
    }

    int32 MeldTileIndexBySeat[4] = {};
    for (const FMahjongMeld& Meld : CachedPublicState.PublicMelds)
    {
        const int32 RelativeSeat = GetRelativeSeat(Meld.OwnerSeat);
        if (RelativeSeat == INDEX_NONE) continue;
        for (int32 TileIndex = 0; TileIndex < Meld.Tiles.Num(); ++TileIndex)
        {
            const FMahjongTile& Tile = Meld.Tiles[TileIndex];
            const int32 PackedIndex = MeldTileIndexBySeat[RelativeSeat]++;
            // Exposed melds use the same upright orientation and tight pitch as the player's hand.
            // Keep them on a parallel inner row so they remain readable without overlapping the hand.
            const float StartX = -0.5f * (MeldTileCountBySeat[RelativeSeat] - 1) * TileTightPitch;
            const FVector Base(StartX + PackedIndex * TileTightPitch, -MeldDistanceFromCenter,
                TileHeight * 0.5f + 9.0f);
            AddTile(Tile.IsValid() ? &Tile : nullptr, Tile.IsValid(), true,
                RotateAroundTable(Base, RelativeSeat), RotateAroundTable(FRotator::ZeroRotator, RelativeSeat));
        }
    }
}

int32 AMahjong3DTableActor::GetRelativeSeat(const int32 AbsoluteSeat) const
{
    return GetRelativeWallSide(AbsoluteSeat, CachedLocalSeat);
}

int32 AMahjong3DTableActor::GetRelativeWallSide(const int32 AbsoluteWallSide, const int32 LocalSeat)
{
    if (AbsoluteWallSide < 0 || AbsoluteWallSide >= 4 || LocalSeat < 0 || LocalSeat >= 4)
    {
        return INDEX_NONE;
    }
    return (AbsoluteWallSide - LocalSeat + 4) % 4;
}
