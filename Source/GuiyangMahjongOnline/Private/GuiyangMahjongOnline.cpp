#include "GuiyangMahjongOnline.h"
#include "Modules/ModuleManager.h"

DEFINE_LOG_CATEGORY(LogMahjongOnline);

// 注册在线公共模块；具体登录状态由 UGuiyangLoginSubsystem 管理。
IMPLEMENT_MODULE(FDefaultModuleImpl, GuiyangMahjongOnline);
