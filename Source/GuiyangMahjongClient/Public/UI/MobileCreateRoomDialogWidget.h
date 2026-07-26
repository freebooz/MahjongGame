#pragma once

#include "CoreMinimal.h"
#include "Blueprint/UserWidget.h"
#include "Core/MahjongTypes.h"
#include "MobileCreateRoomDialogWidget.generated.h"

class UButton;
class UCheckBox;
class UEditableTextBox;
class UTextBlock;
class UMobileRuleConfigWidget;
class UMobileRuleSummaryWidget;

/** 创建房间弹窗。收集基础房间参数，并通过 PlayerController 提交权威创建请求。 */
UCLASS(Abstract, BlueprintType)
class GUIYANGMAHJONGCLIENT_API UMobileCreateRoomDialogWidget : public UUserWidget
{
    GENERATED_BODY()

protected:
    /** 绑定提交、取消和规则变化事件。 */
    virtual void NativeConstruct() override;

    /** 房间局数、公开性、密码开关和密码输入。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UEditableTextBox> Txt_RoundCount;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UCheckBox> Chk_PublicRoom;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UCheckBox> Chk_EnablePassword;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UEditableTextBox> Txt_Password;
    /** 校验状态、规则编辑器和人类可读摘要。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_Status;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UMobileRuleConfigWidget> RuleConfig;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UMobileRuleSummaryWidget> RuleSummary;
    /** 提交和取消按钮。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Create;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_Cancel;

    /** 收集控件值、校验并发起权威创建请求。 */
    UFUNCTION() void HandleCreate();
    UFUNCTION() void HandleCancel();
    UFUNCTION() void HandleRuleConfigChanged(FMahjongRuleConfig Config);
    UFUNCTION() void HandleOptionCheckChanged(bool bChecked);
    UFUNCTION() void HandleOptionTextChanged(const FText& Text);

private:
    /** 任一选项变化后重建规则摘要。 */
    void RefreshRuleSummary();
};
