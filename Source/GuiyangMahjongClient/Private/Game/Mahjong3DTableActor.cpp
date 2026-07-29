#include "Game/Mahjong3DTableActor.h"

#include "Components/SceneComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/StaticMesh.h"
#include "GameFramework/PlayerController.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Materials/MaterialInterface.h"
#include "UI/MahjongTileVisualLibrary.h"

namespace
{
    // Unreal uses centimetres. These are real mahjong-tile dimensions for the
    // 300 cm x 300 cm tabletop, rather than the legacy ten-times presentation scale.
    constexpr float TileWidth = 4.4f;
    constexpr float TileHeight = 6.2f;
    constexpr float TileDepth = 3.0f;
    constexpr float SelectedTileLift = 2.5f;
    // Adjacent wall, hand, discard, and meld tiles touch directly.
    constexpr float TileSpacing = 0.0f;
    constexpr float TilePitch = TileWidth + TileSpacing;
    constexpr float TileLongPitch = TileHeight + TileSpacing;
    constexpr float Mahjong50ModelWidth = 3.6f;
    constexpr int32 GuiyangSuitedTileCount = 108;
    constexpr int32 WallSideCount = 4;
    // Complete two-tile stacks: South/North 14 stacks, East/West 13.
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

    int32 GetClockwiseSegmentForPhysicalIndex(const int32 PhysicalIndex)
    {
        int32 Start = 0;
        for (int32 Segment = 0; Segment < WallSideCount; ++Segment)
        {
            const int32 End = Start + WallTilesByClockwiseSegment[Segment];
            if (PhysicalIndex < End) return Segment;
            Start = End;
        }
        return INDEX_NONE;
    }

    FVector RotateAroundTable(const FVector& Position, const int32 RelativeSeat)
    {
        switch (RelativeSeat)
        {
        // The room camera looks north with yaw +90, so screen-right is world -X.
        // Relative seat 1 is the next player on the right; rotate clockwise in
        // world space so East/West walls agree with both the HUD and center dial.
        case 1: return FVector(Position.Y, -Position.X, Position.Z);
        case 2: return FVector(-Position.X, -Position.Y, Position.Z);
        case 3: return FVector(-Position.Y, Position.X, Position.Z);
        default: return Position;
        }
    }

