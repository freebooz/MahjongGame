#include "UI/MobileSettlementWidget.h"
#include "Game/GuiyangMahjongPlayerController.h"
#include "Components/Button.h"
#include "Components/Border.h"
#include "Components/Image.h"
#include "Components/TextBlock.h"
#include "Components/VerticalBox.h"
#include "Blueprint/WidgetTree.h"
#include "Engine/World.h"
#include "TimerManager.h"
#include "GuiyangMahjong.h"

namespace
{
    constexpr float AutoNextRoundDelaySeconds = 10.0f;
}

void UMobileSettlementWidget::NativeConstruct()
{
    Super::NativeConstruct();
    // 结算控件叠加在仍存活的 3D 牌桌上；隐藏旧资产残留的全屏底图和遮罩，避免结算时遮住牌墙与手牌。
    for (const FName BackgroundName : {
        FName(TEXT("Scale_BackgroundFill")),
        FName(TEXT("Size_BackgroundDesign")),
        FName(TEXT("Background_ComponentSlot")),
        FName(TEXT("Img_Background")),
        FName(TEXT("Border_Mask"))})
    {
        if (UWidget* Background =
            WidgetTree ? WidgetTree->FindWidget(BackgroundName) : nullptr)
        {
            Background->SetVisibility(ESlateVisibility::Collapsed);
        }
    }
    Btn_NextRound->OnClicked.AddUniqueDynamic(this, &ThisClass::HandleNextRound);
    Btn_BackLobby->OnClicked.AddUniqueDynamic(this, &ThisClass::HandleBackLobby);
    if (UTextBlock* ConfirmLabel =
        Cast<UTextBlock>(WidgetTree->FindWidget(TEXT("Btn_NextRound_Label"))))
    {
        ConfirmLabel->SetText(FText::FromString(TEXT("确定")));
    }
}

void UMobileSettlementWidget::NativeDestruct()
{
    ClearAutoNextRoundTimer();
    Super::NativeDestruct();
}

void UMobileSettlementWidget::SetSettlementResult(const FMahjongSettlementResult& Result)
{
    ClearAutoNextRoundTimer();
    bRoundSettlementActive = true;
    bNextRoundRequested = false;
    Btn_NextRound->SetVisibility(ESlateVisibility::Visible);
    Btn_NextRound->SetIsEnabled(true);
    Txt_ResultTitle->SetText(FText::FromString(Result.bDrawGame ? TEXT("本局流局") : FString::Printf(TEXT("座位 %d 胡牌"), Result.WinnerSeat)));
    Txt_HuType->SetText(FText::FromString(Result.bSelfDraw ? TEXT("自摸") : TEXT("点炮")));
    FString JiSummary = Result.FlippedJiTile.IsValid()
        ? FString::Printf(TEXT("翻鸡牌：%s"), *Result.FlippedJiTile.ToDebugString())
        : TEXT("本局未翻鸡");
    for (int32 Seat = 0; Seat < Result.PlayerJiCounts.Num(); ++Seat)
    {
        const auto ReadCount = [Seat](const TArray<int32>& Counts)
        {
            return Counts.IsValidIndex(Seat) ? Counts[Seat] : 0;
        };
        // 黑八和冲锋鸡是总鸡中的审计子集，文案按分项展示但不暗示需要再次相加。
        JiSummary += FString::Printf(TEXT("\n座位%d：总%d（内鸡%d·外鸡%d·黑八%d·冲锋鸡%d）"),
            Seat, Result.PlayerJiCounts[Seat], ReadCount(Result.PlayerInnerJiCounts),
            ReadCount(Result.PlayerOuterJiCounts), ReadCount(Result.PlayerWuGuJiCounts),
            ReadCount(Result.PlayerChongFengJiCounts));
    }
    Txt_JiResult->SetText(FText::FromString(JiSummary));
    Panel_PlayerScores->ClearChildren();
    for (const FMahjongPlayerScoreResult& Player : Result.PlayerResults)
    {
        UTextBlock* Row = NewObject<UTextBlock>(this);
        Row->SetText(FText::FromString(FString::Printf(TEXT("座位 %d　基础 %+d　鸡 %+d　特殊鸡 %+d　杠 %+d　合计 %+d"),
            Player.SeatIndex, Player.BaseScoreDelta, Player.JiScoreDelta,
            Player.SpecialJiScoreDelta, Player.GangScoreDelta, Player.TotalDelta)));
        Panel_PlayerScores->AddChildToVerticalBox(Row);
    }
    if (UWorld* World = GetWorld())
    {
        World->GetTimerManager().SetTimer(AutoNextRoundTimerHandle, this,
            &ThisClass::HandleAutoNextRound, AutoNextRoundDelaySeconds, false);
    }
    UE_LOG(LogMahjongUI, Log, TEXT("结算弹窗数据刷新完成"));
}

