#include "Editor/GenerateMahjongUICommandlet.h"

#if WITH_EDITOR
#include "WidgetBlueprint.h"
#include "WidgetBlueprintFactory.h"
#include "Blueprint/WidgetTree.h"
#include "Components/Border.h"
#include "Components/Button.h"
#include "Components/CanvasPanel.h"
#include "Components/CanvasPanelSlot.h"
#include "Components/CheckBox.h"
#include "Components/CircularThrobber.h"
#include "Components/EditableTextBox.h"
#include "Components/HorizontalBox.h"
#include "Components/Image.h"
#include "Components/Overlay.h"
#include "Components/ProgressBar.h"
#include "Components/SafeZone.h"
#include "Components/ScaleBox.h"
#include "Components/SizeBox.h"
#include "Components/Slider.h"
#include "Components/TextBlock.h"
#include "Components/VerticalBox.h"
#include "Components/Viewport.h"
#include "Components/WrapBox.h"
#include "AssetRegistry/AssetRegistryModule.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/PackageName.h"
#include "UObject/UnrealType.h"
#include "UObject/SavePackage.h"
#include "Engine/Texture2D.h"
#include "Sound/SoundBase.h"
#include "Sound/SlateSound.h"
#include "UI/MobileConnectServerWidget.h"
#include "UI/MobileCreatingRoomWidget.h"
#include "UI/MobileLobbyWidget.h"
#include "UI/MobileRoomWidget.h"
#include "UI/MobileMahjongHUDWidget.h"
#include "UI/MobileHandTileWidget.h"
#include "UI/MobileDiscardTileWidget.h"
#include "UI/MobileActionButtonPanel.h"
#include "UI/MobileSettlementWidget.h"
#include "UI/MobileErrorToastWidget.h"
#include "UI/MobileReconnectOverlayWidget.h"
#include "UI/MobileLoginWidget.h"
#include "UI/MobileRootHUDWidget.h"
#include "UI/MobileConfirmDialogWidget.h"
#include "UI/MobileCreateRoomDialogWidget.h"
#include "UI/MobileJoinRoomDialogWidget.h"
#include "UI/MobileSettingsWidget.h"
#include "UI/MahjongResponsiveScaleBox.h"
#include "UI/MobileRuleConfigWidget.h"
#include "UI/MobileRuleSummaryWidget.h"

namespace MahjongUIBuilder
{
    static const FLinearColor Gold(0.92f, 0.66f, 0.20f, 1.0f);
    static const FLinearColor PanelGreen(0.045f, 0.25f, 0.20f, 0.96f);
    static const FLinearColor WarmWhite(0.98f, 0.94f, 0.80f, 1.0f);

    static void MarkVariable(UWidgetBlueprint* BP, UWidget* Widget)
    {
        Widget->bIsVariable = true;
        BP->OnVariableAdded(Widget->GetFName());
    }

    static UCanvasPanelSlot* Place(UCanvasPanel* Canvas, UWidget* Widget, const FVector2D Position, const FVector2D Size,
        const FAnchors Anchors = FAnchors(0.0f, 0.0f), const FVector2D Alignment = FVector2D::ZeroVector)
    {
        UCanvasPanelSlot* Slot = Canvas->AddChildToCanvas(Widget);
        Slot->SetAnchors(Anchors);
        Slot->SetAlignment(Alignment);
        Slot->SetPosition(Position);
        Slot->SetSize(Size);
        return Slot;
    }

    static UTextBlock* Text(UWidgetBlueprint* BP, const FName Name, const TCHAR* Value, const int32 Size = 28)
    {
        UTextBlock* Widget = BP->WidgetTree->ConstructWidget<UTextBlock>(UTextBlock::StaticClass(), Name);
        Widget->SetText(FText::FromString(Value));
        Widget->SetColorAndOpacity(FSlateColor(WarmWhite));
        FSlateFontInfo Font = Widget->GetFont(); Font.Size = Size; Widget->SetFont(Font);
        MarkVariable(BP, Widget);
        return Widget;
    }

    static FSlateBrush BoxBrush(const FLinearColor Color)
    {
        FSlateBrush Brush;
        Brush.DrawAs = ESlateBrushDrawType::Box;
        Brush.Margin = FMargin(0.25f);
        Brush.TintColor = FSlateColor(Color);
        return Brush;
    }

    static FSlateBrush TextureBrush(const TCHAR* ObjectPath, const float Margin = 0.0f)
    {
        FSlateBrush Brush;
        if (UTexture2D* Texture = LoadObject<UTexture2D>(nullptr, ObjectPath))
        {
            Brush.SetResourceObject(Texture);
            Brush.ImageSize = FVector2D(Texture->GetSizeX(), Texture->GetSizeY());
        }
        Brush.DrawAs = Margin > 0.0f ? ESlateBrushDrawType::Box : ESlateBrushDrawType::Image;
        Brush.Margin = FMargin(Margin);
        return Brush;
    }

    static FString ButtonKind(const FName Name)
    {
        const FString Value = Name.ToString();
        if (Value.Contains(TEXT("Hu"))) return TEXT("Hu");
        if (Value.Contains(TEXT("Gang"))) return TEXT("Gang");
        if (Value.Contains(TEXT("Peng"))) return TEXT("Peng");
        if (Value.Contains(TEXT("Pass"))) return TEXT("Pass");
        if (Value.Contains(TEXT("PlayTile"))) return TEXT("PlayTile");
        if (Value.Contains(TEXT("Leave")) || Value.Contains(TEXT("Exit")) || Value.Contains(TEXT("Back")) || Value.Contains(TEXT("Cancel"))
            || Value.Contains(TEXT("Close"))) return TEXT("NeutralDark");
        if (Value.Contains(TEXT("Connect")) || Value.Contains(TEXT("Quick")) || Value.Contains(TEXT("Ready")) || Value.Contains(TEXT("Confirm"))) return TEXT("PrimaryGold");
        return TEXT("PrimaryGreen");
    }

    static UButton* Button(UWidgetBlueprint* BP, const FName Name, const TCHAR* Label,
        const bool bUseGenericClickSound = true)
    {
        UButton* Widget = BP->WidgetTree->ConstructWidget<UButton>(UButton::StaticClass(), Name);
        FButtonStyle Style;
        const FString Kind = ButtonKind(Name);
        const FString Prefix = FString::Printf(TEXT("/Game/UI/Textures/Buttons/T_Btn_%s_"), *Kind);
        Style.SetNormal(TextureBrush(*FString::Printf(TEXT("%sNormal.T_Btn_%s_Normal"), *Prefix, *Kind), 0.1458f));
        Style.SetHovered(TextureBrush(*FString::Printf(TEXT("%sHovered.T_Btn_%s_Hovered"), *Prefix, *Kind), 0.1458f));
        Style.SetPressed(TextureBrush(*FString::Printf(TEXT("%sPressed.T_Btn_%s_Pressed"), *Prefix, *Kind), 0.1458f));
        Style.SetDisabled(TextureBrush(*FString::Printf(TEXT("%sDisabled.T_Btn_%s_Disabled"), *Prefix, *Kind), 0.1458f));
        if (bUseGenericClickSound)
        {
            if (USoundBase* ClickSoundAsset = LoadObject<USoundBase>(nullptr,
                TEXT("/Game/UI/Audio/SFX_UI_Click.SFX_UI_Click")))
            {
                FSlateSound ClickSound;
                ClickSound.SetResourceObject(ClickSoundAsset);
                Style.SetPressedSound(ClickSound);
            }
        }
        Widget->SetStyle(Style);
        MarkVariable(BP, Widget);
        if (Label && Label[0] != TEXT('\0'))
        {
            UTextBlock* LabelWidget = Text(BP, *FString::Printf(TEXT("%s_Label"), *Name.ToString()), Label, 27);
            LabelWidget->SetJustification(ETextJustify::Center);
            Widget->AddChild(LabelWidget);
        }
        return Widget;
    }

    static void ReplaceButtonContent(UWidgetBlueprint* BP, UButton* Widget, UWidget* NewContent)
    {
        for (int32 ChildIndex = 0; ChildIndex < Widget->GetChildrenCount(); ++ChildIndex)
        {
            if (UWidget* OldContent = Widget->GetChildAt(ChildIndex))
            {
                BP->OnVariableRemoved(OldContent->GetFName());
            }
        }
        Widget->ClearChildren();
        Widget->AddChild(NewContent);
    }