    FRotator RotateAroundTable(const FRotator& Rotation, const int32 RelativeSeat)
    {
        return FRotator(Rotation.Pitch, Rotation.Yaw - 90.0f * RelativeSeat, Rotation.Roll);
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
    BackTileMesh = LoadObject<UStaticMesh>(nullptr,
        TEXT("/Game/Art/Mahjong/Mahjong50/Meshes/SM_Mahjong50.SM_Mahjong50"));
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
    const int32 PreviousSelectedTileId = SelectedTileId;
    SelectedTileId = UniqueId;
    ApplyLocalHandTileVisualState(PreviousSelectedTileId);
    ApplyLocalHandTileVisualState(SelectedTileId);
}

void AMahjong3DTableActor::SetHoveredTile(const int32 UniqueId)
{
    if (HoveredTileId == UniqueId) return;
    const int32 PreviousHoveredTileId = HoveredTileId;
    HoveredTileId = UniqueId;
    ApplyLocalHandTileVisualState(PreviousHoveredTileId);
    ApplyLocalHandTileVisualState(HoveredTileId);
}

int32 AMahjong3DTableActor::GetLocalHandTileUnderCursor(
    APlayerController* PlayerController) const
{
    if (!PlayerController)
    {
        return INDEX_NONE;
    }
    float MouseX = 0.0f;
    float MouseY = 0.0f;
    if (!PlayerController->GetMousePosition(MouseX, MouseY))
    {
        return INDEX_NONE;
    }

    const FVector2D Cursor(MouseX, MouseY);
    struct FProjectedHandTile
    {
        int32 UniqueId = INDEX_NONE;
        FVector2D Center = FVector2D::ZeroVector;
        double MinY = TNumericLimits<double>::Max();
        double MaxY = TNumericLimits<double>::Lowest();
    };
    TArray<FProjectedHandTile> ProjectedTiles;
    ProjectedTiles.Reserve(LocalHandTileComponents.Num());
    double HandMinY = TNumericLimits<double>::Max();
    double HandMaxY = TNumericLimits<double>::Lowest();

    for (const TPair<int32, UStaticMeshComponent*>& Pair :
         LocalHandTileComponents)
    {
        const UStaticMeshComponent* Component = Pair.Value;
        if (!IsValid(Component) || !Component->IsVisible())
        {
            continue;
        }

        FVector2D ProjectedCenter;
        if (!PlayerController->ProjectWorldLocationToScreen(
            Component->Bounds.Origin, ProjectedCenter, true))
        {
            continue;
        }

        FProjectedHandTile ProjectedTile;
        ProjectedTile.UniqueId = Pair.Key;
        ProjectedTile.Center = ProjectedCenter;
        const FVector Origin = Component->Bounds.Origin;
        const FVector Extent = Component->Bounds.BoxExtent;
        for (int32 CornerIndex = 0; CornerIndex < 8; ++CornerIndex)
        {
            const FVector Corner = Origin + FVector(
                (CornerIndex & 1) ? Extent.X : -Extent.X,
                (CornerIndex & 2) ? Extent.Y : -Extent.Y,
                (CornerIndex & 4) ? Extent.Z : -Extent.Z);
            FVector2D ProjectedCorner;
            if (PlayerController->ProjectWorldLocationToScreen(
                Corner, ProjectedCorner, true))
            {
                ProjectedTile.MinY =
                    FMath::Min(ProjectedTile.MinY, ProjectedCorner.Y);
                ProjectedTile.MaxY =
                    FMath::Max(ProjectedTile.MaxY, ProjectedCorner.Y);
            }
        }
        if (ProjectedTile.MinY <= ProjectedTile.MaxY)
        {
            HandMinY = FMath::Min(HandMinY, ProjectedTile.MinY);
            HandMaxY = FMath::Max(HandMaxY, ProjectedTile.MaxY);
            ProjectedTiles.Add(ProjectedTile);
        }
    }

    if (ProjectedTiles.IsEmpty())
    {
        return INDEX_NONE;
    }

    // The local hand is one horizontal row. Partition it at the exact
    // midpoints between projected mesh centres instead of testing overlapping
    // physics/AABB bounds. Therefore one cursor X can resolve to only one
    // physical UniqueId, including when the previously selected tile is raised.
    ProjectedTiles.Sort([](
        const FProjectedHandTile& Left,
        const FProjectedHandTile& Right)
    {
        return Left.Center.X < Right.Center.X;
    });

    constexpr double ScreenHitMargin = 6.0;
    if (Cursor.Y < HandMinY - ScreenHitMargin
        || Cursor.Y > HandMaxY + ScreenHitMargin)
    {
        return INDEX_NONE;
    }

    for (int32 Index = 0; Index < ProjectedTiles.Num(); ++Index)
    {
        const double LeftBoundary = Index == 0
            ? ProjectedTiles[Index].Center.X
                - 0.5 * FMath::Abs(
                    ProjectedTiles[FMath::Min(1, ProjectedTiles.Num() - 1)]
                        .Center.X
                    - ProjectedTiles[Index].Center.X)
            : 0.5 * (ProjectedTiles[Index - 1].Center.X
                + ProjectedTiles[Index].Center.X);
        const double RightBoundary = Index == ProjectedTiles.Num() - 1
            ? ProjectedTiles[Index].Center.X
                + 0.5 * FMath::Abs(
                    ProjectedTiles[Index].Center.X
                    - ProjectedTiles[FMath::Max(0, Index - 1)].Center.X)
            : 0.5 * (ProjectedTiles[Index].Center.X
                + ProjectedTiles[Index + 1].Center.X);
        if (Cursor.X >= LeftBoundary - ScreenHitMargin
            && Cursor.X < RightBoundary + ScreenHitMargin)
        {
            return ProjectedTiles[Index].UniqueId;
        }
    }
    return INDEX_NONE;
}

FRotator AMahjong3DTableActor::ResolveTileMeshRotation(
    const FRotator& Rotation, const bool bFaceUp, const bool bUpright)
{
    FRotator MeshRotation = Rotation;
    if (bUpright)
    {
        if (bFaceUp)
        {
            // Mahjong50's authored face points toward local +Y. Turning it
            // outward is sufficient: rotating another 180 degrees around the
            // face axis also inverted local Z and made the south glyph upside down.
            MeshRotation.Yaw += 180.0f;
        }
    }
    else
    {
        // The imported face is local +Y. Unreal's negative roll points it
        // upward for discards/melds; positive roll hides it below the back.
        MeshRotation.Roll += bFaceUp ? -90.0f : 90.0f;
    }
    return MeshRotation;
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
    LocalHandTileIds.Reset();
    LocalHandTileComponents.Reset();
    LocalHandTileBaseLocations.Reset();
    for (UActorComponent* Component : RuntimeComponents)
    {
        if (IsValid(Component)) Component->DestroyComponent();
    }
    RuntimeComponents.Reset();
}

void AMahjong3DTableActor::ApplyLocalHandTileVisualState(const int32 UniqueId)
{
    UStaticMeshComponent** ComponentPtr = LocalHandTileComponents.Find(UniqueId);
    const FVector* BaseLocation = LocalHandTileBaseLocations.Find(UniqueId);
    if (!ComponentPtr || !IsValid(*ComponentPtr) || !BaseLocation)
    {
        return;
    }

    UStaticMeshComponent* Component = *ComponentPtr;
    const bool bSelected = UniqueId == SelectedTileId;
    const bool bHovered = UniqueId == HoveredTileId && !bSelected;

    // Move the exact hit-tested component rather than rebuilding the whole
    // hand. This keeps the cursor target, selected UniqueId and visible tile
    // permanently in sync.
    Component->SetRelativeLocation(
        *BaseLocation + (bSelected
            ? FVector(0.0f, 0.0f, SelectedTileLift)
            : FVector::ZeroVector));
    Component->SetRenderCustomDepth(bSelected || bHovered);
    Component->SetCustomDepthStencilValue(bSelected ? 252 : 251);

    // The Mahjong50 material exposes a Fresnel rim-emissive parameter. It
    // produces a real mesh-aligned glow without spawning helper geometry.
    Component->SetScalarParameterValueOnMaterials(
        TEXT("SelectionGlow"), bSelected ? 20.0f : (bHovered ? 1.6f : 0.0f));
    Component->SetVectorParameterValueOnMaterials(
        TEXT("SelectionGlowColor"),
        bSelected
            ? FVector(0.04f, 1.0f, 0.18f)
            : FVector(0.04f, 0.35f, 1.0f));
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
            DynamicMaterial->SetVectorParameterValue(TEXT("EmissiveColor"), Color * 2.0f);
            DynamicMaterial->SetVectorParameterValue(TEXT("GlowColor"), Color * 2.0f);
        }
    }
    RuntimeComponents.Add(Component);
    return Component;
}