void UMobileSettlementWidget::SetFinalSettlementResult(const FMahjongFinalSettlementResult& Result)
{
    ClearAutoNextRoundTimer();
    bRoundSettlementActive = false;
    bNextRoundRequested = true;
    Txt_ResultTitle->SetText(FText::FromString(TEXT("最终大结算")));
    Txt_HuType->SetText(FText::FromString(FString::Printf(TEXT("完成 %d 局"), Result.CompletedRounds)));
    Txt_JiResult->SetText(FText::FromString(FString::Printf(TEXT("房间号：%s"), *Result.RoomId)));
    Panel_PlayerScores->ClearChildren();
    for (const FMahjongFinalPlayerResult& Player : Result.Players)
    {
        UTextBlock* Row = NewObject<UTextBlock>(this);
        Row->SetText(FText::FromString(FString::Printf(TEXT("第 %d 名　座位 %d　%s　总分 %+d"),
            Player.Rank, Player.SeatIndex, *Player.PlayerName, Player.TotalScore)));
        Panel_PlayerScores->AddChildToVerticalBox(Row);
    }
    Btn_NextRound->SetVisibility(ESlateVisibility::Collapsed);
}

void UMobileSettlementWidget::HandleNextRound()
{
    AcknowledgeRoundAndClose();
}

void UMobileSettlementWidget::HandleAutoNextRound()
{
    AcknowledgeRoundAndClose();
}

void UMobileSettlementWidget::AcknowledgeRoundAndClose()
{
    // 只关闭结算遮罩，房间 HUD、摄像机、牌桌与牌墙继续存活，由权威服务端推进下一局。
    if (!bRoundSettlementActive || bNextRoundRequested)
    {
        return;
    }
    bNextRoundRequested = true;
    ClearAutoNextRoundTimer();
    Btn_NextRound->SetIsEnabled(false);
    SetVisibility(ESlateVisibility::Collapsed);
    if (AGuiyangMahjongPlayerController* PC =
        Cast<AGuiyangMahjongPlayerController>(GetOwningPlayer()))
    {
        PC->Server_RequestNextRound();
    }
}

void UMobileSettlementWidget::ClearAutoNextRoundTimer()
{
    if (UWorld* World = GetWorld())
    {
        World->GetTimerManager().ClearTimer(AutoNextRoundTimerHandle);
    }
}

void UMobileSettlementWidget::DismissRoundSettlement()
{
    if (!bRoundSettlementActive)
    {
        return;
    }
    ClearAutoNextRoundTimer();
    bRoundSettlementActive = false;
    bNextRoundRequested = true;
    SetVisibility(ESlateVisibility::Collapsed);
}

void UMobileSettlementWidget::HandleBackLobby()
{
    if (AGuiyangMahjongPlayerController* PC = Cast<AGuiyangMahjongPlayerController>(GetOwningPlayer()))
    {
        PC->ReturnToLobby();
    }
    RemoveFromParent();
}
