#pragma once

#include "CoreMinimal.h"
#include "Blueprint/UserWidget.h"
#include "Auth/GuiyangLoginTypes.h"
#include "MobileLoginWidget.generated.h"

class UButton;
class UCheckBox;
class UCircularThrobber;
class UImage;
class UTextBlock;

/** 登录页面。只调用登录子系统，不直接创建账号、Session 或修改网络权威状态。 */
UCLASS(Abstract, BlueprintType)
class GUIYANGMAHJONGCLIENT_API UMobileLoginWidget : public UUserWidget
{
    GENERATED_BODY()

protected:
    /** 绑定按钮与登录子系统事件，并初始化当前登录状态。 */
    virtual void NativeConstruct() override;
    virtual void NativeDestruct() override;

    /** 全屏背景与游戏标志。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UImage> Img_Background;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UImage> Img_GameLogo;
    /** 微信/游客登录及协议确认控件。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_WechatLogin;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_GuestLogin;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UCheckBox> Chk_AgreeTerms;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_UserAgreement;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_PrivacyPolicy;
    /** 状态、版本文本和登录中转圈。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_LoginStatus;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_Version;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UCircularThrobber> Loading_Login;

    /** 登录按钮、协议链接和登录结果事件处理。 */
    UFUNCTION() void HandleGuestLogin();
    UFUNCTION() void HandleWechatLogin();
    UFUNCTION() void HandleUserAgreement();
    UFUNCTION() void HandlePrivacyPolicy();
    UFUNCTION() void HandleLoginStateChanged(EGuiyangLoginState State, const FGuiyangLoginProfile& Profile);
    UFUNCTION() void HandleLoginFailed(const FString& ChineseReason);

public:
    /** 同步状态文字并切换加载动画及按钮可用性。 */
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void SetLoginStatus(const FString& ChineseStatus, bool bLoading);

private:
    /** 未同意用户协议与隐私政策时阻止登录并给出提示。 */
    bool ValidateAgreement();
};