UStaticMeshComponent* AMahjong3DTableActor::AddTile(
    const FMahjongTile* Tile, const bool bFaceUp, const bool bUpright,
    const FVector& Location, const FRotator& Rotation, const bool bSelected,
    const bool bHovered, const float ScaleMultiplier)
{
    FVector TileLocation = Location;
    // Unreal units are centimetres: raise only the matching selected tile by
    // the requested 2.5 cm. Hover never changes the tile position.
    if (bSelected) TileLocation.Z += SelectedTileLift;
    const float SafeScale = FMath::Max(ScaleMultiplier, 0.1f);
    UStaticMesh* Mesh = ResolveTileMesh(Tile, bFaceUp);
    if (Mesh)
    {
        UStaticMeshComponent* Component = NewObject<UStaticMeshComponent>(this);
        Component->SetStaticMesh(Mesh);
        Component->SetCollisionEnabled(ECollisionEnabled::NoCollision);
        // Avoid shimmer from hundreds of overlapping dynamic tile shadows.
        // UViewport 中同时存在上百张动态牌时，逐牌动态投影会产生闪烁和过亮边缘。
        Component->SetCastShadow(false);
        const FRotator MeshRotation = ResolveTileMeshRotation(Rotation, bFaceUp, bUpright);
        if (bUpright)
        {
            // FBX coordinate conversion maps the authored +Y face to Unreal local -Y.
            // Zero yaw therefore faces the south player; seat rotation keeps every concealed
            // opponent hand pointing outward while the local face remains readable.
            // The imported Blender mesh origin is at the bottom centre, whereas these
            // layout coordinates were authored around the legacy mesh centre.
            // Blender 模型枢轴在底部中心；现有布局坐标以牌体中心为准。
            TileLocation.Z -= TileHeight * 0.5f;
        }
        Component->SetRelativeLocation(TileLocation);
        Component->SetRelativeRotation(MeshRotation);
        Component->SetRelativeScale3D(FVector(
            TileWidth / Mahjong50ModelWidth * SafeScale));
        const bool bSelectionOutline = bSelected;
        const bool bHoverOutline = bHovered && !bSelected;
        Component->SetRenderCustomDepth(bSelectionOutline || bHoverOutline);
        Component->SetCustomDepthStencilValue(bSelectionOutline ? 252 : 251);
        if (bFaceUp)
        {
            // The face is an 8K atlas; request its resident mip while the room is visible so
            // mobile texture streaming does not leave the local hand on a blurred fallback mip.
            Component->SetTextureForceResidentFlag(true);
        }
        Component->SetupAttachment(SceneRoot);
        AddInstanceComponent(Component);
        Component->RegisterComponent();
        if (bFaceUp && Tile)
        {
            int32 AtlasColumn = 0;
            int32 AtlasRowFromBottom = 0;
            if (UMahjongTileVisualLibrary::GetFaceAtlasCell(
                *Tile, AtlasColumn, AtlasRowFromBottom))
            {
                // Explicit per-component atlas coordinates prevent the unified
                // material's default cell (Characters_4) from being reused by
                // every runtime tile after cooking or render-state recreation.
                Component->SetScalarParameterValueOnMaterials(
                    TEXT("Column"), static_cast<float>(AtlasColumn));
                Component->SetScalarParameterValueOnMaterials(
                    TEXT("RowFromBottom"), static_cast<float>(AtlasRowFromBottom));
            }
        }
        RuntimeComponents.Add(Component);

        Component->SetScalarParameterValueOnMaterials(
            TEXT("SelectionGlow"),
            bSelectionOutline ? 20.0f : (bHoverOutline ? 1.6f : 0.0f));
        Component->SetVectorParameterValueOnMaterials(
            TEXT("SelectionGlowColor"),
            bSelectionOutline
                ? FVector(0.04f, 1.0f, 0.18f)
                : FVector(0.04f, 0.35f, 1.0f));
        return Component;
    }
    const FVector Size = (bUpright
        ? FVector(TileWidth, TileDepth, TileHeight)
        : FVector(TileHeight, TileWidth, TileDepth)) * FMath::Max(ScaleMultiplier, 0.1f);
    return AddBox(TileLocation, Size, Rotation,
        bSelected ? FLinearColor(0.95f, 0.68f, 0.16f) : FLinearColor(0.92f, 0.88f, 0.72f));
}

