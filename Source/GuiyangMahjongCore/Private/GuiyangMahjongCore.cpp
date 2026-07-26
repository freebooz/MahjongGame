#include "GuiyangMahjongCore.h"
#include "Modules/ModuleManager.h"

DEFINE_LOG_CATEGORY(LogMahjongCore);

// 规则核心保持为纯逻辑模块，便于客户端预测、服务端权威计算和自动化测试复用。
IMPLEMENT_MODULE(FDefaultModuleImpl, GuiyangMahjongCore);
