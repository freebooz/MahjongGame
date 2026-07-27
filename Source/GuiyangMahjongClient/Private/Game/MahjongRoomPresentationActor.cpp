#include "Game/MahjongRoomPresentationActor.h"

#include "Camera/CameraComponent.h"
#include "Components/ChildActorComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Game/Mahjong3DTableActor.h"
#include "Engine/StaticMesh.h"
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

    // Older presentation assets were authored for a 115 cm prototype and some saved component
    // hierarchies still carry a 10x parent scale. Normalize the two runtime visual branches from
    // their actual mesh bounds so stale Blueprint transforms cannot hide the 300 cm table.
    UStaticMeshComponent* TableMeshComponent = nullptr;
    TInlineComponentArray<UStaticMeshComponent*> StaticMeshComponents(this);
    for (UStaticMeshComponent* Component : StaticMeshComponents)
    {
        if (Component && Component->GetName() == TEXT("MahjongTableMesh"))
        {
            TableMeshComponent = Component;
            break;
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
