#pragma once

#include "CoreMinimal.h"
#include "Blueprint/UserWidget.h"
#include "Rules/GuiyangRuleSnapshot.h"
#include "MobileRuleSummaryWidget.generated.h"

class UTextBlock;

/** 展示不可变规则快照及其哈希，供创建房确认和房间内一致性核对。 */
UCLASS(Abstract, BlueprintType)
class GUIYANGMAHJONGCLIENT_API UMobileRuleSummaryWidget : public UUserWidget
{
    GENERATED_BODY()

protected:
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_RuleTitle;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_RuleLines;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_RuleHash;

public:
    /** 从可编辑配置创建规范快照后显示摘要；主要用于创建房间确认阶段。 */
    UFUNCTION(BlueprintCallable, Category="麻将|规则")
    void SetRuleConfig(const FMahjongRuleConfig& Config, int32 RoundCount, bool bPasswordProtected);

    /** 直接显示服务端冻结的规则快照和短哈希，用于房间内一致性核对。 */
    UFUNCTION(BlueprintCallable, Category="麻将|规则")
    void SetRuleSnapshot(const FGuiyangRuleSnapshot& Snapshot, int32 RoundCount, bool bPasswordProtected);

    /** 生成纯展示文本，不修改快照；密码仅显示保护状态，不接收密码正文。 */
    static FString BuildSummaryText(const FGuiyangRuleSnapshot& Snapshot, int32 RoundCount, bool bPasswordProtected);
};
