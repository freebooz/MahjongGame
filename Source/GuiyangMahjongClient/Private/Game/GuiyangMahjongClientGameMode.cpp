#include "Game/GuiyangMahjongClientGameMode.h"

#include "Game/GuiyangMahjongPlayerController.h"

AGuiyangMahjongClientGameMode::AGuiyangMahjongClientGameMode()
{
    // Entry 关卡只需要控制器承载登录/HUD，不生成 Pawn 或旧式 HUD。
    PlayerControllerClass = AGuiyangMahjongPlayerController::StaticClass();
    DefaultPawnClass = nullptr;
    HUDClass = nullptr;
    // 玩家在麻将房间中使用固定桌面摄像机，不参与 Pawn 出生流程。
    bStartPlayersAsSpectators = true;
}
