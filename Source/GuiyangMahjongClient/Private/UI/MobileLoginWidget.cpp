#include "UI/MobileLoginWidget.h"

#include "Auth/GuiyangLoginSubsystem.h"
#include "Components/Button.h"
#include "Components/CheckBox.h"
#include "Components/CircularThrobber.h"
#include "Components/TextBlock.h"
#include "GuiyangMahjong.h"

void UMobileLoginWidget::NativeConstruct()
{
    Super::NativeConstruct();
    Btn_GuestLogin->OnClicked.AddUniqueDynamic(this, &ThisClass::HandleGuestLogin);
    Btn_WechatLogin->OnClicked.AddUniqueDynamic(this, &ThisClass::HandleWechatLogin);
    Btn_UserAgreement->OnClicked.AddUniqueDynamic(this, &ThisClass::HandleUserAgreement);
    Btn_PrivacyPolicy->OnClicked.AddUniqueDynamic(this, &ThisClass::HandlePrivacyPolicy);
    Txt_Version->SetText(FText::FromString(TEXT("版本 0.2.0 · UE 5.8")));
    SetLoginStatus(TEXT("请选择登录方式"), false);

    if (UGuiyangLoginSubsystem* Login = GetGameInstance()->GetSubsystem<UGuiyangLoginSubsystem>())
    {
        Login->OnLoginStateChanged.AddUniqueDynamic(this, &ThisClass::HandleLoginStateChanged);
        Login->OnLoginFailed.AddUniqueDynamic(this, &ThisClass::HandleLoginFailed);
    }
    UE_LOG(LogMahjongUI, Log, TEXT("登录界面创建完成"));
}

void UMobileLoginWidget::NativeDestruct()
{
    if (UGuiyangLoginSubsystem* Login = GetGameInstance()->GetSubsystem<UGuiyangLoginSubsystem>())
    {
        Login->OnLoginStateChanged.RemoveDynamic(this, &ThisClass::HandleLoginStateChanged);
        Login->OnLoginFailed.RemoveDynamic(this, &ThisClass::HandleLoginFailed);
    }
    Super::NativeDestruct();
}

bool UMobileLoginWidget::ValidateAgreement()
{
    if (!Chk_AgreeTerms->IsChecked())
    {
        SetLoginStatus(TEXT("请先阅读并同意用户协议和隐私政策"), false);
        UE_LOG(LogMahjongUI, Warning, TEXT("登录请求被本地拒绝：尚未同意协议"));
        return false;
    }
    return true;
}

void UMobileLoginWidget::HandleGuestLogin()
{
    if (!ValidateAgreement()) return;
    if (UGuiyangLoginSubsystem* Login = GetGameInstance()->GetSubsystem<UGuiyangLoginSubsystem>()) Login->LoginAsGuest();
}

void UMobileLoginWidget::HandleWechatLogin()
{
    if (!ValidateAgreement()) return;
    if (UGuiyangLoginSubsystem* Login = GetGameInstance()->GetSubsystem<UGuiyangLoginSubsystem>()) Login->LoginWithWechat();
}

void UMobileLoginWidget::HandleUserAgreement()
{
    SetLoginStatus(TEXT("用户协议：文明游戏，禁止作弊和非法交易"), false);
    UE_LOG(LogMahjongUI, Log, TEXT("玩家查看用户协议"));
}

void UMobileLoginWidget::HandlePrivacyPolicy()
{
    SetLoginStatus(TEXT("隐私政策：仅收集账号登录和联机所需的最少信息"), false);
    UE_LOG(LogMahjongUI, Log, TEXT("玩家查看隐私政策"));
}

void UMobileLoginWidget::HandleLoginStateChanged(const EGuiyangLoginState State, const FGuiyangLoginProfile& Profile)
{
    switch (State)
    {
    case EGuiyangLoginState::LoggingIn:
        SetLoginStatus(TEXT("登录中，请稍候……"), true);
        break;
    case EGuiyangLoginState::LoggedIn:
        SetLoginStatus(FString::Printf(TEXT("登录成功，欢迎 %s"), *Profile.DisplayName), false);
        break;
    case EGuiyangLoginState::Expired:
        SetLoginStatus(TEXT("会话已过期，请重新登录"), false);
        break;
    default:
        SetLoginStatus(TEXT("请选择登录方式"), false);
        break;
    }
}

void UMobileLoginWidget::HandleLoginFailed(const FString& ChineseReason)
{
    SetLoginStatus(ChineseReason, false);
}

void UMobileLoginWidget::SetLoginStatus(const FString& ChineseStatus, const bool bLoading)
{
    Txt_LoginStatus->SetText(FText::FromString(ChineseStatus));
    Loading_Login->SetVisibility(bLoading ? ESlateVisibility::Visible : ESlateVisibility::Collapsed);
    Btn_GuestLogin->SetIsEnabled(!bLoading);
    Btn_WechatLogin->SetIsEnabled(!bLoading);
}
