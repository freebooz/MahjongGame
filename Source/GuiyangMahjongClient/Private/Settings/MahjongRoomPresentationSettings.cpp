#include "Settings/MahjongRoomPresentationSettings.h"

UMahjongRoomPresentationSettings::UMahjongRoomPresentationSettings()
{
    // 具体蓝图类只由客户端平台配置提供。若在原生构造函数中写默认路径，
    // 编辑器执行 Dedicated Server Cook 时会形成软引用并污染服务器包。
    PresentationClass.Reset();
}
