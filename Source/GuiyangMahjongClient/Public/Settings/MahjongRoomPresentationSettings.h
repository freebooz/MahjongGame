#pragma once

#include "CoreMinimal.h"
#include "Engine/DeveloperSettings.h"
#include "UObject/SoftObjectPtr.h"
#include "MahjongRoomPresentationSettings.generated.h"

class AMahjongRoomPresentationActor;

/** 保存客户端专用的、由设计人员编辑的麻将房间表现类软引用。 */
UCLASS(Config=Game, DefaultConfig, meta=(DisplayName="Mahjong Room Presentation"))
class GUIYANGMAHJONGCLIENT_API UMahjongRoomPresentationSettings final : public UDeveloperSettings
{
    GENERATED_BODY()

public:
    /** 写入默认蓝图类路径，但保持软引用以免服务器或大厅提前加载资源。 */
    UMahjongRoomPresentationSettings();

    virtual FName GetCategoryName() const override { return TEXT("Game"); }

    /** 加入 MahjongRoomMap 后在本地生成的表现蓝图类。 */
    UPROPERTY(Config, EditAnywhere, Category="Presentation",
        meta=(AllowedClasses="/Script/GuiyangMahjongClient.MahjongRoomPresentationActor"))
    TSoftClassPtr<AMahjongRoomPresentationActor> PresentationClass;
};
