#include "MahjongCoreTestSupport.h"

#if WITH_DEV_AUTOMATION_TESTS

/**
 * 覆盖编辑器 UI 资源绑定、对话框布局、三维桌面展示和手机平板缩放。
 * 保持原自动化测试路径和断言不变，失败由 Unreal Automation Framework 汇总。
 */
#if WITH_EDITOR
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongUISoundAssetsTest, "GuiyangMahjong.UI.SoundAssetsAndButtonBinding", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongUISoundAssetsTest::RunTest(const FString& Parameters)
{
    static const TCHAR* SoundPaths[] = {
        TEXT("/Game/UI/Audio/SFX_UI_Click.SFX_UI_Click"),
        TEXT("/Game/UI/Audio/SFX_Tile_Select.SFX_Tile_Select"),
        TEXT("/Game/UI/Audio/SFX_Tile_Play.SFX_Tile_Play"),
        TEXT("/Game/UI/Audio/SFX_Peng.SFX_Peng"),
        TEXT("/Game/UI/Audio/SFX_Gang.SFX_Gang"),
        TEXT("/Game/UI/Audio/SFX_Hu.SFX_Hu"),
        TEXT("/Game/UI/Audio/SFX_Pass.SFX_Pass"),
        TEXT("/Game/UI/Audio/BGM_FirstLightParticles.BGM_FirstLightParticles")
    };
    for (const TCHAR* SoundPath : SoundPaths)
    {
        TestNotNull(FString::Printf(TEXT("音效必须可被 UE 加载：%s"), SoundPath),
            LoadObject<USoundBase>(nullptr, SoundPath));
    }

    UWidgetBlueprint* ActionPanel = LoadObject<UWidgetBlueprint>(nullptr,
        TEXT("/Game/UI/Components/WBP_ActionButtonPanel.WBP_ActionButtonPanel"));
    TestNotNull(TEXT("操作按钮面板必须存在"), ActionPanel);
    if (ActionPanel && ActionPanel->WidgetTree)
    {
        TArray<UWidget*> Widgets;
        ActionPanel->WidgetTree->GetAllWidgets(Widgets);
        int32 ButtonCount = 0;
        for (UWidget* Widget : Widgets)
        {
            if (const UButton* Button = Cast<UButton>(Widget))
            {
                ++ButtonCount;
                TestNull(FString::Printf(TEXT("操作按钮 %s 不应叠加通用点击声"), *Button->GetName()),
                    Cast<USoundBase>(Button->GetStyle().PressedSlateSound.GetResourceObject()));
            }
        }
        TestEqual(TEXT("碰杠胡过面板必须包含四个按钮"), ButtonCount, 4);
    }

    UWidgetBlueprint* Login = LoadObject<UWidgetBlueprint>(nullptr,
        TEXT("/Game/UI/Screens/WBP_Login.WBP_Login"));
    TestNotNull(TEXT("登录界面必须存在"), Login);
    if (Login && Login->WidgetTree)
    {
        TArray<UWidget*> Widgets;
        Login->WidgetTree->GetAllWidgets(Widgets);
        int32 ButtonCount = 0;
        for (UWidget* Widget : Widgets)
        {
            if (const UButton* Button = Cast<UButton>(Widget))
            {
                ++ButtonCount;
                TestNotNull(FString::Printf(TEXT("通用按钮 %s 必须绑定点击声"), *Button->GetName()),
                    Cast<USoundBase>(Button->GetStyle().PressedSlateSound.GetResourceObject()));
            }
        }
        TestTrue(TEXT("登录界面必须包含通用按钮"), ButtonCount > 0);
    }

    UWidgetBlueprint* HandTile = LoadObject<UWidgetBlueprint>(nullptr,
        TEXT("/Game/UI/Components/WBP_HandTile.WBP_HandTile"));
    TestNotNull(TEXT("手牌组件必须存在"), HandTile);
    if (HandTile && HandTile->WidgetTree)
    {
        const UButton* TileButton = Cast<UButton>(HandTile->WidgetTree->FindWidget(TEXT("Btn_Tile")));
        TestNotNull(TEXT("手牌点击按钮必须存在"), TileButton);
        if (TileButton)
        {
            TestNull(TEXT("手牌按钮不应叠加通用点击声"),
                Cast<USoundBase>(TileButton->GetStyle().PressedSlateSound.GetResourceObject()));
        }
    }
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongSettingsDialogTest, "GuiyangMahjong.UI.SettingsDialogAsset", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongSettingsDialogTest::RunTest(const FString& Parameters)
{
    FMahjongLocalSettings InvalidSettings;
    InvalidSettings.MusicVolume = -1.0f;
    InvalidSettings.SoundVolume = 2.0f;
    InvalidSettings.Sanitize();
    TestEqual(TEXT("音乐音量必须限制为 0"), InvalidSettings.MusicVolume, 0.0f);
    TestEqual(TEXT("音效音量必须限制为 1"), InvalidSettings.SoundVolume, 1.0f);

    UWidgetBlueprint* Settings = LoadObject<UWidgetBlueprint>(nullptr,
        TEXT("/Game/UI/Dialogs/WBP_Settings.WBP_Settings"));
    TestNotNull(TEXT("设置弹窗资产必须存在"), Settings);
    if (!Settings || !Settings->WidgetTree)
    {
        return false;
    }

    TestTrue(TEXT("设置弹窗必须继承 UMobileSettingsWidget"),
        Settings->GeneratedClass && Settings->GeneratedClass->IsChildOf(UMobileSettingsWidget::StaticClass()));
    TestNotNull(TEXT("音乐开关必须存在"), Cast<UCheckBox>(Settings->WidgetTree->FindWidget(TEXT("Chk_MusicEnabled"))));
    TestNotNull(TEXT("音效开关必须存在"), Cast<UCheckBox>(Settings->WidgetTree->FindWidget(TEXT("Chk_SoundEnabled"))));
    TestNotNull(TEXT("震动开关必须存在"), Cast<UCheckBox>(Settings->WidgetTree->FindWidget(TEXT("Chk_VibrationEnabled"))));
    TestNotNull(TEXT("音乐音量滑块必须存在"), Cast<USlider>(Settings->WidgetTree->FindWidget(TEXT("Slider_MusicVolume"))));
    TestNotNull(TEXT("音效音量滑块必须存在"), Cast<USlider>(Settings->WidgetTree->FindWidget(TEXT("Slider_SoundVolume"))));
    TestNotNull(TEXT("重置按钮必须存在"), Cast<UButton>(Settings->WidgetTree->FindWidget(TEXT("Btn_Reset"))));
    TestNotNull(TEXT("退出游戏按钮必须存在"), Cast<UButton>(Settings->WidgetTree->FindWidget(TEXT("Btn_ExitGame"))));
    TestNotNull(TEXT("关闭按钮必须存在"), Cast<UButton>(Settings->WidgetTree->FindWidget(TEXT("Btn_Close"))));

    UWidgetBlueprint* Lobby = LoadObject<UWidgetBlueprint>(nullptr,
        TEXT("/Game/UI/Screens/WBP_Lobby.WBP_Lobby"));
    TestNotNull(TEXT("大厅资产必须存在"), Lobby);
    if (Lobby && Lobby->WidgetTree)
    {
        TestNotNull(TEXT("大厅设置按钮必须存在"),
            Cast<UButton>(Lobby->WidgetTree->FindWidget(TEXT("Btn_Setting"))));
    }
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongCreateRoomDialogLayoutTest, "GuiyangMahjong.UI.CreateRoomDialogLayout", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongCreateRoomDialogLayoutTest::RunTest(const FString& Parameters)
{
    UWidgetBlueprint* CreateRoom = LoadObject<UWidgetBlueprint>(nullptr,
        TEXT("/Game/UI/Dialogs/WBP_CreateRoomDialog.WBP_CreateRoomDialog"));
    TestNotNull(TEXT("创建房间弹窗资源必须存在"), CreateRoom);
    if (!CreateRoom || !CreateRoom->WidgetTree)
    {
        return false;
    }

    const UBorder* Dialog = Cast<UBorder>(CreateRoom->WidgetTree->FindWidget(TEXT("Border_Dialog9Slice")));
    TestNotNull(TEXT("创建房间弹窗背景必须存在"), Dialog);
    const UCanvasPanelSlot* DialogSlot = Dialog ? Cast<UCanvasPanelSlot>(Dialog->Slot) : nullptr;
    TestNotNull(TEXT("创建房间弹窗必须使用画布布局"), DialogSlot);
    if (DialogSlot)
    {
        TestTrue(TEXT("1.5 倍缩放下弹窗宽度必须适配手机横屏"), DialogSlot->GetSize().X <= 1720.0f);
        TestTrue(TEXT("1.5 倍缩放下弹窗高度不得超过 700"), DialogSlot->GetSize().Y <= 700.0f);
    }

    for (const FName WidgetName : {FName(TEXT("RuleConfig")), FName(TEXT("RuleSummary")),
        FName(TEXT("Txt_Status")), FName(TEXT("Btn_Create")), FName(TEXT("Btn_Cancel"))})
    {
        const UWidget* Widget = CreateRoom->WidgetTree->FindWidget(WidgetName);
        TestNotNull(FString::Printf(TEXT("创建房间关键控件必须存在：%s"), *WidgetName.ToString()), Widget);
        const UCanvasPanelSlot* Slot = Widget ? Cast<UCanvasPanelSlot>(Widget->Slot) : nullptr;
        TestNotNull(FString::Printf(TEXT("创建房间关键控件必须使用画布槽：%s"), *WidgetName.ToString()), Slot);
        if (Slot)
        {
            const FVector2D FarEdge = Slot->GetPosition() + Slot->GetSize();
            TestTrue(FString::Printf(TEXT("控件不得超出弹窗右边界：%s"), *WidgetName.ToString()),
                FarEdge.X <= 1720.0f);
            TestTrue(FString::Printf(TEXT("控件不得超出弹窗下边界：%s"), *WidgetName.ToString()),
                FarEdge.Y <= 700.0f);
        }
    }
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongRoomReturnLobbyButtonTest, "GuiyangMahjong.UI.RoomReturnLobbyButton", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongRoomReturnLobbyButtonTest::RunTest(const FString& Parameters)
{
    UWidgetBlueprint* Room = LoadObject<UWidgetBlueprint>(nullptr,
        TEXT("/Game/UI/Screens/WBP_Room.WBP_Room"));
    TestNotNull(TEXT("游戏房间界面必须存在"), Room);
    if (!Room || !Room->WidgetTree)
    {
        return false;
    }

    const UButton* ReturnButton = Cast<UButton>(Room->WidgetTree->FindWidget(TEXT("Btn_ReturnLobby")));
    TestNotNull(TEXT("游戏房间必须提供返回大厅按钮"), ReturnButton);
    const UCanvasPanelSlot* ReturnSlot = ReturnButton ? Cast<UCanvasPanelSlot>(ReturnButton->Slot) : nullptr;
    TestNotNull(TEXT("返回大厅按钮必须使用画布槽"), ReturnSlot);
    if (ReturnSlot)
    {
        const FVector2D FarEdge = ReturnSlot->GetPosition() + ReturnSlot->GetSize();
        TestTrue(TEXT("返回大厅按钮不得超出房间界面右边界"), FarEdge.X <= 1920.0f);
        TestTrue(TEXT("返回大厅按钮必须位于移动端可视高度内"), FarEdge.Y <= 720.0f);
    }
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongThreeDTableLayoutTest, "GuiyangMahjong.UI.ThreeDTableLayout", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongThreeDTableLayoutTest::RunTest(const FString& Parameters)
{
    UWidgetBlueprint* GameHUD = LoadObject<UWidgetBlueprint>(nullptr,
        TEXT("/Game/UI/Screens/WBP_GameHUD.WBP_GameHUD"));
    TestNotNull(TEXT("三维牌桌 HUD 必须存在"), GameHUD);
    if (!GameHUD || !GameHUD->WidgetTree) return false;

    // The serialized legacy UViewport is ignored at runtime. Artists tune the client-only
    // presentation in MahjongRoomVisualPreviewMap; MahjongRoomMap stays target-neutral.
    const UWidget* LegacyBackground = GameHUD->WidgetTree->FindWidget(TEXT("Background_ComponentSlot"));
    TestTrue(TEXT("旧版绿色桌面背景若仍存在于序列化资源中，必须由 HUD 兼容绑定接管"),
        !LegacyBackground || FindFProperty<FObjectPropertyBase>(
            UMobileMahjongHUDWidget::StaticClass(), TEXT("Background_ComponentSlot")) != nullptr);
    TestNotNull(TEXT("等待阶段的准备按钮必须直接位于三维牌桌 HUD"),
        GameHUD->WidgetTree->FindWidget(TEXT("Btn_Ready")));
    TestNotNull(TEXT("三维牌桌 HUD 必须保留返回大厅按钮"),
        GameHUD->WidgetTree->FindWidget(TEXT("Btn_ReturnLobby")));
    TestNotNull(TEXT("准备状态提示必须直接位于三维牌桌 HUD"),
        GameHUD->WidgetTree->FindWidget(TEXT("Txt_ReadyStatus")));
    TestNotNull(TEXT("三维牌桌 Actor 类必须可加载"), AMahjong3DTableActor::StaticClass());
    const AGuiyangMahjongPlayerController* ControllerDefault =
        GetDefault<AGuiyangMahjongPlayerController>();
    TestNotNull(TEXT("房间玩家控制器必须可加载"), ControllerDefault);
    if (ControllerDefault)
    {
        TestFalse(TEXT("房间固定镜头不得被 Pawn 或旁观者自动接管"),
            ControllerDefault->bAutoManageActiveCameraTarget);
    }
    const AMahjongRoomCameraActor* CameraDefault = GetDefault<AMahjongRoomCameraActor>();
    TestNotNull(TEXT("房间电影摄像机预设类必须可加载"), CameraDefault);
    if (CameraDefault && CameraDefault->GetCineCameraComponent())
    {
        TestTrue(TEXT("房间摄像机必须带稳定标签"),
            CameraDefault->ActorHasTag(AMahjongRoomCameraActor::RoomCameraTag));
        TestEqual(TEXT("默认摄像机焦距必须为 30mm"),
            CameraDefault->GetCineCameraComponent()->CurrentFocalLength, 30.0f);
        TestFalse(TEXT("移动横屏摄像机不得强制黑边宽高比"),
            CameraDefault->GetCineCameraComponent()->bConstrainAspectRatio);
        const UCineCameraComponent* CameraComponent = CameraDefault->GetCineCameraComponent();
        const FPostProcessSettings& PostProcess = CameraComponent->PostProcessSettings;
        TestTrue(TEXT("Room camera must override motion blur"), PostProcess.bOverride_MotionBlurAmount);
        TestEqual(TEXT("Room camera motion blur must be disabled"), PostProcess.MotionBlurAmount, 0.0f);
        TestTrue(TEXT("Room camera must override depth of field"), PostProcess.bOverride_DepthOfFieldEnabled);
        TestFalse(TEXT("Room camera depth of field must be disabled"), PostProcess.DepthOfFieldEnabled);
        TestTrue(TEXT("Room camera must override sharpening"), PostProcess.bOverride_Sharpen);
        TestEqual(TEXT("Room camera sharpening must remain readable"), PostProcess.Sharpen, 0.5f);
        TestEqual(TEXT("Room camera focus must not blur Mahjong faces"),
            CameraComponent->FocusSettings.FocusMethod, ECameraFocusMethod::Disable);
    }

    const FRotator UprightRotation =
        AMahjong3DTableActor::ResolveTileMeshRotation(FRotator::ZeroRotator, true, true);
    const FVector UprightFaceNormal = UprightRotation.RotateVector(FVector::YAxisVector);
    TestTrue(TEXT("Local upright Mahjong50 face must point toward the south-side camera"),
        UprightFaceNormal.Y < -0.99f);
    TestTrue(TEXT("Local upright Mahjong50 glyph must remain right-side up"),
        UprightRotation.RotateVector(FVector::ZAxisVector).Z > 0.99f);
    const FRotator CameraFacingSouthRotation =
        AMahjong3DTableActor::ResolveTileMeshRotation(FRotator(0.0f, 0.0f, -30.0f), true, true);
    const FVector CameraFacingSouthNormal =
        CameraFacingSouthRotation.RotateVector(FVector::YAxisVector);
    TestTrue(TEXT("South hand face must tilt upward toward the elevated room camera"),
        CameraFacingSouthNormal.Y < -0.8f && CameraFacingSouthNormal.Z > 0.49f);
    const FRotator FlatFaceUpRotation =
        AMahjong3DTableActor::ResolveTileMeshRotation(FRotator::ZeroRotator, true, false);
    TestTrue(TEXT("Flat face-up Mahjong50 tile must point upward"),
        FlatFaceUpRotation.RotateVector(FVector::YAxisVector).Z > 0.99f);
    const FRotator FlatFaceDownRotation =
        AMahjong3DTableActor::ResolveTileMeshRotation(FRotator::ZeroRotator, false, false);
    TestTrue(TEXT("Flat face-down Mahjong50 tile must point downward"),
        FlatFaceDownRotation.RotateVector(FVector::YAxisVector).Z < -0.99f);

    UWorld* SharedRoomWorld = LoadObject<UWorld>(nullptr,
        TEXT("/Game/Maps/MahjongRoomMap.MahjongRoomMap"));
    TestNotNull(TEXT("共享麻将房间关卡必须可加载"), SharedRoomWorld);
    bool bSharedMapContainsClientPresentation = false;
    if (SharedRoomWorld && SharedRoomWorld->PersistentLevel)
    {
        for (const AActor* Actor : SharedRoomWorld->PersistentLevel->Actors)
        {
            bSharedMapContainsClientPresentation |= IsValid(Actor)
                && (Actor->IsA<AMahjongRoomPresentationActor>()
                    || Actor->IsA<AMahjong3DTableActor>()
                    || Actor->IsA<AMahjongRoomCameraActor>());
        }
    }
    TestFalse(TEXT("共享 MahjongRoomMap 不得序列化客户端展示类"),
        bSharedMapContainsClientPresentation);

    const UMahjongRoomPresentationSettings* PresentationSettings =
        GetDefault<UMahjongRoomPresentationSettings>();
    TestNotNull(TEXT("客户端房间展示设置必须存在"), PresentationSettings);
    UClass* ConfiguredPresentationClass = PresentationSettings
        ? PresentationSettings->PresentationClass.LoadSynchronous() : nullptr;
    TestNotNull(TEXT("客户端房间展示蓝图必须可加载"), ConfiguredPresentationClass);
    UWorld* PreviewWorld = LoadObject<UWorld>(nullptr,
        TEXT("/Game/Maps/MahjongRoomVisualPreviewMap.MahjongRoomVisualPreviewMap"));
    TestNotNull(TEXT("麻将房间视觉预览关卡必须可加载"), PreviewWorld);
    const AMahjongRoomPresentationActor* PresentationInstance = nullptr;
    if (PreviewWorld && PreviewWorld->PersistentLevel && ConfiguredPresentationClass)
    {
        for (const AActor* Actor : PreviewWorld->PersistentLevel->Actors)
        {
            if (IsValid(Actor) && Actor->GetClass() == ConfiguredPresentationClass)
            {
                PresentationInstance = Cast<AMahjongRoomPresentationActor>(Actor);
                break;
            }
        }
    }
    if (ConfiguredPresentationClass)
    {
        TestTrue(TEXT("配置的展示蓝图必须继承客户端 Presentation 基类"),
            ConfiguredPresentationClass->IsChildOf(AMahjongRoomPresentationActor::StaticClass()));
        TestEqual(TEXT("运行时必须配置设计师可编辑的 BP_MahjongRoomPresentation"),
            ConfiguredPresentationClass->GetPathName(),
            FString(TEXT("/Game/Client/Room/Presentation/BP_MahjongRoomPresentation."
                         "BP_MahjongRoomPresentation_C")));

        TestNotNull(TEXT("预览关卡必须包含房间展示蓝图实例"), PresentationInstance);
        if (PresentationInstance)
        {
            const UDirectionalLightComponent* Directional =
                PresentationInstance->FindComponentByClass<UDirectionalLightComponent>();
            TestNotNull(TEXT("运行时展示必须包含跨平台主方向光"), Directional);
            if (Directional)
            {
                TestEqual(TEXT("主方向光必须由展示蓝图拥有"),
                    Directional->CreationMethod, EComponentCreationMethod::SimpleConstructionScript);
                TestTrue(TEXT("主方向光必须处于手动曝光安全范围"),
                    Directional->Intensity > 0.0f && Directional->Intensity <= 50.0f);
                TestTrue(TEXT("夜间主方向光必须保留桌面接触阴影"), Directional->CastShadows);
            }
            const USkyLightComponent* Sky =
                PresentationInstance->FindComponentByClass<USkyLightComponent>();
            TestNotNull(TEXT("运行时展示必须包含环境补光"), Sky);
            if (Sky)
            {
                TestEqual(TEXT("天光必须由展示蓝图拥有"),
                    Sky->CreationMethod, EComponentCreationMethod::SimpleConstructionScript);
                TestFalse(TEXT("封闭麻将房不得启用实时天空捕获"),
                    Sky->IsRealTimeCaptureEnabled());
            }
            TArray<URectLightComponent*> RectLights;
            PresentationInstance->GetComponents<URectLightComponent>(RectLights);
            TestEqual(TEXT("运行时展示必须包含主光、补光、顶光与轮廓光"), RectLights.Num(), 4);
            for (const URectLightComponent* Rect : RectLights)
            {
                TestEqual(TEXT("矩形灯必须由展示蓝图拥有"),
                    Rect->CreationMethod, EComponentCreationMethod::SimpleConstructionScript);
                TestEqual(TEXT("矩形灯必须使用明确的流明单位"),
                    Rect->IntensityUnits, ELightUnits::Lumens);
                TestTrue(TEXT("减半后的矩形灯必须处于安全流明范围"),
                    Rect->Intensity > 0.0f && Rect->Intensity <= 100.0f);
            }
            const UCineCameraComponent* PresentationCamera =
                PresentationInstance->FindComponentByClass<UCineCameraComponent>();
            TestNotNull(TEXT("展示蓝图必须直接拥有可编辑电影摄像机"), PresentationCamera);
            if (PresentationCamera)
            {
                TestEqual(TEXT("电影摄像机必须由展示蓝图拥有"),
                    PresentationCamera->CreationMethod,
                    EComponentCreationMethod::SimpleConstructionScript);
            }
            TArray<UStaticMeshComponent*> StaticMeshes;
            PresentationInstance->GetComponents<UStaticMeshComponent>(StaticMeshes);
            const UStaticMeshComponent* TableMeshComponent = nullptr;
            for (const UStaticMeshComponent* Component : StaticMeshes)
            {
                if (Component && Component->GetStaticMesh())
                {
                    const FString GeometryIdentity =
                        Component->GetName() + TEXT(" ")
                        + Component->GetStaticMesh()->GetPathName();
                    TestFalse(TEXT("房间展示蓝图不得包含球体或天空穹顶几何体"),
                        GeometryIdentity.Contains(TEXT("Sphere"), ESearchCase::IgnoreCase)
                        || GeometryIdentity.Contains(TEXT("Dome"), ESearchCase::IgnoreCase)
                        || GeometryIdentity.Contains(TEXT("Hemisphere"), ESearchCase::IgnoreCase));
                }
                if (Component && Component->GetName() == TEXT("MahjongTableMesh"))
                {
                    TableMeshComponent = Component;
                    break;
                }
            }
            TestNotNull(TEXT("展示蓝图必须直接拥有可编辑麻将桌模型"), TableMeshComponent);
            if (TableMeshComponent)
            {
                TestEqual(TEXT("麻将桌模型必须由展示蓝图拥有"),
                    TableMeshComponent->CreationMethod,
                    EComponentCreationMethod::SimpleConstructionScript);
                TestTrue(TEXT("麻将桌模型组件必须配置静态网格"),
                    TableMeshComponent->GetStaticMesh() != nullptr);
                if (TableMeshComponent->GetStaticMesh())
                {
                    const FVector TableSize =
                        TableMeshComponent->GetStaticMesh()->GetBounds().BoxExtent
                        * 2.0f * TableMeshComponent->GetRelativeScale3D();
                    TestTrue(TEXT("Mahjong tabletop width must be 300 cm"),
                        FMath::IsNearlyEqual(TableSize.X, 300.0f, 0.5f));
                    TestTrue(TEXT("Mahjong tabletop depth must be 300 cm"),
                        FMath::IsNearlyEqual(TableSize.Y, 300.0f, 0.5f));
                }
            }
            TArray<UChildActorComponent*> ChildActors;
            PresentationInstance->GetComponents<UChildActorComponent>(ChildActors);
            TestEqual(TEXT("展示蓝图只保留一个动态麻将牌布局 Child Actor"),
                ChildActors.Num(), 1);
            if (ChildActors.Num() == 1)
            {
                TestEqual(TEXT("麻将牌布局 Child Actor 必须由展示蓝图拥有"),
                    ChildActors[0]->CreationMethod,
                    EComponentCreationMethod::SimpleConstructionScript);
                TestTrue(TEXT("麻将牌布局必须使用运行时 AMahjong3DTableActor"),
                    ChildActors[0]->GetChildActorClass() == AMahjong3DTableActor::StaticClass());
            }
        }
    }

    bool bHasPresentation = false;
    bool bHasIndependentKeyLight = false;
    if (PreviewWorld && PreviewWorld->PersistentLevel)
    {
        for (const AActor* Actor : PreviewWorld->PersistentLevel->Actors)
        {
            if (!IsValid(Actor)) continue;
            bHasPresentation |= Actor->GetClass() == ConfiguredPresentationClass;
            bHasIndependentKeyLight |= Actor->IsA<ADirectionalLight>() || Actor->IsA<ASkyLight>();
        }
    }
    TestTrue(TEXT("视觉预览关卡必须放置与运行时相同的展示蓝图"), bHasPresentation);
    TestFalse(TEXT("关键灯光必须属于展示蓝图，预览关卡不得保留独立实例"), bHasIndependentKeyLight);
    UStaticMesh* TileMesh = LoadObject<UStaticMesh>(nullptr,
        TEXT("/Game/Art/Mahjong/Mahjong50/Tiles/SM_Mahjong50_Characters_1.SM_Mahjong50_Characters_1"));
    TestNotNull(TEXT("Mahjong50 PBR 麻将牌静态网格必须已导入"), TileMesh);
    if (TileMesh)
    {
        const FVector Extent = TileMesh->GetBounds().BoxExtent;
        TestTrue(TEXT("麻将牌宽度必须约为 36mm"), FMath::IsNearlyEqual(Extent.X * 2.0f, 3.6f, 0.15f));
        TestTrue(TEXT("麻将牌厚度必须约为 26mm"), FMath::IsNearlyEqual(Extent.Y * 2.0f, 2.6f, 0.15f));
        TestTrue(TEXT("麻将牌高度必须约为 50mm"), FMath::IsNearlyEqual(Extent.Z * 2.0f, 5.0f, 0.15f));
        TestEqual(TEXT("Mahjong50 麻将牌必须使用牌身与牌面连续的单一材质槽"),
            TileMesh->GetStaticMaterials().Num(), 1);
    }
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FMahjongPhoneTabletScalingTest, "GuiyangMahjong.UI.PhoneTabletScaling", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FMahjongPhoneTabletScalingTest::RunTest(const FString& Parameters)
{
    const UMahjongUIScalingRule* Rule = GetDefault<UMahjongUIScalingRule>();
    const float PhoneScale = Rule->GetDPIScaleBasedOnSize(FIntPoint(2700, 1224));
    const float Tablet16x10Scale = Rule->GetDPIScaleBasedOnSize(FIntPoint(2560, 1600));
    const float Tablet4x3Scale = Rule->GetDPIScaleBasedOnSize(FIntPoint(2048, 1536));
    TestEqual(TEXT("20:9 手机必须使用 1.0 倍 UI"), PhoneScale, 1.0f);
    TestEqual(TEXT("16:10 平板必须使用 1.0 倍 UI"), Tablet16x10Scale, 1.0f);
    TestEqual(TEXT("4:3 平板必须使用 1.0 倍 UI"), Tablet4x3Scale, 1.0f);
    TestEqual(TEXT("手机宽屏前景必须保持比例"),
        UMahjongResponsiveScaleBox::ResolveStretchForViewport(FIntPoint(2700, 1224)), EStretch::ScaleToFit);
    TestEqual(TEXT("16:10 平板前景必须保持比例"),
        UMahjongResponsiveScaleBox::ResolveStretchForViewport(FIntPoint(2560, 1600)), EStretch::ScaleToFit);
    TestEqual(TEXT("4:3 平板前景必须保持比例"),
        UMahjongResponsiveScaleBox::ResolveStretchForViewport(FIntPoint(2048, 1536)), EStretch::ScaleToFit);

    UWidgetBlueprint* RootHUD = LoadObject<UWidgetBlueprint>(nullptr,
        TEXT("/Game/UI/Screens/WBP_RootHUD.WBP_RootHUD"));
    TestNotNull(TEXT("RootHUD 必须存在"), RootHUD);
    if (RootHUD && RootHUD->WidgetTree)
    {
        TestNotNull(TEXT("RootHUD 必须使用手机/平板响应式前景缩放框"),
            Cast<UMahjongResponsiveScaleBox>(RootHUD->WidgetTree->FindWidget(TEXT("Scale_Design1920x1080"))));
    }
    return true;
}
#endif

#endif

