#pragma once

#include "CoreMinimal.h"
#include "CineCameraActor.h"
#include "MahjongRoomCameraActor.generated.h"

/**
 * 真实麻将房间使用的可编辑摄像机预设。
 * 摄像机由房间表现 Actor 持有；在 MahjongRoomVisualPreviewMap 中驾驶该子摄像机，
 * 即可直接调整 CineCameraComponent 的位置、焦距和视角。
 */
UCLASS(Blueprintable)
class GUIYANGMAHJONGCLIENT_API AMahjongRoomCameraActor : public ACineCameraActor
{
    GENERATED_BODY()

public:
    /** 设置适合横屏桌面俯视的稳定默认镜头。 */
    AMahjongRoomCameraActor(const FObjectInitializer& ObjectInitializer);

    /** 供表现层查找摄像机组件的稳定标签。 */
    static const FName RoomCameraTag;

    /** 锁定曝光和后处理，避免运行时自动曝光造成闪烁或过曝。 */
    void ConfigureStablePostProcess();
};