    static UEditableTextBox* Edit(UWidgetBlueprint* BP, const FName Name, const TCHAR* Hint)
    {
        UEditableTextBox* Widget = BP->WidgetTree->ConstructWidget<UEditableTextBox>(UEditableTextBox::StaticClass(), Name);
        Widget->SetHintText(FText::FromString(Hint));
        FEditableTextBoxStyle Style = Widget->WidgetStyle;
        Style.BackgroundImageNormal = TextureBrush(TEXT("/Game/UI/Textures/Controls/T_Input_Normal_9Slice.T_Input_Normal_9Slice"), 0.1094f);
        Style.BackgroundImageHovered = TextureBrush(TEXT("/Game/UI/Textures/Controls/T_Input_Focused_9Slice.T_Input_Focused_9Slice"), 0.1094f);
        Style.BackgroundImageFocused = TextureBrush(TEXT("/Game/UI/Textures/Controls/T_Input_Focused_9Slice.T_Input_Focused_9Slice"), 0.1094f);
        Style.BackgroundImageReadOnly = TextureBrush(TEXT("/Game/UI/Textures/Controls/T_Input_Disabled_9Slice.T_Input_Disabled_9Slice"), 0.1094f);
        Widget->WidgetStyle = Style;
        MarkVariable(BP, Widget);
        return Widget;
    }

    static UCheckBox* Check(UWidgetBlueprint* BP, const FName Name)
    {
        UCheckBox* Widget = BP->WidgetTree->ConstructWidget<UCheckBox>(UCheckBox::StaticClass(), Name);
        MarkVariable(BP, Widget);
        return Widget;
    }

    static USlider* Slider(UWidgetBlueprint* BP, const FName Name, const float Value)
    {
        USlider* Widget = BP->WidgetTree->ConstructWidget<USlider>(USlider::StaticClass(), Name);
        Widget->SetMinValue(0.0f);
        Widget->SetMaxValue(1.0f);
        Widget->SetStepSize(0.05f);
        Widget->SetValue(Value);
        Widget->SetSliderBarColor(FLinearColor(0.72f, 0.52f, 0.16f, 1.0f));
        Widget->SetSliderHandleColor(WarmWhite);
        MarkVariable(BP, Widget);
        return Widget;
    }

    static UBorder* Border(UWidgetBlueprint* BP, const FName Name, const FLinearColor Color)
    {
        UBorder* Widget = BP->WidgetTree->ConstructWidget<UBorder>(UBorder::StaticClass(), Name);
        const FString Value = Name.ToString();
        const TCHAR* Asset = Value.Contains(TEXT("Toast")) ? TEXT("/Game/UI/Textures/Panels/T_Panel_Toast_9Slice.T_Panel_Toast_9Slice")
            : Value.Contains(TEXT("Dialog")) ? TEXT("/Game/UI/Textures/Panels/T_Panel_Dialog_DarkGreen_9Slice.T_Panel_Dialog_DarkGreen_9Slice")
            : Value.Contains(TEXT("Reconnect")) ? TEXT("/Game/UI/Textures/Panels/T_Panel_Notice_9Slice.T_Panel_Notice_9Slice")
            : TEXT("/Game/UI/Textures/Panels/T_Panel_Main_GreenGold_9Slice.T_Panel_Main_GreenGold_9Slice");
        FSlateBrush Brush = TextureBrush(Asset, 0.1563f);
        Brush.TintColor = FSlateColor(Color);
        Widget->SetBrush(Brush);
        Widget->SetPadding(FMargin(24.0f));
        MarkVariable(BP, Widget);
        return Widget;
    }

    static UImage* Image(UWidgetBlueprint* BP, const FName Name, const FLinearColor Color)
    {
        UImage* Widget = BP->WidgetTree->ConstructWidget<UImage>(UImage::StaticClass(), Name);
        if (Name == TEXT("Img_Background"))
        {
            Widget->SetBrush(TextureBrush(TEXT("/Game/UI/Textures/Backgrounds/T_BG_Login_Guiyang.T_BG_Login_Guiyang")));
        }
        else
        {
            Widget->SetColorAndOpacity(Color);
        }
        MarkVariable(BP, Widget);
        return Widget;
    }

    static const TCHAR* ResolveBackgroundPath(const FString& BPName)
    {
        if (BPName == TEXT("WBP_Login"))
        {
            return TEXT("/Game/UI/Textures/Backgrounds/T_BG_Login_Guiyang.T_BG_Login_Guiyang");
        }
        if (BPName == TEXT("WBP_ConnectServer"))
        {
            return TEXT("/Game/UI/Textures/Backgrounds/T_BG_Login_Guiyang.T_BG_Login_Guiyang");
        }
        if (BPName == TEXT("WBP_Lobby"))
        {
            return TEXT("/Game/UI/Textures/Backgrounds/T_BG_Lobby_JiaxiuTower.T_BG_Lobby_JiaxiuTower");
        }
        if (BPName == TEXT("WBP_CreatingRoom"))
        {
            return TEXT("/Game/UI/Textures/Backgrounds/T_BG_CreatingRoom_GuiyangMoon.T_BG_CreatingRoom_GuiyangMoon");
        }
        if (BPName == TEXT("WBP_Room"))
        {
            return TEXT("/Game/UI/Textures/Backgrounds/T_BG_Room_GuiyangNight.T_BG_Room_GuiyangNight");
        }
        if (BPName == TEXT("WBP_CreateRoomDialog"))
        {
            return TEXT("/Game/UI/Textures/Backgrounds/T_BG_Rules_GuiyangCulture.T_BG_Rules_GuiyangCulture");
        }
        if (BPName == TEXT("WBP_Settings"))
        {
            return TEXT("/Game/UI/Textures/Backgrounds/T_BG_Settings_GuiyangPattern.T_BG_Settings_GuiyangPattern");
        }
        if (BPName == TEXT("WBP_Settlement"))
        {
            return TEXT("/Game/UI/Textures/Backgrounds/T_BG_Settlement_GuiyangRiver.T_BG_Settlement_GuiyangRiver");
        }
        // Root HUD, Game HUD, popup-only dialogs and reusable components are transparent.
        return nullptr;
    }