UStaticMesh* AMahjong3DTableActor::ResolveTileMesh(const FMahjongTile* Tile, const bool bFaceUp) const
{
    if (!bFaceUp) return BackTileMesh ? BackTileMesh : DefaultTileMesh;
    if (!Tile || !Tile->IsValid()) return DefaultTileMesh;
    const int32 RuleIndex = Tile->GetRuleIndex();
    return RuleIndex >= 0 && RuleIndex < 27 && TileMeshes.IsValidIndex(RuleIndex) && TileMeshes[RuleIndex]
        ? TileMeshes[RuleIndex]
        : DefaultTileMesh;
}

void AMahjong3DTableActor::AddRemainingWall()
{
    const int32 Remaining = FMath::Clamp(
        CachedPublicState.RemainingTileCount, 0, GuiyangSuitedTileCount);
    // Mirror MahjongDeckManager's physical clockwise ring. Consumed tiles are
    // removed consecutively from the server-provided break position; they must
    // never be averaged back across all four sides after every draw.
    for (int32 PhysicalIndex = 0;
         PhysicalIndex < GuiyangSuitedTileCount;
         ++PhysicalIndex)
    {
        if (!IsWallPhysicalSlotRemaining(
            PhysicalIndex,
            Remaining,
            CachedPublicState.WallBreakSide,
            CachedPublicState.WallBreakStackFromRight))
        {
            continue;
        }

        const int32 ClockwiseSegment =
            GetClockwiseSegmentForPhysicalIndex(PhysicalIndex);
        if (ClockwiseSegment == INDEX_NONE) continue;
        const int32 IndexWithinSide =
            PhysicalIndex - GetClockwiseSegmentStart(ClockwiseSegment);
        // Physical clockwise segment order is South -> West -> North -> East,
        // while absolute seat indices are South -> East -> North -> West.
        const int32 AbsoluteWallSide =
            (WallSideCount - ClockwiseSegment) % WallSideCount;
        const int32 RelativeWallSide =
            GetRelativeWallSide(AbsoluteWallSide, CachedLocalSeat);
        if (RelativeWallSide == INDEX_NONE) continue;

        // Physical indices run from the selected wall's right end toward its
        // left end. Preserve the empty slots so the break and draw progression
        // remain visible instead of compressing the surviving tiles together.
        const int32 StacksOnSide =
            WallTilesByAbsoluteSide[AbsoluteWallSide] / 2;
        const float HalfWallSpan =
            0.5f * (StacksOnSide - 1) * TilePitch;
        const int32 StackFromRight = IndexWithinSide / 2;
        // Draw the upper tile first, then the lower tile. A partially consumed
        // stack may leave its lower tile, but can never leave an upper tile floating.
        const int32 Level = 1 - IndexWithinSide % 2;
        // The camera's screen-right axis is world -X. Start at the owning
        // player's physical right end and advance leftward, matching the
        // server's "从右往左数墩、顺时针连续抓牌" cursor.
        const FVector Base(
            -HalfWallSpan + StackFromRight * TilePitch,
            -WallDistanceFromCenter,
            TileDepth * 0.5f + Level * TileDepth);
        AddTile(nullptr, false, false, RotateAroundTable(Base, RelativeWallSide),
            RotateAroundTable(FRotator::ZeroRotator, RelativeWallSide));
    }
}

