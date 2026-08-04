#include "UI/MobileRuleSummaryWidget.h"

#include "Components/TextBlock.h"

FString UMobileRuleSummaryWidget::BuildSummaryText(const FGuiyangRuleSnapshot& Snapshot,
    const int32 RoundCount, const bool bPasswordProtected)
{
    const FMahjongRuleConfig& Config = Snapshot.Config;
    const FString TileSet = Config.TileSetMode == EMahjongTileSetMode::Standard136
        ? TEXT("136 张标准牌") : TEXT("108 张万筒条");
    const FString JiScope = Config.JiCountingScope == EMahjongJiCountingScope::HandMeldAndDiscard
        ? TEXT("内外鸡全算") : TEXT("按房间鸡区范围");
    const FString JiRules = FString::Printf(TEXT("冲锋鸡%s · 责任鸡%s · 黑八%s · %s"),
        Config.bEnableChongFengJi ? TEXT("开") : TEXT("关"),
        Config.bEnableZeRenJi ? TEXT("开") : TEXT("关"),
        Config.bEnableWuGuJi ? TEXT("开") : TEXT("关"), *JiScope);
    const FString HuRules = FString::Printf(TEXT("抢杠胡%s · 一炮多响%s · 七对%s · 接炮%s"),
        Config.bEnableQiangGangHu ? TEXT("开") : TEXT("关"),
        Config.bEnableYiPaoDuoXiang ? TEXT("开") : TEXT("关"),
        Config.bEnableQiDui ? TEXT("开") : TEXT("关"),
        Config.bRequireGangForDiscardHu ? TEXT("须先杠") : TEXT("不验杠"));
    const FString ScoreRules = FString::Printf(TEXT("底分 %d · 鸡分 %d · 豆分 %d · 自摸 ×%d"),
        Config.BaseScore, Config.JiScore, Config.GangScore, Config.ZiMoMultiplier);
    const FString TimeoutRules = Config.bEnableTimeoutAutoPlay
        ? FString::Printf(TEXT("出牌 %d 秒 · 操作 %d 秒 · 重连 %d 秒"),
            Config.TurnTimeoutSeconds, Config.ReactionTimeoutSeconds, Config.ReconnectTimeoutSeconds)
        : TEXT("自动托管关闭");
    return FString::Printf(TEXT("%s · %d 局 · %s\n%s\n%s\n%s\n%s"),
        *TileSet, RoundCount, bPasswordProtected ? TEXT("密码房") : TEXT("公开房"),
        *JiRules, *HuRules, *ScoreRules, *TimeoutRules);
}

void UMobileRuleSummaryWidget::SetRuleConfig(const FMahjongRuleConfig& Config,
    const int32 RoundCount, const bool bPasswordProtected)
{
    SetRuleSnapshot(UGuiyangRuleSnapshotLibrary::CreateSnapshot(Config), RoundCount, bPasswordProtected);
}

void UMobileRuleSummaryWidget::SetRuleSnapshot(const FGuiyangRuleSnapshot& Snapshot,
    const int32 RoundCount, const bool bPasswordProtected)
{
    Txt_RuleTitle->SetText(FText::FromString(FString::Printf(TEXT("%s · 版本 %d"),
        *Snapshot.Config.RuleId.ToString(), Snapshot.Config.RuleVersion)));
    Txt_RuleLines->SetText(FText::FromString(BuildSummaryText(Snapshot, RoundCount, bPasswordProtected)));
    Txt_RuleHash->SetText(FText::FromString(FString::Printf(TEXT("规则哈希：%s"), *Snapshot.RuleHash.Left(16))));
}
