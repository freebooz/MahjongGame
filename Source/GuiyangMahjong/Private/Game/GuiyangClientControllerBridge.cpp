#include "Game/GuiyangClientControllerBridge.h"

namespace
{
    /** 由客户端模块在启动时注册；服务端目标中保持为空。 */
    FGuiyangClientBridgeFactory GClientBridgeFactory;
}

// 使用移动语义替换旧工厂，支持编辑器热重载。
void FGuiyangClientBridgeRegistry::Register(FGuiyangClientBridgeFactory Factory)
{
    GClientBridgeFactory = MoveTemp(Factory);
}

void FGuiyangClientBridgeRegistry::Unregister()
{
    GClientBridgeFactory = nullptr;
}

UObject* FGuiyangClientBridgeRegistry::Create(AGuiyangMahjongPlayerController& Controller)
{
    // 没有链接客户端模块时安全返回空，独立服务器不会创建 UI 桥接。
    return GClientBridgeFactory ? GClientBridgeFactory(Controller) : nullptr;
}