bool AMahjong3DTableActor::IsWallPhysicalSlotRemaining(
    const int32 PhysicalIndex,
    const int32 RemainingTileCount,
    const int32 WallBreakSide,
    const int32 WallBreakStackFromRight)
{
    if (PhysicalIndex < 0 || PhysicalIndex >= GuiyangSuitedTileCount) return false;

    const int32 SafeRemaining = FMath::Clamp(
        RemainingTileCount, 0, GuiyangSuitedTileCount);
    const int32 SafeBreakSide =
        WallBreakSide >= 0 && WallBreakSide < WallSideCount ? WallBreakSide : 0;
    const int32 StacksOnBreakSide =
        WallTilesByAbsoluteSide[SafeBreakSide] / 2;
    const int32 SafeBreakStack = FMath::Clamp(
        WallBreakStackFromRight, 0, StacksOnBreakSide);
    const int32 ClockwiseBreakSegment =
        (WallSideCount - SafeBreakSide) % WallSideCount;
    const int32 DrawStartIndex =
        (GetClockwiseSegmentStart(ClockwiseBreakSegment)
            + SafeBreakStack * 2)
        % GuiyangSuitedTileCount;
    const int32 Consumed = GuiyangSuitedTileCount - SafeRemaining;
    const int32 ClockwiseDistanceFromBreak =
        (PhysicalIndex - DrawStartIndex + GuiyangSuitedTileCount)
        % GuiyangSuitedTileCount;
    return ClockwiseDistanceFromBreak >= Consumed;
}

