#include "Modules/ModuleManager.h"

#include "Game/GuiyangClientControllerBridge.h"
#include "Game/GuiyangClientControllerBridgeImpl.h"
#include "Game/GuiyangMahjongPlayerController.h"

class FGuiyangMahjongClientModule final : public IModuleInterface
{
public:
    /** 注册默认客户端桥接工厂，使共享 PlayerController 无需依赖客户端实现模块。 */
    virtual void StartupModule() override
    {
        FGuiyangClientBridgeRegistry::Register([](AGuiyangMahjongPlayerController& Controller) -> UObject*
        {
            return NewObject<UGuiyangClientControllerBridgeImpl>(&Controller, NAME_None, RF_Transient);
        });
    }

    /** 模块卸载时移除工厂，避免热重载后遗留悬空回调。 */
    virtual void ShutdownModule() override
    {
        FGuiyangClientBridgeRegistry::Unregister();
    }
};

IMPLEMENT_MODULE(FGuiyangMahjongClientModule, GuiyangMahjongClient);
