#pragma once

#include "CoreMinimal.h"
#include "Blueprint/UserWidget.h"
#include "MobileConnectServerWidget.generated.h"

class UButton; class UCheckBox; class UEditableTextBox; class UTextBlock;

/** 服务器连接页 C++ 基类。动态文本全部由 TextBlock/EditableTextBox 渲染。 */
UCLASS(Abstract, BlueprintType)
class GUIYANGMAHJONGCLIENT_API UMobileConnectServerWidget : public UUserWidget
{
    GENERATED_BODY()
protected:
    /** 视图构造后恢复本地连接设置并绑定提交按钮，不会自动发起网络连接。 */
    virtual void NativeConstruct() override;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UEditableTextBox> Txt_ServerIP;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UEditableTextBox> Txt_ServerPort;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UEditableTextBox> Txt_PlayerName;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UCheckBox> Chk_RememberAddress;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Connect;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_ConnectButton;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_Version;
    /** 校验地址、端口和玩家名后发起连接；无效输入只更新界面，不进入 ClientTravel。 */
    UFUNCTION() void HandleConnectClicked();
public:
    /** 切换连接中的禁用和提示状态，防止用户重复提交并发旅行请求。 */
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void SetConnecting(bool bConnecting);
};
