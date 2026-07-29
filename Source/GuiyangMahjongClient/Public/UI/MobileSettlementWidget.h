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
    virtual void NativeConstruct() override;
    virtual void NativeDestruct() override;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_ResultTitle;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_HuType;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UTextBlock> Txt_JiResult;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UVerticalBox> Panel_PlayerScores;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_NextRound;
    UPROPERTY(meta=(BindWidget)) TObjectPtr<UButton> Btn_BackLobby;
    UFUNCTION() void HandleNextRound();
    UFUNCTION() void HandleAutoNextRound();
    UFUNCTION() void HandleBackLobby();
    FTimerHandle AutoNextRoundTimerHandle;
    bool bRoundSettlementActive = false;
    bool bNextRoundRequested = false;
    void AcknowledgeRoundAndClose();
    void ClearAutoNextRoundTimer();
public:
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void SetSettlementResult(const FMahjongSettlementResult& Result);
    UFUNCTION(BlueprintCallable, Category="麻将|UI") void SetFinalSettlementResult(
        const FMahjongFinalSettlementResult& Result);
    /** Close an intermediate-round result without sending a duplicate acknowledgement. */
    void DismissRoundSettlement();
};
