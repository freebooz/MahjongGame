#pragma once

#include "CoreMinimal.h"
#include "Blueprint/UserWidget.h"
#include "Core/MahjongTypes.h"
#include "MobileRuleConfigWidget.generated.h"

class UCheckBox;
class UEditableTextBox;

DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FMahjongRuleConfigChanged, FMahjongRuleConfig, Config);

/** 房间规则配置组件。只编辑请求参数，不直接修改房间或牌桌权威状态。 */
UCLASS(Abstract, BlueprintType)
class GUIYANGMAHJONGCLIENT_API UMobileRuleConfigWidget : public UUserWidget
{
    GENERATED_BODY()

protected:
    /** 绑定全部规则控件并广播初始配置。 */
    virtual void NativeConstruct() override;

    /** 牌墙模式与贵阳特殊规则开关。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UCheckBox> Chk_Standard136;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UCheckBox> Chk_ChongFengJi;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UCheckBox> Chk_ZeRenJi;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UCheckBox> Chk_WuGuJi;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UCheckBox> Chk_QiangGangHu;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UCheckBox> Chk_YiPaoDuoXiang;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UCheckBox> Chk_QiDui;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UCheckBox> Chk_TimeoutAutoPlay;
    /** 计分倍率及出牌、响应、重连超时输入。 */
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UEditableTextBox> Txt_BaseScore;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UEditableTextBox> Txt_JiScore;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UEditableTextBox> Txt_GangScore;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UEditableTextBox> Txt_ZiMoMultiplier;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UEditableTextBox> Txt_TurnTimeout;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UEditableTextBox> Txt_ReactionTimeout;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UEditableTextBox> Txt_ReconnectTimeout;

    /** 任一控件变化时重新解析并广播有效配置。 */
    UFUNCTION() void HandleCheckChanged(bool bChecked);
    UFUNCTION() void HandleTextChanged(const FText& Text);

public:
    /** 有效规则配置变化事件。 */
    UPROPERTY(BlueprintAssignable, Category="麻将|规则") FMahjongRuleConfigChanged OnRuleConfigChanged;

    /** 从结构体写入控件，或严格解析控件到结构体。 */
    UFUNCTION(BlueprintCallable, Category="麻将|规则") void SetRuleConfig(const FMahjongRuleConfig& Config);
    bool TryGetRuleConfig(FMahjongRuleConfig& OutConfig, FString& OutError) const;

private:
    void BroadcastRuleConfigChanged();
};