    static UCanvasPanel* Root(UWidgetBlueprint* BP)
    {
        BP->Modify();
        if (BP->WidgetTree)
        {
            // 整棵树重建时所有旧变量都会失效，直接清空 GUID 映射可避免已移除的嵌套控件残留。
            BP->WidgetVariableNameToGuidMap.Reset();
            BP->WidgetTree->Rename(nullptr, GetTransientPackage(), REN_DontCreateRedirectors);
        }
        const FString BPName = BP->GetName();
        FVector2D DesignResolution(1920.0f, 1080.0f);
        if (BPName == TEXT("WBP_HandTile"))
        {
            DesignResolution = FVector2D(92.0f, 128.0f);
        }
        else if (BPName == TEXT("WBP_DiscardTile"))
        {
            DesignResolution = FVector2D(64.0f, 88.0f);
        }
        else if (BPName == TEXT("WBP_ActionButtonPanel"))
        {
            DesignResolution = FVector2D(600.0f, 96.0f);
        }
        else if (BPName == TEXT("WBP_RuleConfig"))
        {
            DesignResolution = FVector2D(1400.0f, 640.0f);
        }
        else if (BPName == TEXT("WBP_RuleSummary"))
        {
            DesignResolution = FVector2D(560.0f, 440.0f);
        }

        BP->WidgetTree = NewObject<UWidgetTree>(BP, TEXT("WidgetTree"), RF_Transactional | RF_ArchetypeObject);
        UOverlay* ViewportRoot = BP->WidgetTree->ConstructWidget<UOverlay>(UOverlay::StaticClass(), TEXT("Overlay_ViewportRoot"));
        UScaleBox* BackgroundScale = BP->WidgetTree->ConstructWidget<UScaleBox>(UScaleBox::StaticClass(), TEXT("Scale_BackgroundFill"));
        USizeBox* BackgroundSize = BP->WidgetTree->ConstructWidget<USizeBox>(USizeBox::StaticClass(), TEXT("Size_BackgroundDesign"));
        BackgroundScale->SetStretch(EStretch::ScaleToFill);
        BackgroundScale->SetStretchDirection(EStretchDirection::Both);
        BackgroundScale->SetClipping(EWidgetClipping::ClipToBounds);
        BackgroundSize->SetWidthOverride(DesignResolution.X);
        BackgroundSize->SetHeightOverride(DesignResolution.Y);

        const TCHAR* BackgroundPath = ResolveBackgroundPath(BPName);
        UWidget* Background = nullptr;
        if (!BackgroundPath)
        {
            // Runtime overlays and reusable components must not create a backing brush. The game
            // HUD needs the real MahjongRoomMap to remain visible, and popup widgets need the
            // underlying screen to remain visible.
            // ConstructWidget registers objects with the WidgetTree even when they are detached.
            // Move these unused compatibility nodes out of the tree so the compiler does not
            // require variable GUIDs for widgets that can never render.
            BackgroundScale->Rename(nullptr, GetTransientPackage(), REN_DontCreateRedirectors);
            BackgroundSize->Rename(nullptr, GetTransientPackage(), REN_DontCreateRedirectors);
            BackgroundScale = nullptr;
            BackgroundSize = nullptr;
        }
        else if (BackgroundPath)
        {
            const FName BackgroundName = BPName == TEXT("WBP_Login") ? TEXT("Img_Background") : TEXT("Background_ComponentSlot");
            UImage* BackgroundImage = BP->WidgetTree->ConstructWidget<UImage>(UImage::StaticClass(), BackgroundName);
            BackgroundImage->SetBrush(TextureBrush(BackgroundPath));
            MarkVariable(BP, BackgroundImage);
            Background = BackgroundImage;
        }
        if (Background)
        {
            BackgroundSize->AddChild(Background);
            BackgroundScale->AddChild(BackgroundSize);
            ViewportRoot->AddChildToOverlay(BackgroundScale);
        }

        USafeZone* Safe = BP->WidgetTree->ConstructWidget<USafeZone>(USafeZone::StaticClass(), TEXT("SafeZone_Root"));
        // 本项目要求移动端前景 UI 覆盖刘海区域，不再由 SafeZone 自动收缩四边。
        Safe->SetSidesToPad(false, false, false, false);
        UMahjongResponsiveScaleBox* Scale = BP->WidgetTree->ConstructWidget<UMahjongResponsiveScaleBox>(
            UMahjongResponsiveScaleBox::StaticClass(), TEXT("Scale_Design1920x1080"));
        USizeBox* DesignSize = BP->WidgetTree->ConstructWidget<USizeBox>(USizeBox::StaticClass(), TEXT("Size_Design1920x1080"));
        UCanvasPanel* Canvas = BP->WidgetTree->ConstructWidget<UCanvasPanel>(UCanvasPanel::StaticClass(), TEXT("Canvas_Root"));
        Safe->AddChild(Scale);
        Scale->AddChild(DesignSize);
        DesignSize->AddChild(Canvas);
        // 前景在手机、平板和桌面始终保持 16:9 设计比例；背景分支独立铺满屏幕。
        Scale->SetStretch(EStretch::ScaleToFit);
        Scale->SetStretchDirection(EStretchDirection::Both);
        DesignSize->SetWidthOverride(DesignResolution.X);
        DesignSize->SetHeightOverride(DesignResolution.Y);
        ViewportRoot->AddChildToOverlay(Safe);

        BP->WidgetTree->RootWidget = ViewportRoot;
        MarkVariable(BP, ViewportRoot);
        // GameHUD deliberately has no background branch. Registering its detached compatibility
        // widgets as variables leaves stale GUIDs after compilation and makes the asset fail QA.
        if (Background)
        {
            MarkVariable(BP, BackgroundScale);
            MarkVariable(BP, BackgroundSize);
        }
        MarkVariable(BP, Safe); MarkVariable(BP, Scale); MarkVariable(BP, DesignSize); MarkVariable(BP, Canvas);
        return Canvas;
    }

    static UWidgetBlueprint* Create(const TCHAR* Folder, const TCHAR* Name, UClass* ParentClass)
    {
        const FString PackageName = FString::Printf(TEXT("/Game/UI/%s/%s"), Folder, Name);
        const FString ObjectPath = FString::Printf(TEXT("%s.%s"), *PackageName, Name);
        if (UWidgetBlueprint* Existing = LoadObject<UWidgetBlueprint>(nullptr, *ObjectPath))
        {
            Existing->ParentClass = ParentClass;
            return Existing;
        }
        UPackage* Package = CreatePackage(*PackageName);
        UWidgetBlueprintFactory* Factory = NewObject<UWidgetBlueprintFactory>();
        Factory->ParentClass = ParentClass;
        UWidgetBlueprint* BP = Cast<UWidgetBlueprint>(Factory->FactoryCreateNew(UWidgetBlueprint::StaticClass(), Package, FName(Name), RF_Public | RF_Standalone, nullptr, GWarn));
        if (BP) FAssetRegistryModule::AssetCreated(BP);
        return BP;
    }

    static bool Save(UWidgetBlueprint* BP)
    {
        const TArray<UWidget*> SourceWidgets = BP->GetAllSourceWidgets();
        UE_LOG(LogTemp, Display, TEXT("编译前控件树检查：%s，控件数=%d"), *BP->GetPathName(), SourceWidgets.Num());

        // 在编译前验证 C++ BindWidget 契约，避免只生成“看得见但不可用”的蓝图。
        bool bBindingsValid = true;
        for (TFieldIterator<FObjectPropertyBase> PropertyIt(BP->ParentClass, EFieldIteratorFlags::IncludeSuper); PropertyIt; ++PropertyIt)
        {
            const FObjectPropertyBase* Property = *PropertyIt;
            if (!Property->HasMetaData(TEXT("BindWidget")))
            {
                continue;
            }

            UWidget* BoundWidget = BP->WidgetTree->FindWidget(Property->GetFName());
            UClass* RequiredClass = Property->PropertyClass;
            if (!BoundWidget || !BoundWidget->IsA(RequiredClass))
            {
                UE_LOG(LogTemp, Error, TEXT("控件绑定验证失败：%s.%s，期望类型=%s，实际=%s"),
                    *BP->GetName(), *Property->GetName(), *GetNameSafe(RequiredClass), *GetNameSafe(BoundWidget));
                bBindingsValid = false;
            }
        }
        if (!bBindingsValid)
        {
            return false;
        }

        FKismetEditorUtilities::CompileBlueprint(BP, EBlueprintCompileOptions::SkipGarbageCollection);
        if (BP->Status == BS_Error) return false;
        UPackage* Package = BP->GetOutermost();
        Package->MarkPackageDirty();
        const FString Filename = FPackageName::LongPackageNameToFilename(Package->GetName(), FPackageName::GetAssetPackageExtension());
        IFileManager::Get().MakeDirectory(*FPaths::GetPath(Filename), true);
        FSavePackageArgs Args; Args.TopLevelFlags = RF_Public | RF_Standalone; Args.SaveFlags = SAVE_NoError;
        return UPackage::SavePackage(Package, BP, *Filename, Args);
    }

    static UHorizontalBox* Horizontal(UWidgetBlueprint* BP, const FName Name)
    {
        UHorizontalBox* W = BP->WidgetTree->ConstructWidget<UHorizontalBox>(UHorizontalBox::StaticClass(), Name); MarkVariable(BP, W); return W;
    }
    static UVerticalBox* Vertical(UWidgetBlueprint* BP, const FName Name)
    {
        UVerticalBox* W = BP->WidgetTree->ConstructWidget<UVerticalBox>(UVerticalBox::StaticClass(), Name); MarkVariable(BP, W); return W;
    }
    static UWrapBox* Wrap(UWidgetBlueprint* BP, const FName Name)
    {
        UWrapBox* W = BP->WidgetTree->ConstructWidget<UWrapBox>(UWrapBox::StaticClass(), Name); MarkVariable(BP, W); return W;
    }
}

UGenerateMahjongUICommandlet::UGenerateMahjongUICommandlet()
{
    IsClient = false; IsEditor = true; IsServer = false; LogToConsole = true;
}

