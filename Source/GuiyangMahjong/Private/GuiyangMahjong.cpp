#include "GuiyangMahjong.h"
#include "Modules/ModuleManager.h"

DEFINE_LOG_CATEGORY(LogMahjongServer);
DEFINE_LOG_CATEGORY(LogMahjongNet);
DEFINE_LOG_CATEGORY(LogMahjongRule);
DEFINE_LOG_CATEGORY(LogMahjongScore);
DEFINE_LOG_CATEGORY(LogMahjongUI);
DEFINE_LOG_CATEGORY(LogMahjongAndroid);
DEFINE_LOG_CATEGORY(LogMahjongReconnect);
DEFINE_LOG_CATEGORY(LogMahjongMCP);

// 注册共享游戏框架模块；这里不放客户端或服务端专属初始化逻辑。
IMPLEMENT_PRIMARY_GAME_MODULE(FDefaultGameModuleImpl, GuiyangMahjong, "GuiyangMahjong");
