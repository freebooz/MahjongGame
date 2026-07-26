#include "UI/MahjongResponsiveScaleBox.h"

#include "Engine/Engine.h"
#include "Engine/GameViewportClient.h"

EStretch::Type UMahjongResponsiveScaleBox::ResolveStretchForViewport(const FIntPoint ViewportSize)
{
    // 前景控件在手机、平板和桌面端都保持设计稿 16:9 几何。
    // 全屏覆盖交给独立背景层；拉伸前景会改变点击区域并把边缘按钮挤出屏幕。
    return EStretch::ScaleToFit;
}

void UMahjongResponsiveScaleBox::SynchronizeProperties()
{
#if PLATFORM_ANDROID
    FVector2D ViewportSize = FVector2D::ZeroVector;
    if (GEngine && GEngine->GameViewport)
    {
        GEngine->GameViewport->GetViewportSize(ViewportSize);
    }
    // Android 运行时以真实物理视口重新同步，兼容刘海屏和横竖屏切换。
    if (ViewportSize.X > 0.0f && ViewportSize.Y > 0.0f)
    {
        SetStretch(ResolveStretchForViewport(FIntPoint(
            FMath::RoundToInt(ViewportSize.X), FMath::RoundToInt(ViewportSize.Y))));
    }
#endif
    Super::SynchronizeProperties();
}
