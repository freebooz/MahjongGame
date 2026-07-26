#include "UI/MobileErrorToastWidget.h"
#include "Components/TextBlock.h"
#include "Engine/World.h"
#include "TimerManager.h"
#include "GuiyangMahjong.h"

void UMobileErrorToastWidget::ShowToast(const FString& Message, const float DurationSeconds)
{
    // 每次显示都会重置隐藏计时，连续错误只保留最后一条的完整阅读时间。
    Txt_Message->SetText(FText::FromString(Message));
    SetVisibility(ESlateVisibility::HitTestInvisible);
    GetWorld()->GetTimerManager().ClearTimer(HideTimer);
    GetWorld()->GetTimerManager().SetTimer(HideTimer, this, &ThisClass::HideToast, FMath::Max(0.1f, DurationSeconds), false);
    UE_LOG(LogMahjongUI, Warning, TEXT("Toast：%s"), *Message);
}
// 隐藏而不销毁控件，下一次错误可立即复用。
void UMobileErrorToastWidget::HideToast(){ SetVisibility(ESlateVisibility::Collapsed); }
