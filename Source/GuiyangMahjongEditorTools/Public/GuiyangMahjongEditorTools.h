#pragma once

#include "Modules/ModuleManager.h"

class IConsoleObject;

/** 仅编辑器加载的项目工具模块，不进入客户端或独立服务器包。 */
class FGuiyangMahjongEditorToolsModule final : public IModuleInterface
{
public:
    /** 注册控制台命令和引擎初始化完成后的资产修复回调。 */
    virtual void StartupModule() override;
    /** 注销所有委托和命令，避免模块重载后留下悬空回调。 */
    virtual void ShutdownModule() override;

private:
    /** 引擎完成初始化后，持久化房间展示蓝图的完整编辑器模式。 */
    void HandlePostEngineInit();
    /** 强制以包含组件树和三维视口的完整蓝图编辑器打开展示蓝图。 */
    void OpenRoomPresentationEditor();

    /** 用于在模块卸载时精确移除延迟回调。 */
    FDelegateHandle PostEngineInitHandle;
    /** “Mahjong.OpenRoomPresentationEditor”控制台命令句柄。 */
    IConsoleObject* OpenRoomPresentationCommand = nullptr;
};