int32 UGenerateMahjongUICommandlet::Main(const FString& Params)
{
    using namespace MahjongUIBuilder;
    int32 Created = 0;
    auto Finish = [&Created](UWidgetBlueprint* BP)
    {
        if (BP && Save(BP)) { ++Created; UE_LOG(LogTemp, Display, TEXT("UMG资产已生成：%s"), *BP->GetPathName()); return true; }
        UE_LOG(LogTemp, Error, TEXT("UMG资产生成失败")); return false;
    };

    auto GenerateCreatingRoom = [&Finish]()
    {
        UWidgetBlueprint* Loading = Create(
            TEXT("Screens"), TEXT("WBP_CreatingRoom"), UMobileCreatingRoomWidget::StaticClass());
        UCanvasPanel* C = Root(Loading);

        UTextBlock* Title = Text(Loading, TEXT("Txt_CreatingTitle"), TEXT("正在创建房间"), 48);
        Title->SetJustification(ETextJustify::Center);
        Place(C, Title, {0, 548}, {920, 76}, FAnchors(0.5f, 0), {0.5f, 0});

        UTextBlock* Status = Text(Loading, TEXT("Txt_CreatingStatus"), TEXT("正在创建房间……"), 28);
        Status->SetJustification(ETextJustify::Center);
        Place(C, Status, {0, 635}, {920, 52}, FAnchors(0.5f, 0), {0.5f, 0});

        UBorder* Track = Border(Loading, TEXT("Border_CreatingProgress"), FLinearColor(0.015f, 0.08f, 0.07f, 0.92f));
        Track->SetPadding(FMargin(8.0f));
        Place(C, Track, {0, 706}, {860, 50}, FAnchors(0.5f, 0), {0.5f, 0});
        UProgressBar* Progress = Loading->WidgetTree->ConstructWidget<UProgressBar>(
            UProgressBar::StaticClass(), TEXT("Progress_CreatingRoom"));
        Progress->SetPercent(0.08f);
        Progress->SetFillColorAndOpacity(Gold);
        MarkVariable(Loading, Progress);
        Track->AddChild(Progress);

        UTextBlock* Percent = Text(Loading, TEXT("Txt_CreatingPercent"), TEXT("8%"), 22);
        Percent->SetJustification(ETextJustify::Center);
        Place(C, Percent, {0, 772}, {300, 42}, FAnchors(0.5f, 0), {0.5f, 0});

        UTextBlock* Hint = Text(Loading, TEXT("Txt_CreatingHint"),
            TEXT("正在准备专属牌桌，请勿重复操作"), 20);
        Hint->SetJustification(ETextJustify::Center);
        Hint->SetColorAndOpacity(FSlateColor(FLinearColor(0.82f, 0.88f, 0.82f, 0.92f)));
        Place(C, Hint, {0, 834}, {900, 42}, FAnchors(0.5f, 0), {0.5f, 0});
        return Finish(Loading);
    };

    if (Params.Contains(TEXT("OnlyCreatingRoom"), ESearchCase::IgnoreCase))
    {
        return GenerateCreatingRoom() ? 0 : 1;
    }

    GenerateCreatingRoom();

    // 手牌和弃牌组件先生成，供主 HUD 动态创建。
    UWidgetBlueprint* Hand = Create(TEXT("Components"), TEXT("WBP_HandTile"), UMobileHandTileWidget::StaticClass());
    { UCanvasPanel* C = Root(Hand); UButton* B = Button(Hand, TEXT("Btn_Tile"), TEXT(""), false); Place(C, B, {0,0}, {92,128}); UTextBlock* T = Text(Hand, TEXT("Txt_TileName"), TEXT("一万"), 30); ReplaceButtonContent(Hand, B, T); }
    Finish(Hand);

    UWidgetBlueprint* Discard = Create(TEXT("Components"), TEXT("WBP_DiscardTile"), UMobileDiscardTileWidget::StaticClass());
    {
        UCanvasPanel* C = Root(Discard);
        UBorder* B = Border(Discard, TEXT("Border_Tile"), WarmWhite);
        FSlateBrush TileBrush = TextureBrush(TEXT("/Game/Art/Mahjong/Mahjong50/Textures/T_Mahjong50_FaceAtlas_BaseColor.T_Mahjong50_FaceAtlas_BaseColor"));
        TileBrush.SetUVRegion(FBox2f(
            FVector2f(160.0f / 8192.0f, 3.0f * 1024.0f / 4096.0f),
            FVector2f((160.0f + 704.0f) / 8192.0f, 1.0f)));
        TileBrush.TintColor = FSlateColor(WarmWhite);
        B->SetBrush(TileBrush);
        B->SetPadding(FMargin(4.0f));
        B->SetHorizontalAlignment(HAlign_Fill);
        B->SetVerticalAlignment(VAlign_Center);
        Place(C, B, {0,0}, {64,88});
        UTextBlock* T = Text(Discard, TEXT("Txt_TileName"), TEXT("一万"), 22);
        T->SetColorAndOpacity(FSlateColor(FLinearColor::Black));
        T->SetJustification(ETextJustify::Center);
        T->SetVisibility(ESlateVisibility::Collapsed);
        B->AddChild(T);
    }
    Finish(Discard);

    UWidgetBlueprint* Action = Create(TEXT("Components"), TEXT("WBP_ActionButtonPanel"), UMobileActionButtonPanel::StaticClass());
    { UCanvasPanel* C = Root(Action); UHorizontalBox* H = Horizontal(Action, TEXT("Panel_Actions")); Place(C,H,{0,0},{600,96}); H->AddChildToHorizontalBox(Button(Action,TEXT("Btn_Hu"),TEXT("胡"),false)); H->AddChildToHorizontalBox(Button(Action,TEXT("Btn_Gang"),TEXT("杠"),false)); H->AddChildToHorizontalBox(Button(Action,TEXT("Btn_Peng"),TEXT("碰"),false)); H->AddChildToHorizontalBox(Button(Action,TEXT("Btn_Pass"),TEXT("过"),false)); }
    Finish(Action);

    UWidgetBlueprint* RuleConfig = Create(TEXT("Components"), TEXT("WBP_RuleConfig"), UMobileRuleConfigWidget::StaticClass());
    { UCanvasPanel* C=Root(RuleConfig); Place(C,Text(RuleConfig,TEXT("Txt_RuleConfigTitle"),TEXT("玩法规则配置"),38),{50,30},{700,60}); Place(C,Check(RuleConfig,TEXT("Chk_Standard136")),{60,120},{44,44}); Place(C,Text(RuleConfig,TEXT("Lbl_Standard136"),TEXT("使用 136 张标准牌"),24),{115,120},{330,44}); Place(C,Check(RuleConfig,TEXT("Chk_ChongFengJi")),{60,190},{44,44}); Place(C,Text(RuleConfig,TEXT("Lbl_ChongFengJi"),TEXT("冲锋鸡"),24),{115,190},{240,44}); Place(C,Check(RuleConfig,TEXT("Chk_ZeRenJi")),{60,260},{44,44}); Place(C,Text(RuleConfig,TEXT("Lbl_ZeRenJi"),TEXT("责任鸡"),24),{115,260},{240,44}); Place(C,Check(RuleConfig,TEXT("Chk_WuGuJi")),{60,330},{44,44}); Place(C,Text(RuleConfig,TEXT("Lbl_WuGuJi"),TEXT("乌骨鸡"),24),{115,330},{240,44}); Place(C,Check(RuleConfig,TEXT("Chk_QiangGangHu")),{470,120},{44,44}); Place(C,Text(RuleConfig,TEXT("Lbl_QiangGangHu"),TEXT("抢杠胡"),24),{525,120},{240,44}); Place(C,Check(RuleConfig,TEXT("Chk_YiPaoDuoXiang")),{470,190},{44,44}); Place(C,Text(RuleConfig,TEXT("Lbl_YiPaoDuoXiang"),TEXT("一炮多响"),24),{525,190},{260,44}); Place(C,Check(RuleConfig,TEXT("Chk_QiDui")),{470,260},{44,44}); Place(C,Text(RuleConfig,TEXT("Lbl_QiDui"),TEXT("七对"),24),{525,260},{240,44}); Place(C,Check(RuleConfig,TEXT("Chk_TimeoutAutoPlay")),{470,330},{44,44}); Place(C,Text(RuleConfig,TEXT("Lbl_TimeoutAutoPlay"),TEXT("超时自动托管"),24),{525,330},{300,44}); Place(C,Text(RuleConfig,TEXT("Lbl_BaseScore"),TEXT("底分"),22),{900,110},{150,40}); Place(C,Edit(RuleConfig,TEXT("Txt_BaseScore"),TEXT("1-100")),{1080,100},{260,56}); Place(C,Text(RuleConfig,TEXT("Lbl_JiScore"),TEXT("鸡分"),22),{900,180},{150,40}); Place(C,Edit(RuleConfig,TEXT("Txt_JiScore"),TEXT("0-100")),{1080,170},{260,56}); Place(C,Text(RuleConfig,TEXT("Lbl_GangScore"),TEXT("豆分"),22),{900,250},{150,40}); Place(C,Edit(RuleConfig,TEXT("Txt_GangScore"),TEXT("0-100")),{1080,240},{260,56}); Place(C,Text(RuleConfig,TEXT("Lbl_ZiMoMultiplier"),TEXT("自摸倍数"),22),{900,320},{170,40}); Place(C,Edit(RuleConfig,TEXT("Txt_ZiMoMultiplier"),TEXT("1-16")),{1080,310},{260,56}); Place(C,Text(RuleConfig,TEXT("Lbl_TurnTimeout"),TEXT("出牌超时"),22),{900,410},{170,40}); Place(C,Edit(RuleConfig,TEXT("Txt_TurnTimeout"),TEXT("3-120 秒")),{1080,400},{260,56}); Place(C,Text(RuleConfig,TEXT("Lbl_ReactionTimeout"),TEXT("操作超时"),22),{900,480},{170,40}); Place(C,Edit(RuleConfig,TEXT("Txt_ReactionTimeout"),TEXT("3-60 秒")),{1080,470},{260,56}); Place(C,Text(RuleConfig,TEXT("Lbl_ReconnectTimeout"),TEXT("重连时限"),22),{900,550},{170,40}); Place(C,Edit(RuleConfig,TEXT("Txt_ReconnectTimeout"),TEXT("15-600 秒")),{1080,540},{260,56}); }
    Finish(RuleConfig);

    UWidgetBlueprint* RuleSummary = Create(TEXT("Components"), TEXT("WBP_RuleSummary"), UMobileRuleSummaryWidget::StaticClass());
    { UCanvasPanel* C=Root(RuleSummary); UBorder* B=Border(RuleSummary,TEXT("Panel_RuleSummary9Slice"),PanelGreen); Place(C,B,{0,0},{560,440}); UVerticalBox* V=Vertical(RuleSummary,TEXT("Panel_RuleSummaryContent")); B->AddChild(V); V->AddChildToVerticalBox(Text(RuleSummary,TEXT("Txt_RuleTitle"),TEXT("GuiyangMainstreamV1 · 版本 1"),28)); V->AddChildToVerticalBox(Text(RuleSummary,TEXT("Txt_RuleLines"),TEXT("108 张万筒条 · 4 局 · 公开房\n冲锋鸡开 · 责任鸡开 · 乌骨鸡开\n抢杠胡开 · 一炮多响开 · 七对开\n底分 1 · 鸡分 1 · 豆分 1 · 自摸 ×2\n出牌 15 秒 · 操作 8 秒 · 重连 120 秒"),20)); V->AddChildToVerticalBox(Text(RuleSummary,TEXT("Txt_RuleHash"),TEXT("规则哈希：--"),16)); }
    Finish(RuleSummary);

    UWidgetBlueprint* Toast = Create(TEXT("Components"), TEXT("WBP_ErrorToast"), UMobileErrorToastWidget::StaticClass());
    { UCanvasPanel* C=Root(Toast); UBorder* B=Border(Toast,TEXT("Border_Toast"),FLinearColor(0.55f,0.08f,0.06f,0.96f)); Place(C,B,{0,70},{800,84},FAnchors(0.5f,0),FVector2D(0.5f,0)); B->AddChild(Text(Toast,TEXT("Txt_Message"),TEXT("操作失败，请重试"),28)); }
    Finish(Toast);

    UWidgetBlueprint* Login = Create(TEXT("Screens"), TEXT("WBP_Login"), UMobileLoginWidget::StaticClass());
    {
        // 超宽屏 Fill 会压缩可用逻辑高度；登录操作区控制在前 500 像素内，
        // 同时也让 4:3 / 16:10 平板保留更舒适的上下留白。
        UCanvasPanel* C = Root(Login);
        UImage* Logo = Image(Login, TEXT("Img_GameLogo"), Gold);
        Logo->SetBrush(TextureBrush(TEXT("/Game/UI/Textures/Icons/Icon_Chicken.Icon_Chicken")));
        Place(C, Logo, {-280, 32}, {112, 112}, FAnchors(0.5f, 0));
        Place(C, Text(Login, TEXT("Txt_GameTitle"), TEXT("贵阳捉鸡麻将"), 44), {-145, 44}, {560, 64}, FAnchors(0.5f, 0));
        Place(C, Text(Login, TEXT("Txt_GameSubtitle"), TEXT("黔韵牌局 · 地道玩法"), 20), {-140, 108}, {480, 36}, FAnchors(0.5f, 0));
        Place(C, Text(Login, TEXT("Txt_LoginStatus"), TEXT("请选择登录方式"), 26), {0, 178}, {660, 46}, FAnchors(0.5f, 0), {0.5f, 0});

        UCircularThrobber* Loading = Login->WidgetTree->ConstructWidget<UCircularThrobber>(UCircularThrobber::StaticClass(), TEXT("Loading_Login"));
        MarkVariable(Login, Loading);
        Loading->SetVisibility(ESlateVisibility::Collapsed);
        Place(C, Loading, {0, 228}, {54, 54}, FAnchors(0.5f, 0), {0.5f, 0});
        Place(C, Button(Login, TEXT("Btn_WechatLogin"), TEXT("微信登录（PC 模拟授权）")), {-230, 272}, {460, 68}, FAnchors(0.5f, 0));
        Place(C, Button(Login, TEXT("Btn_GuestLogin"), TEXT("游客登录")), {-230, 356}, {460, 68}, FAnchors(0.5f, 0));

        UCheckBox* Terms = Login->WidgetTree->ConstructWidget<UCheckBox>(UCheckBox::StaticClass(), TEXT("Chk_AgreeTerms"));
        MarkVariable(Login, Terms);
        Place(C, Terms, {-330, 452}, {40, 40}, FAnchors(0.5f, 0));
        Place(C, Text(Login, TEXT("Txt_AgreeHint"), TEXT("我已阅读并同意"), 22), {-278, 454}, {205, 36}, FAnchors(0.5f, 0));
        Place(C, Button(Login, TEXT("Btn_UserAgreement"), TEXT("用户协议")), {-52, 444}, {168, 52}, FAnchors(0.5f, 0));
        Place(C, Button(Login, TEXT("Btn_PrivacyPolicy"), TEXT("隐私政策")), {136, 444}, {168, 52}, FAnchors(0.5f, 0));
        Place(C, Text(Login, TEXT("Txt_Version"), TEXT("版本 0.2.0 · UE 5.8"), 22), {30, -54}, {360, 36}, FAnchors(0, 1));
    }
    Finish(Login);

    UWidgetBlueprint* Confirm = Create(TEXT("Dialogs"), TEXT("WBP_ConfirmDialog"), UMobileConfirmDialogWidget::StaticClass());
    { UCanvasPanel* C=Root(Confirm); UBorder* Mask=Border(Confirm,TEXT("Border_Mask"),FLinearColor(0,0,0,0.65f)); UCanvasPanelSlot* MaskSlot=Place(C,Mask,{0,0},{0,0},FAnchors(0,0,1,1)); MaskSlot->SetOffsets(FMargin(0)); UBorder* Dialog=Border(Confirm,TEXT("Border_Dialog9Slice"),PanelGreen); Place(C,Dialog,{0,0},{760,420},FAnchors(0.5f,0.5f),{0.5f,0.5f}); UVerticalBox* V=Vertical(Confirm,TEXT("Panel_ConfirmContent")); Dialog->AddChild(V); V->AddChildToVerticalBox(Text(Confirm,TEXT("Txt_Title"),TEXT("请确认"),42)); V->AddChildToVerticalBox(Text(Confirm,TEXT("Txt_Message"),TEXT("是否继续当前操作？"),28)); UHorizontalBox* Buttons=Horizontal(Confirm,TEXT("Panel_ConfirmButtons")); Buttons->AddChildToHorizontalBox(Button(Confirm,TEXT("Btn_Confirm"),TEXT("确认"))); Buttons->AddChildToHorizontalBox(Button(Confirm,TEXT("Btn_Cancel"),TEXT("取消"))); V->AddChildToVerticalBox(Buttons); }
    Finish(Confirm);

    UWidgetBlueprint* CreateRoom = Create(TEXT("Dialogs"), TEXT("WBP_CreateRoomDialog"), UMobileCreateRoomDialogWidget::StaticClass());
    {
        UCanvasPanel* C = Root(CreateRoom);
        UBorder* Mask = Border(CreateRoom, TEXT("Border_Mask"), FLinearColor(0, 0, 0, 0.65f));
        UCanvasPanelSlot* MaskSlot = Place(C, Mask, {0, 0}, {0, 0}, FAnchors(0, 0, 1, 1));
        MaskSlot->SetOffsets(FMargin(0));

        // 700 高度可在手机横屏和平板界面内完整显示，并保留上下边距。
        UBorder* Dialog = Border(CreateRoom, TEXT("Border_Dialog9Slice"), PanelGreen);
        Place(C, Dialog, {0, 0}, {1720, 700}, FAnchors(0.5f, 0.5f), {0.5f, 0.5f});
        UCanvasPanel* D = CreateRoom->WidgetTree->ConstructWidget<UCanvasPanel>(
            UCanvasPanel::StaticClass(), TEXT("Panel_CreateRoomContent"));
        MarkVariable(CreateRoom, D);
        Dialog->AddChild(D);

        Place(D, Text(CreateRoom, TEXT("Txt_Title"), TEXT("创建房间与规则确认"), 38), {40, 15}, {700, 50});
        Place(D, Text(CreateRoom, TEXT("Txt_RoundCountLabel"), TEXT("局数"), 22), {40, 75}, {90, 44});
        Place(D, Edit(CreateRoom, TEXT("Txt_RoundCount"), TEXT("1-16 局")), {125, 68}, {180, 52});
        Place(D, Check(CreateRoom, TEXT("Chk_PublicRoom")), {350, 72}, {44, 44});
        Place(D, Text(CreateRoom, TEXT("Txt_PublicRoomLabel"), TEXT("允许快速匹配"), 22), {405, 72}, {240, 44});
        Place(D, Check(CreateRoom, TEXT("Chk_EnablePassword")), {665, 72}, {44, 44});
        Place(D, Text(CreateRoom, TEXT("Txt_EnablePasswordLabel"), TEXT("启用密码"), 22), {720, 72}, {170, 44});
        UEditableTextBox* Password = Edit(CreateRoom, TEXT("Txt_Password"), TEXT("6-12 位可选密码"));
        Password->SetIsPassword(true);
        Place(D, Password, {900, 68}, {360, 52});

        UClass* RuleConfigClass = RuleConfig->GeneratedClass;
        UWidget* RuleConfigWidget = CreateRoom->WidgetTree->ConstructWidget<UWidget>(
            RuleConfigClass, TEXT("RuleConfig"));
        MarkVariable(CreateRoom, RuleConfigWidget);
        Place(D, RuleConfigWidget, {30, 135}, {1080, 430});

        UClass* RuleSummaryClass = RuleSummary->GeneratedClass;
        UWidget* RuleSummaryWidget = CreateRoom->WidgetTree->ConstructWidget<UWidget>(
            RuleSummaryClass, TEXT("RuleSummary"));
        MarkVariable(CreateRoom, RuleSummaryWidget);
        Place(D, RuleSummaryWidget, {1130, 135}, {540, 430});

        Place(D, Text(CreateRoom, TEXT("Txt_Status"), TEXT("请确认规则摘要后创建房间"), 22),
            {45, 595}, {850, 44});
        Place(D, Button(CreateRoom, TEXT("Btn_Create"), TEXT("创建房间")), {1110, 585}, {260, 68});
        Place(D, Button(CreateRoom, TEXT("Btn_Cancel"), TEXT("取消")), {1400, 585}, {220, 68});
    }
    Finish(CreateRoom);

    UWidgetBlueprint* JoinRoom = Create(TEXT("Dialogs"), TEXT("WBP_JoinRoomDialog"), UMobileJoinRoomDialogWidget::StaticClass());
    { UCanvasPanel* C=Root(JoinRoom); UBorder* Mask=Border(JoinRoom,TEXT("Border_Mask"),FLinearColor(0,0,0,0.65f)); UCanvasPanelSlot* MaskSlot=Place(C,Mask,{0,0},{0,0},FAnchors(0,0,1,1)); MaskSlot->SetOffsets(FMargin(0)); UBorder* Dialog=Border(JoinRoom,TEXT("Border_Dialog9Slice"),PanelGreen); Place(C,Dialog,{0,0},{760,520},FAnchors(0.5f,0.5f),{0.5f,0.5f}); UCanvasPanel* D=JoinRoom->WidgetTree->ConstructWidget<UCanvasPanel>(UCanvasPanel::StaticClass(),TEXT("Panel_JoinRoomContent")); MarkVariable(JoinRoom,D); Dialog->AddChild(D); Place(D,Text(JoinRoom,TEXT("Txt_Title"),TEXT("加入房间"),42),{40,25},{680,64}); Place(D,Edit(JoinRoom,TEXT("Txt_RoomCode"),TEXT("请输入 6 位房间号")),{40,110},{680,64}); UEditableTextBox* Password=Edit(JoinRoom,TEXT("Txt_Password"),TEXT("密码房请输入密码")); Password->SetIsPassword(true); Place(D,Password,{40,195},{680,64}); Place(D,Text(JoinRoom,TEXT("Txt_Status"),TEXT("请输入 6 位房间号"),22),{40,280},{680,48}); Place(D,Button(JoinRoom,TEXT("Btn_Join"),TEXT("加入房间")),{130,370},{230,72}); Place(D,Button(JoinRoom,TEXT("Btn_Cancel"),TEXT("取消")),{400,370},{230,72}); }
    Finish(JoinRoom);

    UWidgetBlueprint* Settings = Create(TEXT("Dialogs"), TEXT("WBP_Settings"), UMobileSettingsWidget::StaticClass());
    {
        UCanvasPanel* C = Root(Settings);
        UBorder* Mask = Border(Settings, TEXT("Border_Mask"), FLinearColor(0,0,0,0.68f));
        UCanvasPanelSlot* MaskSlot = Place(C, Mask, {0,0}, {0,0}, FAnchors(0,0,1,1));
        MaskSlot->SetOffsets(FMargin(0));
        UBorder* Dialog = Border(Settings, TEXT("Border_Dialog9Slice"), PanelGreen);
        Place(C, Dialog, {0,0}, {920,700}, FAnchors(0.5f,0.5f), {0.5f,0.5f});
        UCanvasPanel* D = Settings->WidgetTree->ConstructWidget<UCanvasPanel>(UCanvasPanel::StaticClass(), TEXT("Panel_SettingsContent"));
        MarkVariable(Settings, D);
        Dialog->AddChild(D);
        Place(D, Text(Settings, TEXT("Txt_Title"), TEXT("本地设置"), 42), {40,20}, {500,64});
        Place(D, Text(Settings, TEXT("Txt_Subtitle"), TEXT("设置仅保存在当前设备，可随时调整"), 20), {40,78}, {720,42});

        Place(D, Check(Settings, TEXT("Chk_MusicEnabled")), {48,145}, {44,44});
        Place(D, Text(Settings, TEXT("Lbl_MusicEnabled"), TEXT("背景音乐"), 26), {110,146}, {220,44});
        Place(D, Slider(Settings, TEXT("Slider_MusicVolume"), 0.70f), {330,148}, {430,40});
        Place(D, Text(Settings, TEXT("Txt_MusicVolumeValue"), TEXT("70"), 24), {785,145}, {80,44});

        Place(D, Check(Settings, TEXT("Chk_SoundEnabled")), {48,235}, {44,44});
        Place(D, Text(Settings, TEXT("Lbl_SoundEnabled"), TEXT("操作音效"), 26), {110,236}, {220,44});
        Place(D, Slider(Settings, TEXT("Slider_SoundVolume"), 0.85f), {330,238}, {430,40});
        Place(D, Text(Settings, TEXT("Txt_SoundVolumeValue"), TEXT("85"), 24), {785,235}, {80,44});

        Place(D, Check(Settings, TEXT("Chk_VibrationEnabled")), {48,325}, {44,44});
        Place(D, Text(Settings, TEXT("Lbl_VibrationEnabled"), TEXT("触感反馈"), 26), {110,326}, {300,44});
        Place(D, Text(Settings, TEXT("Txt_VibrationHint"), TEXT("支持设备上用于出牌与操作确认"), 20), {330,329}, {520,40});

        UBorder* HintPanel = Border(Settings, TEXT("Panel_SettingsNotice"), FLinearColor(0.03f,0.14f,0.12f,0.90f));
        HintPanel->AddChild(Text(Settings, TEXT("Txt_SettingsNotice"), TEXT("提示：关闭操作音效后，按钮点击、选牌、出牌、碰杠胡过均保持静音。"), 20));
        Place(D, HintPanel, {48,410}, {817,90});
        Place(D, Button(Settings, TEXT("Btn_Reset"), TEXT("恢复默认")), {70,550}, {220,76});
        Place(D, Button(Settings, TEXT("Btn_ExitGame"), TEXT("退出游戏")), {350,550}, {220,76});
        Place(D, Button(Settings, TEXT("Btn_Close"), TEXT("完成")), {630,550}, {220,76});
    }
    Finish(Settings);

    UWidgetBlueprint* RootHUD = Create(TEXT("Screens"), TEXT("WBP_RootHUD"), UMobileRootHUDWidget::StaticClass());
    { UCanvasPanel* C=Root(RootHUD); UOverlay* Screen=RootHUD->WidgetTree->ConstructWidget<UOverlay>(UOverlay::StaticClass(),TEXT("ScreenLayer")); MarkVariable(RootHUD,Screen); UCanvasPanelSlot* ScreenSlot=Place(C,Screen,{0,0},{0,0},FAnchors(0,0,1,1)); ScreenSlot->SetOffsets(FMargin(0)); UOverlay* Popup=RootHUD->WidgetTree->ConstructWidget<UOverlay>(UOverlay::StaticClass(),TEXT("PopupLayer")); MarkVariable(RootHUD,Popup); UCanvasPanelSlot* PopupSlot=Place(C,Popup,{0,0},{0,0},FAnchors(0,0,1,1)); PopupSlot->SetOffsets(FMargin(0)); PopupSlot->SetZOrder(100); }
    Finish(RootHUD);

    UWidgetBlueprint* Connect = Create(TEXT("Screens"), TEXT("WBP_ConnectServer"), UMobileConnectServerWidget::StaticClass());
    {
        UCanvasPanel* C = Root(Connect);
        UBorder* FormPanel = Border(Connect, TEXT("Panel_ConnectForm"), FLinearColor(0.02f, 0.14f, 0.12f, 0.92f));
        Place(C, FormPanel, {0, 210}, {620, 590}, FAnchors(0.5f, 0), {0.5f, 0})->SetZOrder(-1);
        Place(C, Text(Connect, TEXT("Title"), TEXT("连接游戏服务"), 48),
            {0, 245}, {520, 72}, FAnchors(0.5f, 0), {0.5f, 0});
        Place(C, Edit(Connect, TEXT("Txt_ServerIP"), TEXT("服务器 IP，例如 127.0.0.1")),
            {-230, 340}, {460, 64}, FAnchors(0.5f, 0));
        Place(C, Edit(Connect, TEXT("Txt_ServerPort"), TEXT("端口 7777")),
            {-230, 425}, {460, 64}, FAnchors(0.5f, 0));
        Place(C, Edit(Connect, TEXT("Txt_PlayerName"), TEXT("请输入中文昵称")),
            {-230, 510}, {460, 64}, FAnchors(0.5f, 0));
        UCheckBox* Remember = Connect->WidgetTree->ConstructWidget<UCheckBox>(
            UCheckBox::StaticClass(), TEXT("Chk_RememberAddress"));
        MarkVariable(Connect, Remember);
        Place(C, Remember, {-230, 600}, {44, 44}, FAnchors(0.5f, 0));
        Place(C, Text(Connect, TEXT("Txt_RememberAddressLabel"), TEXT("记住服务器地址"), 22),
            {-170, 603}, {280, 40}, FAnchors(0.5f, 0));
        UButton* ConnectButton = Button(Connect, TEXT("Btn_Connect"), TEXT(""));
        Place(C, ConnectButton, {-230, 680}, {460, 76}, FAnchors(0.5f, 0));
        ReplaceButtonContent(Connect, ConnectButton,
            Text(Connect, TEXT("Txt_ConnectButton"), TEXT("连接服务器"), 30));
        Place(C, Text(Connect, TEXT("Txt_Version"), TEXT("版本 0.2.0 · UE 5.8"), 20),
            {30, -54}, {360, 36}, FAnchors(0, 1));
    }
    Finish(Connect);

    UWidgetBlueprint* Lobby = Create(TEXT("Screens"), TEXT("WBP_Lobby"), UMobileLobbyWidget::StaticClass());
    {
        UCanvasPanel* C = Root(Lobby);
        UBorder* PlayerPanel = Border(Lobby, TEXT("Panel_PlayerInfo"), FLinearColor(0.02f, 0.14f, 0.12f, 0.88f));
        Place(C, PlayerPanel, {48, 42}, {520, 210})->SetZOrder(-1);
        Place(C, Text(Lobby, TEXT("Txt_PlayerName"), TEXT("玩家昵称"), 34), {82, 70}, {430, 54});
        Place(C, Text(Lobby, TEXT("Txt_PlayerId"), TEXT("玩家ID：--"), 22), {82, 132}, {430, 40});
        Place(C, Text(Lobby, TEXT("Txt_OnlineCount"), TEXT("在线人数：0"), 22), {82, 184}, {430, 40});

        UBorder* ActionPanel = Border(Lobby, TEXT("Panel_LobbyActions"), FLinearColor(0.02f, 0.14f, 0.12f, 0.78f));
        Place(C, ActionPanel, {0, 300}, {620, 320}, FAnchors(0.5f, 0), {0.5f, 0})->SetZOrder(-1);
        Place(C, Button(Lobby, TEXT("Btn_QuickStart"), TEXT("快速开始")),
            {0, 340}, {500, 110}, FAnchors(0.5f, 0), {0.5f, 0});
        Place(C, Button(Lobby, TEXT("Btn_CreateRoom"), TEXT("创建房间")),
            {-260, 490}, {240, 82}, FAnchors(0.5f, 0));
        Place(C, Button(Lobby, TEXT("Btn_JoinRoom"), TEXT("加入房间")),
            {20, 490}, {240, 82}, FAnchors(0.5f, 0));
        Place(C, Button(Lobby, TEXT("Btn_Setting"), TEXT("设置")),
            {-220, 60}, {180, 64}, FAnchors(1, 0));
    }
    Finish(Lobby);

    UWidgetBlueprint* Room = Create(TEXT("Screens"), TEXT("WBP_Room"), UMobileRoomWidget::StaticClass());
    {
        UCanvasPanel* C = Root(Room);
        Place(C, Text(Room, TEXT("Txt_RoomId"), TEXT("房间号：100001"), 34), {60, 45}, {500, 60});
        Place(C, Text(Room, TEXT("Txt_RuleSummary"), TEXT("贵阳捉鸡·四人房"), 24), {60, 105}, {500, 44});
        UClass* RuleSummaryClass = RuleSummary->GeneratedClass;
        UWidget* RuleSummaryWidget = Room->WidgetTree->ConstructWidget<UWidget>(RuleSummaryClass, TEXT("RuleSummary"));
        MarkVariable(Room, RuleSummaryWidget);
        Place(C, RuleSummaryWidget, {40, 160}, {560, 390});

        // 返回大厅始终固定在右上角，避免不同宽高比下落到屏幕外。
        Place(C, Button(Room, TEXT("Btn_ReturnLobby"), TEXT("返回大厅")), {1600, 45}, {240, 68});
        Place(C, Text(Room, TEXT("Seat_Top"), TEXT("等待玩家"), 25), {760, 105}, {400, 86});
        Place(C, Text(Room, TEXT("Seat_Left"), TEXT("等待玩家"), 25), {140, 545}, {400, 86});
        Place(C, Text(Room, TEXT("Seat_Right"), TEXT("等待玩家"), 25), {1380, 410}, {400, 86});
        Place(C, Text(Room, TEXT("Seat_Bottom"), TEXT("等待玩家"), 25), {760, 500}, {400, 86});
        Place(C, Button(Room, TEXT("Btn_Ready"), TEXT("准备")), {760, 610}, {240, 68});
        Place(C, Text(Room, TEXT("Txt_StartTip"), TEXT("满四人并准备后自动开始"), 22), {1030, 618}, {520, 44});
    }
    Finish(Room);

    UWidgetBlueprint* Settlement = Create(TEXT("Dialogs"), TEXT("WBP_Settlement"), UMobileSettlementWidget::StaticClass());
    { UCanvasPanel* C=Root(Settlement); UBorder* B=Border(Settlement,TEXT("Panel_Dialog9Slice"),PanelGreen); Place(C,B,{0,0},{980,720},FAnchors(0.5f,0.5f),{0.5f,0.5f}); UCanvasPanel* D=Settlement->WidgetTree->ConstructWidget<UCanvasPanel>(UCanvasPanel::StaticClass(),TEXT("Panel_SettlementContent")); MarkVariable(Settlement,D); B->AddChild(D); Place(D,Text(Settlement,TEXT("Txt_ResultTitle"),TEXT("本局结算"),42),{50,30},{880,64}); Place(D,Text(Settlement,TEXT("Txt_HuType"),TEXT("自摸"),28),{50,105},{880,48}); Place(D,Text(Settlement,TEXT("Txt_JiResult"),TEXT("鸡牌：0 张"),24),{50,160},{880,90}); Place(D,Vertical(Settlement,TEXT("Panel_PlayerScores")),{50,255},{880,250}); Place(D,Button(Settlement,TEXT("Btn_NextRound"),TEXT("确定")),{230,555},{240,76}); Place(D,Button(Settlement,TEXT("Btn_BackLobby"),TEXT("返回大厅")),{510,555},{240,76}); }
    Finish(Settlement);

    UWidgetBlueprint* Reconnect = Create(TEXT("Dialogs"), TEXT("WBP_ReconnectOverlay"), UMobileReconnectOverlayWidget::StaticClass());
    { UCanvasPanel* C=Root(Reconnect); UBorder* B=Border(Reconnect,TEXT("Panel_Reconnect9Slice"),FLinearColor(0.01f,0.05f,0.045f,0.94f)); Place(C,B,{0,0},{760,460},FAnchors(0.5f,0.5f),{0.5f,0.5f}); UCanvasPanel* D=Reconnect->WidgetTree->ConstructWidget<UCanvasPanel>(UCanvasPanel::StaticClass(),TEXT("Panel_ReconnectContent")); MarkVariable(Reconnect,D); B->AddChild(D); Place(D,Text(Reconnect,TEXT("Txt_ReconnectStatus"),TEXT("网络连接已断开，正在尝试重连"),30),{55,45},{650,64}); Place(D,Text(Reconnect,TEXT("Txt_RemainingTime"),TEXT("剩余 120 秒"),25),{55,120},{650,48}); Place(D,Button(Reconnect,TEXT("Btn_Reconnect"),TEXT("重新连接")),{170,220},{420,76}); Place(D,Button(Reconnect,TEXT("Btn_BackConnect"),TEXT("返回连接界面")),{170,320},{420,76}); }
    Finish(Reconnect);

    UWidgetBlueprint* HUD = Create(TEXT("Screens"), TEXT("WBP_GameHUD"), UMobileMahjongHUDWidget::StaticClass());
    {
        UCanvasPanel* C = Root(HUD);
        UViewport* Table3DViewport = HUD->WidgetTree->ConstructWidget<UViewport>(
            UViewport::StaticClass(), TEXT("Table3DViewport"));
        MarkVariable(HUD, Table3DViewport);
        Table3DViewport->SetBackgroundColor(FLinearColor::Transparent);
        Table3DViewport->SetVisibility(ESlateVisibility::HitTestInvisible);
        Place(C, Table3DViewport, {0,0}, {1920,1080});
        Place(C, Text(HUD, TEXT("Txt_RoomId"), TEXT("房间号  100001"), 20), {30,24}, {360,36});
        Place(C, Text(HUD, TEXT("Txt_CurrentPhase"), TEXT("阶段：玩家回合"), 20), {30,62}, {420,36});
        Place(C, Text(HUD, TEXT("Txt_RemainingTileCount"), TEXT("剩余：83"), 26), {820,20}, {250,44});
        Place(C, Text(HUD, TEXT("Txt_CurrentTurnPlayer"), TEXT("当前：玩家"), 20), {790,62}, {360,36});
        Place(C, Text(HUD, TEXT("Txt_Countdown"), TEXT("15"), 36), {1125,20}, {120,52});
        Place(C, Text(HUD, TEXT("Txt_FlippedJiTile"), TEXT("翻鸡：尚未翻牌"), 20), {1300,35}, {500,36});
        Place(C, Text(HUD, TEXT("Txt_JiEvents"), TEXT("特殊鸡事件：无"), 18), {1300,72}, {570,72});

        // 参考传统桌面麻将布局：三家暗手沿桌边排列，只显示公开数量，不泄露牌面。
        Place(C, Horizontal(HUD, TEXT("Panel_TopHandTiles")), {745,112}, {430,66});
        Place(C, Vertical(HUD, TEXT("Panel_LeftHandTiles")), {205,315}, {58,360});
        Place(C, Vertical(HUD, TEXT("Panel_RightHandTiles")), {1660,315}, {58,360});

        UWrapBox* SelfDiscards = Wrap(HUD, TEXT("Panel_SelfDiscards"));
        UWrapBox* TopDiscards = Wrap(HUD, TEXT("Panel_TopDiscards"));
        UWrapBox* LeftDiscards = Wrap(HUD, TEXT("Panel_LeftDiscards"));
        UWrapBox* RightDiscards = Wrap(HUD, TEXT("Panel_RightDiscards"));
        for (UWrapBox* Panel : {SelfDiscards, TopDiscards, LeftDiscards, RightDiscards})
        {
            Panel->SetInnerSlotPadding(FVector2D(6.0f, 6.0f));
        }
        Place(C, SelfDiscards, {720,620}, {480,150});
        Place(C, TopDiscards, {720,250}, {480,150});
        Place(C, LeftDiscards, {445,385}, {300,250});
        Place(C, RightDiscards, {1180,385}, {300,250});

        Place(C, Vertical(HUD, TEXT("Panel_SelfMelds")), {400,755}, {300,130});
        Place(C, Vertical(HUD, TEXT("Panel_TopMelds")), {440,155}, {300,130});
        Place(C, Vertical(HUD, TEXT("Panel_LeftMelds")), {275,600}, {260,150});
        Place(C, Vertical(HUD, TEXT("Panel_RightMelds")), {1385,600}, {260,150});
        // 玩家头像贴近四边，信息放在头像相邻区域，形成清晰的四人座次关系。
        auto AddSeatAvatar = [HUD, C](const FName Name, const FVector2D Position,
            const TCHAR* TexturePath)
        {
            UImage* Avatar = Image(HUD, Name, FLinearColor::White);
            Avatar->SetBrush(TextureBrush(TexturePath));
            Place(C, Avatar, Position, {88,88});
        };
        AddSeatAvatar(TEXT("Img_Seat_Self"), {40,790},
            TEXT("/Game/UI/Textures/Avatars/T_PlayerAvatar_Placeholder_A.T_PlayerAvatar_Placeholder_A"));
        AddSeatAvatar(TEXT("Img_Seat_Top"), {1495,105},
            TEXT("/Game/UI/Textures/Avatars/T_PlayerAvatar_Placeholder_A.T_PlayerAvatar_Placeholder_A"));
        AddSeatAvatar(TEXT("Img_Seat_Left"), {30,190},
            TEXT("/Game/UI/Textures/Avatars/T_PlayerAvatar_Placeholder_B.T_PlayerAvatar_Placeholder_B"));
        AddSeatAvatar(TEXT("Img_Seat_Right"), {1795,190},
            TEXT("/Game/UI/Textures/Avatars/T_PlayerAvatar_Placeholder_B.T_PlayerAvatar_Placeholder_B"));
        Place(C, Text(HUD, TEXT("Seat_Self"), TEXT("我\n13张\n0分"), 20), {138,795}, {190,96});
        Place(C, Text(HUD, TEXT("Seat_Top"), TEXT("玩家\n13张\n0分"), 20), {1595,110}, {260,96});
        Place(C, Text(HUD, TEXT("Seat_Left"), TEXT("玩家\n13张\n0分"), 20), {30,285}, {180,96});
        Place(C, Text(HUD, TEXT("Seat_Right"), TEXT("玩家\n13张\n0分"), 20), {1710,285}, {180,96});

        // 等待玩家和准备操作直接覆盖在三维牌桌上，不再使用独立 WBP_Room 页面。
        Place(C, Button(HUD, TEXT("Btn_ReturnLobby"), TEXT("返回大厅")), {1640,40}, {230,64});
        Place(C, Button(HUD, TEXT("Btn_Ready"), TEXT("准备")), {840,770}, {240,72});
        UTextBlock* ReadyStatus = Text(HUD, TEXT("Txt_ReadyStatus"), TEXT("点击准备，满四人后自动开始"), 22);
        ReadyStatus->SetJustification(ETextJustify::Center);
        Place(C, ReadyStatus, {650,850}, {620,44});

        // The 3D table mesh contains the authoritative centre direction disc. Do not generate a
        // duplicate UMG panel over it.

        Place(C, Horizontal(HUD, TEXT("Panel_SelfHandTiles")), {210,890}, {1500,150});

        UClass* ActionClass = Action->GeneratedClass;
        UWidget* ActionWidget = HUD->WidgetTree->ConstructWidget<UWidget>(ActionClass, TEXT("ActionButtonPanel"));
        MarkVariable(HUD, ActionWidget);
        Place(C, ActionWidget, {660,785}, {600,96});
        UOverlay* Popup = HUD->WidgetTree->ConstructWidget<UOverlay>(UOverlay::StaticClass(), TEXT("PopupLayer"));
        MarkVariable(HUD, Popup);
        UCanvasPanelSlot* PopupSlot = Place(C, Popup, {0,0}, {0,0}, FAnchors(0,0,1,1));
        PopupSlot->SetOffsets(FMargin(0));
    }
    Finish(HUD);

    UE_LOG(LogTemp, Display, TEXT("P0 UMG 生成结束：成功=%d/19"), Created);
    return Created == 19 ? 0 : 1;
}
#else
UGenerateMahjongUICommandlet::UGenerateMahjongUICommandlet()
{
    IsClient = false; IsEditor = false; IsServer = false; LogToConsole = true;
}

int32 UGenerateMahjongUICommandlet::Main(const FString& Params)
{
    UE_LOG(LogTemp, Error, TEXT("UMG 资产生成命令只能在 Editor Target 中运行"));
    return 1;
}
#endif
