#pragma once

#include "Misc/AutomationTest.h"

#include "Table/MahjongDeckManager.h"
#include "Rules/MahjongHuChecker.h"
#include "Rules/MahjongGangChecker.h"
#include "Rules/MahjongChiPengChecker.h"
#include "Rules/GuiyangJiCalculator.h"
#include "Rules/MahjongScoreCalculator.h"
#include "Rules/GuiyangRuleSnapshot.h"
#include "Room/GuiyangRoomManager.h"
#include "Table/MahjongTableEngine.h"
#include "Auth/GuiyangLoginSaveGame.h"
#include "Auth/GuiyangLoginSubsystem.h"
#include "History/MahjongMatchHistorySaveGame.h"
#include "History/GuiyangMatchHistorySubsystem.h"
#include "Engine/GameInstance.h"
#include "Engine/StaticMesh.h"
#include "Kismet/GameplayStatics.h"
#include "Network/MahjongNetworkTypes.h"
#include "UI/MobileMahjongHUDWidget.h"
#include "UI/MobileRoomWidget.h"
#include "UI/MobileReconnectOverlayWidget.h"
#include "Network/GuiyangReconnectSubsystem.h"
#include "UI/MobileRuleSummaryWidget.h"
#include "UI/MahjongTileVisualLibrary.h"
#include "UI/MahjongLocalSettings.h"
#include "UI/MobileSettingsWidget.h"
#include "UI/MahjongResponsiveScaleBox.h"
#include "UI/MahjongUIScalingRule.h"
#include "Game/GuiyangMahjongPlayerController.h"
#include "Game/Mahjong3DTableActor.h"
#include "Game/MahjongRoomCameraActor.h"
#include "Game/MahjongRoomPresentationActor.h"
#include "Settings/MahjongRoomPresentationSettings.h"
#include "CineCameraComponent.h"
#include "Components/ChildActorComponent.h"
#include "Components/DirectionalLightComponent.h"
#include "Components/RectLightComponent.h"
#include "Components/SkyLightComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/DirectionalLight.h"
#include "Engine/Level.h"
#include "Engine/SkyLight.h"
#include "Engine/World.h"
#include "UObject/UnrealType.h"
#include "Sound/SoundBase.h"
#if WITH_EDITOR
#include "WidgetBlueprint.h"
#include "Blueprint/WidgetTree.h"
#include "Components/Button.h"
#include "Components/Border.h"
#include "Components/CanvasPanelSlot.h"
#include "Components/CheckBox.h"
#include "Components/Image.h"
#include "Components/Slider.h"
#include "Components/TextBlock.h"
#endif

#if WITH_DEV_AUTOMATION_TESTS

/**
 * 自动化测试共享牌构造器。
 * 仅在开发自动化构建中可见，生成确定性的规则牌索引和唯一 ID，不参与运行时游戏逻辑。
 */
namespace MahjongTest
{
    /** 根据 0～33 的规则索引生成一张确定性测试牌；调用方负责提供用例内唯一 ID。 */
    inline FMahjongTile MakeTile(const int32 RuleIndex, const int32 UniqueId)
    {
        FMahjongTile Tile;
        Tile.UniqueId = UniqueId;
        if (RuleIndex < 27)
        {
            Tile.Type = EMahjongTileType::Number;
            Tile.Suit = RuleIndex < 9 ? EMahjongSuit::Characters : RuleIndex < 18 ? EMahjongSuit::Bamboo : EMahjongSuit::Dots;
            Tile.Rank = RuleIndex % 9 + 1;
        }
        else
        {
            static const EMahjongTileType Honors[] = { EMahjongTileType::East, EMahjongTileType::South, EMahjongTileType::West,
                EMahjongTileType::North, EMahjongTileType::RedDragon, EMahjongTileType::GreenDragon, EMahjongTileType::WhiteDragon };
            Tile.Type = Honors[RuleIndex - 27];
            Tile.Suit = RuleIndex <= 30 ? EMahjongSuit::Winds : EMahjongSuit::Dragons;
        }
        return Tile;
    }

    /** 按给定规则索引顺序创建手牌；唯一 ID 从零递增，便于断言排序与动作结果。 */
    inline FMahjongHand MakeHand(std::initializer_list<int32> RuleIndices)
    {
        FMahjongHand Hand;
        int32 UniqueId = 0;
        for (const int32 Index : RuleIndices)
        {
            Hand.Tiles.Add(MakeTile(Index, UniqueId++));
        }
        return Hand;
    }
}

#endif

