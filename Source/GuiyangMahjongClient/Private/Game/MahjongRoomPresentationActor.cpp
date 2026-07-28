#include "Game/MahjongRoomPresentationActor.h"

#include "Camera/CameraComponent.h"
#include "Components/ChildActorComponent.h"
#include "Components/ModelComponent.h"
#include "Components/SkyLightComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Game/Mahjong3DTableActor.h"
#include "Engine/Level.h"
#include "Engine/StaticMesh.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/Brush.h"
#include "GameFramework/Volume.h"
#include "EngineUtils.h"
#include "GuiyangMahjong.h"

const FName AMahjongRoomPresentationActor::PresentationTag(TEXT("MahjongRoomPresentation"));

AMahjongRoomPresentationActor::AMahjongRoomPresentationActor()
{
    PrimaryActorTick.bCanEverTick = false;
    SetReplicates(false);
    SetCanBeDamaged(false);
    Tags.AddUnique(PresentationTag);
}

void AMahjongRoomPresentationActor::BeginPlay()
{
    Super::BeginPlay();

    // The room is an enclosed tabletop scene and deliberately owns no sky dome.
    // Disable real-time sky capture even for an older cooked Blueprint, which
    // removes the missing-atmosphere warning without creating replacement geometry.
    TInlineComponentArray<USkyLightComponent*> SkyLightComponents(this);
    for (USkyLightComponent* SkyLight : SkyLightComponents)
    {
        if (SkyLight)
        {
            SkyLight->SetRealTimeCaptureEnabled(false);
        }
    }

    // Older presentation assets were authored for a 115 cm prototype and some saved component
    // hierarchies still carry a 10x parent scale. Normalize the two runtime visual branches from
    // their actual mesh bounds so stale Blueprint transforms cannot hide the 300 cm table.
    UStaticMeshComponent* TableMeshComponent = nullptr;
    TInlineComponentArray<UStaticMeshComponent*> StaticMeshComponents(this);
    for (UStaticMeshComponent* Component : StaticMeshComponents)
    {
        if (!Component)
        {
            continue;
        }

        if (Component->GetName() == TEXT("MahjongTableMesh"))
        {
            TableMeshComponent = Component;
            continue;
        }

        const bool bAllowedPresentationGeometry =
            Component->GetName() == TEXT("RoomBackdropPlane");
        if (Component->GetStaticMesh() && !bAllowedPresentationGeometry)
        {
            const FString GeometryIdentity =
                Component->GetName() + TEXT(" ") + Component->GetStaticMesh()->GetPathName();
            UE_LOG(LogMahjongUI, Warning,
                TEXT("Removed non-whitelisted room presentation geometry: %s"),
                *GeometryIdentity);
            Component->DestroyComponent();
        }
    }

    // Some old room-map revisions placed the obsolete sky dome as an
    // independent StaticMeshActor rather than a presentation component. Remove
    // only explicit sphere/dome assets; the Mahjong table and tile child actor
    // are never selected by this guard.
    if (UWorld* World = GetWorld())
    {
        // BSP rendering is owned by ULevel::ModelComponents after geometry is
        // built. Cooked maps no longer contain the source ABrush actors, so
        // destroying Brush_0 alone cannot remove a stale compiled dome. This
        // room intentionally uses no BSP; suppress every compiled model
        // component before the first room frame as a defense against old maps.
        if (ULevel* Level = World->PersistentLevel)
        {
            for (UModelComponent* ModelComponent : Level->ModelComponents)
            {
                if (!ModelComponent)
                {
                    continue;
                }
                UE_LOG(LogMahjongUI, Warning,
                    TEXT("Disabled obsolete compiled room BSP component: %s bounds=%s"),
                    *ModelComponent->GetPathName(),
                    *ModelComponent->Bounds.BoxExtent.ToCompactString());
                ModelComponent->SetCollisionEnabled(ECollisionEnabled::NoCollision);
                ModelComponent->SetVisibility(false, true);
                ModelComponent->SetHiddenInGame(true, true);
            }
        }

        for (TActorIterator<ABrush> It(World); It; ++It)
        {
            ABrush* BrushActor = *It;
            if (!BrushActor || Cast<AVolume>(BrushActor))
            {
                continue;
            }
            UE_LOG(LogMahjongUI, Warning,
                TEXT("Destroyed obsolete room BSP geometry: %s"),
                *BrushActor->GetPathName());
            BrushActor->Destroy();
        }

        for (TActorIterator<AStaticMeshActor> It(World); It; ++It)
        {
            AStaticMeshActor* MeshActor = *It;
            UStaticMeshComponent* MeshComponent =
                MeshActor ? MeshActor->GetStaticMeshComponent() : nullptr;
            UStaticMesh* Mesh = MeshComponent ? MeshComponent->GetStaticMesh() : nullptr;
            if (!Mesh)
            {
                continue;
            }
            const FString GeometryIdentity =
                MeshActor->GetName() + TEXT(" ") + Mesh->GetPathName();
            if (GeometryIdentity.Contains(TEXT("Sphere"), ESearchCase::IgnoreCase)
                || GeometryIdentity.Contains(TEXT("Dome"), ESearchCase::IgnoreCase)
                || GeometryIdentity.Contains(TEXT("Hemisphere"), ESearchCase::IgnoreCase))
            {
                UE_LOG(LogMahjongUI, Warning,
                    TEXT("Destroyed obsolete room-map sky geometry: %s"),
                    *GeometryIdentity);
                MeshActor->Destroy();
            }
        }
    }

    if (TableMeshComponent && TableMeshComponent->GetStaticMesh())
    {
        const FVector MeshSize =
            TableMeshComponent->GetStaticMesh()->GetBounds().BoxExtent * 2.0f;
        const float LargestHorizontalSize = FMath::Max(MeshSize.X, MeshSize.Y);
        if (LargestHorizontalSize > KINDA_SMALL_NUMBER)
        {
            const float UniformScale = 300.0f / LargestHorizontalSize;
            TableMeshComponent->SetWorldScale3D(FVector(UniformScale));
        }
        // The imported controller-disc UV is reversed relative to the mesh's world axes. Rotate
        // only the physical table by 180 degrees so its 南 label faces the -Y camera at the exact
        // bottom of the screen; the independent tile-layout actor remains unrotated.
        TableMeshComponent->SetWorldRotation(FRotator(0.0f, 180.0f, 0.0f));
        TableMeshComponent->SetVisibility(true, true);
        TableMeshComponent->SetHiddenInGame(false, true);
        UE_LOG(LogMahjongUI, Display,
            TEXT("Normalized Mahjong table: mesh=%s size=%s world-scale=%s"),
            *TableMeshComponent->GetStaticMesh()->GetPathName(),
            *MeshSize.ToCompactString(),
            *TableMeshComponent->GetComponentScale().ToCompactString());
    }
    else
    {
        UE_LOG(LogMahjongUI, Error,
            TEXT("Room presentation has no usable MahjongTableMesh component"));
    }

    TInlineComponentArray<UChildActorComponent*> ChildActorComponents(this);
    for (UChildActorComponent* Component : ChildActorComponents)
    {
        if (!Component || !Cast<AMahjong3DTableActor>(Component->GetChildActor()))
        {
            continue;
        }
        Component->SetWorldScale3D(FVector::OneVector);
        Component->SetWorldRotation(FRotator::ZeroRotator);
        Component->SetVisibility(true, true);
        Component->SetHiddenInGame(false, true);
        if (AActor* ChildActor = Component->GetChildActor())
        {
            ChildActor->SetActorHiddenInGame(false);
            ChildActor->SetActorRotation(FRotator::ZeroRotator);
        }
        UE_LOG(LogMahjongUI, Display,
            TEXT("Normalized Mahjong tile layout: location=%s world-scale=%s"),
            *Component->GetComponentLocation().ToCompactString(),
            *Component->GetComponentScale().ToCompactString());
    }
}

AMahjong3DTableActor* AMahjongRoomPresentationActor::GetTableActor() const
{
    // 桌子由蓝图 ChildActorComponent 持有，遍历而不依赖设计人员命名。
    TInlineComponentArray<UChildActorComponent*> ChildActorComponents(this);
    for (const UChildActorComponent* Component : ChildActorComponents)
    {
        if (AMahjong3DTableActor* TableActor = Cast<AMahjong3DTableActor>(Component->GetChildActor()))
        {
            return TableActor;
        }
    }
    return nullptr;
}

AActor* AMahjongRoomPresentationActor::GetRoomCameraActor() const
{
    // 摄像机组件直接挂在表现 Actor 上时，Actor 自身即可作为 ViewTarget。
    return FindComponentByClass<UCameraComponent>() ? const_cast<AMahjongRoomPresentationActor*>(this) : nullptr;
}