void AMahjong3DTableActor::AddHands()
{
    if (bCachedPrivateState)
    {
        const TArray<FMahjongTile>& Tiles = CachedPrivateState.Hand.Tiles;
        const float LocalHandPitch = TileWidth * LocalHandScale + TileSpacing;
        // The camera's screen-right axis is world -X. Mirror the world-space
        // sequence so the visible 3D hand has the same left-to-right UniqueId
        // order as the transparent UMG click targets.
        const float StartX = 0.5f * (Tiles.Num() - 1) * LocalHandPitch;
        for (int32 Index = 0; Index < Tiles.Num(); ++Index)
        {
            UStaticMeshComponent* HandComponent = AddTile(&Tiles[Index], true, true,
                FVector(StartX - Index * LocalHandPitch, -LocalHandDistanceFromCenter,
                    TileHeight * 0.5f + LocalHandElevation),
                FRotator(0.0f, 0.0f, LocalHandCameraTiltDegrees),
                Tiles[Index].UniqueId == SelectedTileId,
                Tiles[Index].UniqueId == HoveredTileId,
                LocalHandScale);
            if (HandComponent)
            {
                HandComponent->SetCollisionEnabled(ECollisionEnabled::QueryOnly);
                HandComponent->SetCollisionResponseToAllChannels(ECR_Ignore);
                HandComponent->SetCollisionResponseToChannel(ECC_Visibility, ECR_Block);
                LocalHandTileIds.Add(HandComponent, Tiles[Index].UniqueId);
                LocalHandTileComponents.Add(Tiles[Index].UniqueId, HandComponent);
                LocalHandTileBaseLocations.Add(
                    Tiles[Index].UniqueId,
                        HandComponent->GetRelativeLocation()
                            - (Tiles[Index].UniqueId == SelectedTileId
                            ? FVector(0.0f, 0.0f, SelectedTileLift)
                            : FVector::ZeroVector));
            }
        }
    }

    for (const FMahjongSeatInfo& Seat : CachedPublicState.Seats)
    {
        const int32 RelativeSeat = GetRelativeSeat(Seat.SeatIndex);
        if (RelativeSeat == 0) continue;
        const int32 Count = FMath::Clamp(Seat.HandTileCount, 0, 14);
        const float StartX = -0.5f * (Count - 1) * TilePitch;
        for (int32 Index = 0; Index < Count; ++Index)
        {
            const FVector Base(StartX + Index * TilePitch, -HandDistanceFromCenter,
                TileHeight * 0.5f + 0.9f);
            // Use the authored white-body/green-back PBR tile and rotate its
            // face outward toward the owning player. The local viewer therefore
            // sees only the concealed green back, without leaking the mesh's
            // default Characters_4 face or wrapping a white cap in a green cube.
            FRotator ConcealedRotation =
                RotateAroundTable(FRotator::ZeroRotator, RelativeSeat);
            ConcealedRotation.Yaw += 180.0f;
            AddTile(nullptr, false, true,
                RotateAroundTable(Base, RelativeSeat),
                ConcealedRotation);
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
        const int32 SafeDiscardColumns = FMath::Clamp(DiscardColumns, 8, 12);
        const int32 Column = SeatSequence % SafeDiscardColumns;
        const int32 Row = SeatSequence / SafeDiscardColumns;
        // The room camera's screen-right axis is world -X. Start at world +X
        // and advance toward -X so every seat's local discard row is laid out
        // visually from left to right instead of appearing reversed.
        const float DiscardStartX =
            0.5f * static_cast<float>(SafeDiscardColumns - 1) * TilePitch;
        const FVector Base(DiscardStartX - Column * TilePitch,
            -(DiscardFirstRowDistanceFromCenter + 2.5f) - Row * TileLongPitch, 1.4f);
        FRotator DiscardRotation =
            RotateAroundTable(FRotator::ZeroRotator, RelativeSeat);
        // A discard belongs visually to the player who placed it. Turn its
        // face 180 degrees so the glyph is upright from that player's seat.
        DiscardRotation.Yaw += 180.0f;
        // Discards never inherit the local-hand selection state. Highlighting
        // the latest record with bSelected also applies SelectedTileLift,
        // which made another player's discard appear to rise after clicking
        // a local hand tile.
        AddTile(&Record.Tile, true, false,
            RotateAroundTable(Base, RelativeSeat), DiscardRotation,
            false, false);
    }
}

void AMahjong3DTableActor::AddMelds()
{
    int32 MeldTileCountBySeat[4] = {};
    int32 ConcealedHandCountBySeat[4] = {};
    for (const FMahjongSeatInfo& Seat : CachedPublicState.Seats)
    {
        const int32 RelativeSeat = GetRelativeSeat(Seat.SeatIndex);
        if (RelativeSeat >= 0 && RelativeSeat < 4)
        {
            ConcealedHandCountBySeat[RelativeSeat] =
                FMath::Clamp(Seat.HandTileCount, 0, 14);
        }
    }
    if (bCachedPrivateState)
    {
        ConcealedHandCountBySeat[0] = CachedPrivateState.Hand.Tiles.Num();
    }
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
            // Put exposed melds flat on the tabletop, immediately to the
            // screen-right side of the owner's concealed hand. The old
            // centred upright row was hidden behind/offscreen from most seats.
            const float HandHalfSpan = 0.5f
                * FMath::Max(0, ConcealedHandCountBySeat[RelativeSeat] - 1)
                * TilePitch;
            const float VisibleMeldDistance = FMath::Min(
                MeldDistanceFromCenter, WallDistanceFromCenter + 10.0f);
            const FVector Base(
                -HandHalfSpan - TilePitch - PackedIndex * TilePitch,
                -VisibleMeldDistance,
                TileDepth * 0.5f + 0.35f);
            FRotator MeldRotation =
                RotateAroundTable(FRotator::ZeroRotator, RelativeSeat);
            MeldRotation.Yaw += 180.0f;
            // Peng/MingGang/BuGang contain valid public tiles and render face
            // up. AnGang uses invalid public placeholders and remains concealed.
            AddTile(Tile.IsValid() ? &Tile : nullptr, Tile.IsValid(), false,
                RotateAroundTable(Base, RelativeSeat), MeldRotation);
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
