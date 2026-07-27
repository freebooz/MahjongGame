#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "MahjongRoomPresentationActor.generated.h"

class AMahjong3DTableActor;

/**
 * 网络中立 MahjongRoomMap 的客户端视觉根节点。
 * 原生类不硬编码场景组件；BP_MahjongRoomPresentation 持有可在编辑器中调整的麻将桌、
 * 摄像机和灯光组件树。客户端进入房间后在本地创建该蓝图，独立服务器不会加载此类。
 */
UCLASS(Blueprintable)
class GUIYANGMAHJONGCLIENT_API AMahjongRoomPresentationActor : public AActor
{
    GENERATED_BODY()

public:
    /** 用于在关卡中发现已存在表现 Actor 的稳定标签。 */
    static const FName PresentationTag;

    /** 初始化客户端专用的复制和 Tick 策略。 */
    AMahjongRoomPresentationActor();

    /** 从蓝图组件树中查找牌桌与预定摄像机。 */
    AMahjong3DTableActor* GetTableActor() const;
    AActor* GetRoomCameraActor() const;

private:
    virtual void BeginPlay() override;
};
