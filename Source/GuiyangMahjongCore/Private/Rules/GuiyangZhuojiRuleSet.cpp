#include "Rules/GuiyangZhuojiRuleSet.h"

#include "Rules/MahjongHuChecker.h"

bool UGuiyangZhuojiRuleSet::CanHu(const FMahjongHand& Hand) const
{
    // 规则集只负责传递本房间快照；具体牌型拆解集中在胡牌检查器。
    return UMahjongHuChecker::CanHu(Hand, Config.bEnableQiDui);
}
