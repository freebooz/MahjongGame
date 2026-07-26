#include "Game/MahjongRoomPresentationActor.h"

#include "Camera/CameraComponent.h"
#include "Components/ChildActorComponent.h"
#include "Game/Mahjong3DTableActor.h"

const FName AMahjongRoomPresentationActor::PresentationTag(TEXT("MahjongRoomPresentation"));

AMahjongRoomPresentationActor::AMahjongRoomPresentationActor()
{
    PrimaryActorTick.bCanEverTick = false;
    SetReplicates(false);
    SetCanBeDamaged(false);
    Tags.AddUnique(PresentationTag);
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
