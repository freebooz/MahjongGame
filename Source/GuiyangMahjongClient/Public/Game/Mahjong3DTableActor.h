#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "Network/MahjongNetworkTypes.h"
#include "Mahjong3DTableActor.generated.h"

class UActorComponent;
class USceneComponent;
class UStaticMesh;
class UStaticMeshComponent;
class UMaterialInterface;
class UPrimitiveComponent;
class APlayerController;

/**
 * UMG Viewport 中的三维牌桌表现层。
 * 只消费客户端已有的公开/私有快照，不拥有规则状态，也不发送网络请求。
 */
UCLASS()
class GUIYANGMAHJONGCLIENT_API AMahjong3DTableActor final : public AActor
{
    GENERATED_BODY()

public:
    /** 创建仅客户端存在的场景根节点。 */
    AMahjong3DTableActor();

    /** 缓存最新公共/私有快照并重建本地相对视角布局。 */
    void UpdateLayout(const FMahjongPublicTableState& PublicState,
        const FMahjongPrivatePlayerState& PrivateState, bool bHasPrivateState, int32 LocalSeat);
    /** 高亮指定唯一牌 ID，不改变权威手牌。 */
    void SetSelectedTile(int32 UniqueId);
    void SetHoveredTile(int32 UniqueId);
    /** Only true when the selected local tile component has completed its 2.5 cm lift. */
    bool IsLocalHandTileRaised(int32 UniqueId) const;
    /** Resolve the exact visible local-hand mesh below the mouse cursor. */
    int32 GetLocalHandTileUnderCursor(APlayerController* PlayerController) const;
    /** 将服务端绝对牌墙方位转换为当前客户端以自己为南方的相对方位。 */
    static int32 GetRelativeWallSide(int32 AbsoluteWallSide, int32 LocalSeat);
    /** 判断物理牌墙槽位是否仍存在；抓牌从开门处沿顺时针连续推进。 */
    static bool IsWallPhysicalSlotRemaining(int32 PhysicalIndex, int32 RemainingTileCount,
        int32 WallBreakSide, int32 WallBreakStackFromRight);
    /** Applies the Mahjong50 face-axis correction for upright and flat presentation. */
    static FRotator ResolveTileMeshRotation(const FRotator& Rotation, bool bFaceUp, bool bUpright);

private:
    /** 首次进入世界时加载桌面、牌体和牌面表现资源。 */
    virtual void BeginPlay() override;

    /** 场景根与每次重建动态创建的组件集合。 */
    UPROPERTY(VisibleAnywhere, Category="Mahjong|Presentation") TObjectPtr<USceneComponent> SceneRoot;
    UPROPERTY(Transient) TArray<TObjectPtr<UActorComponent>> RuntimeComponents;
    /** 桌面辅助网格、默认牌体、34 种牌面网格及基础材质。 */
    UPROPERTY(Transient) TObjectPtr<UStaticMesh> CubeMesh;
    UPROPERTY(Transient) TObjectPtr<UStaticMesh> DefaultTileMesh;
    UPROPERTY(Transient) TObjectPtr<UStaticMesh> BackTileMesh;
    UPROPERTY(Transient) TArray<TObjectPtr<UStaticMesh>> TileMeshes;
    UPROPERTY(Transient) TObjectPtr<UMaterialInterface> BasicMaterial;
    /** 可在房间展示蓝图的 Child Actor 模板中人工调整的桌面布局距离。 */
    UPROPERTY(EditAnywhere, Category="Mahjong|Layout", meta=(ClampMin="20.0", ClampMax="100.0"))
    // Radial distance from the controller-disc centre to each wall row.
    float WallDistanceFromCenter = 45.0f;
    UPROPERTY(EditAnywhere, Category="Mahjong|Layout", meta=(ClampMin="20.0", ClampMax="140.0"))
    // Radial distance from the controller-disc centre to each concealed hand.
    float HandDistanceFromCenter = 50.0f;
    /** Local hand is raised above the near rail and kept parallel to the bottom of the screen. */
    UPROPERTY(EditAnywhere, Category="Mahjong|Layout", meta=(ClampMin="20.0", ClampMax="160.0"))
    float LocalHandDistanceFromCenter = 60.0f;
    UPROPERTY(EditAnywhere, Category="Mahjong|Layout", meta=(ClampMin="0.0", ClampMax="20.0"))
    float LocalHandElevation = 7.5f;
    UPROPERTY(EditAnywhere, Category="Mahjong|Layout", meta=(ClampMin="1.0", ClampMax="1.6"))
    float LocalHandScale = 1.35f;
    /** Tilt the south face upward toward the elevated 30-degree room camera. */
    UPROPERTY(EditAnywhere, Category="Mahjong|Layout", meta=(ClampMin="-60.0", ClampMax="0.0"))
    float LocalHandCameraTiltDegrees = -30.0f;
    UPROPERTY(EditAnywhere, Category="Mahjong|Layout", meta=(ClampMin="65.0", ClampMax="105.0"))
    float MeldDistanceFromCenter = 82.0f;
    UPROPERTY(EditAnywhere, Category="Mahjong|Layout", meta=(ClampMin="18.0", ClampMax="45.0"))
    float DiscardFirstRowDistanceFromCenter = 25.0f;
    UPROPERTY(EditAnywhere, Category="Mahjong|Layout", meta=(ClampMin="8", ClampMax="12"))
    int32 DiscardColumns = 8;
    /** 只读缓存快照及布局版本状态。 */
    UPROPERTY() FMahjongPublicTableState CachedPublicState;
    UPROPERTY() FMahjongPrivatePlayerState CachedPrivateState;
    bool bCachedPrivateState = false;
    bool bLayoutInitialized = false;
    int32 CachedLocalSeat = 0;
    int32 SelectedTileId = INDEX_NONE;
    int32 HoveredTileId = INDEX_NONE;
    TMap<UPrimitiveComponent*, int32> LocalHandTileIds;
    TMap<int32, UStaticMeshComponent*> LocalHandTileComponents;
    TMap<int32, FVector> LocalHandTileBaseLocations;

    /** 加载客户端美术资源并按快照重建所有运行时组件。 */
    void InitializePresentationAssets();
    void RebuildLayout();
    void ClearRuntimeComponents();
    void ApplyLocalHandTileVisualState(int32 UniqueId);
    /** 创建桌体辅助方盒或一张有正反面的麻将牌。 */
    class UStaticMeshComponent* AddBox(const FVector& Location, const FVector& Size,
        const FRotator& Rotation, const FLinearColor& Color);
    UStaticMesh* ResolveTileMesh(const FMahjongTile* Tile, bool bFaceUp) const;
    class UStaticMeshComponent* AddTile(const FMahjongTile* Tile, bool bFaceUp, bool bUpright,
        const FVector& Location, const FRotator& Rotation, bool bSelected = false,
        bool bHovered = false, float ScaleMultiplier = 1.0f);
    /** 分别生成剩余牌墙、四家手牌、弃牌与副露。 */
    void AddRemainingWall();
    void AddHands();
    void AddDiscards();
    void AddMelds();
    int32 GetRelativeSeat(int32 AbsoluteSeat) const;
};
