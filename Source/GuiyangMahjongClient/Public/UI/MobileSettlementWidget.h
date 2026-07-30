#pragma once

#include "CoreMinimal.h"
#include "Blueprint/UserWidget.h"
#include "Core/MahjongTypes.h"
#include "Network/MahjongNetworkTypes.h"
#include "MobileSettlementWidget.generated.h"

class UButton; class UTextBlock; class UVerticalBox;

/** 单局结算弹窗，只展示 Client_ShowSettlement 下发的权威结果。 */
UCLASS(Abstract, BlueprintType)
class GUIYANGMAHJONGCLIENT_API UMobileSettlementWidget : public UUserWidget
{
    GENERATED_BODY()
protected:
    /** 构造时绑定结算操作；销毁时清理自动确认计时器，禁止回调访问失效 Widget。 */
    virtual void NativeConstruct() override;
    virtual void NativeDestruct() override;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_ResultTitle;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_HuType;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_JiResult;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UVerticalBox> Panel_PlayerScores;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_NextRound;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_BackLobby;
    /** 下一局、自动确认和返回大厅入口共享幂等保护，防止重复发送结算确认。 */
    UFUNCTION() void HandleNextRound();
    UFUNCTION() void HandleAutoNextRound();
    UFUNCTION() void HandleBackLobby();
    /** 自动下一局计时器只属于当前控件实例，销毁时必须清理。 */
    FTimerHandle AutoNextRoundTimerHandle;
    /** 标记中间局结算及确认发送状态，避免关闭弹窗时再次确认。 */
    bool bRoundSettlementActive = false;
    bool bNextRoundRequested = false;
    /** 确认当前中间局并关闭弹窗；仅在尚未发送请求时产生一次网络副作用。 */
    void AcknowledgeRoundAndClose();
    /** 取消自动确认回调，确保控件销毁或最终结算后不再触发下一局。 */
    void ClearAutoNextRoundTimer();
public:
    /** 展示单局权威结算并启动可取消的自动下一局倒计时。 */
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void SetSettlementResult(const FMahjongSettlementResult& Result);
    /** 展示比赛最终排名；会关闭中间局确认状态且不再发送下一局请求。 */
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void SetFinalSettlementResult(
        const FMahjongFinalSettlementResult& Result);
    /** 关闭中间局结果但不重复发送确认，供外部状态切换安全收起弹窗。 */
    void DismissRoundSettlement();
};
