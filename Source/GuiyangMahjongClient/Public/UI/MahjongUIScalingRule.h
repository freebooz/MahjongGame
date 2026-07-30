#pragma once

#include "CoreMinimal.h"
#include "Engine/DPICustomScalingRule.h"
#include "MahjongUIScalingRule.generated.h"

/** 手机和平板共用的响应式 DPI 规则。 */
UCLASS()
class GUIYANGMAHJONGCLIENT_API UMahjongUIScalingRule final : public UDPICustomScalingRule
{
    GENERATED_BODY()

public:
    /** 根据短边和宽高比返回受限 DPI 比例；无效尺寸返回安全默认缩放。 */
    virtual float GetDPIScaleBasedOnSize(FIntPoint Size) const override;
};
