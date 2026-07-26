#include "GuiyangMahjongEditorTools.h"

#include "BlueprintEditorModule.h"
#include "BlueprintEditorTabs.h"
#include "Components/SceneComponent.h"
#include "ContentBrowserModule.h"
#include "Engine/Blueprint.h"
#include "Engine/SCS_Node.h"
#include "Engine/SimpleConstructionScript.h"
#include "Engine/StaticMesh.h"
#include "Framework/Docking/TabManager.h"
#include "HAL/IConsoleManager.h"
#include "IContentBrowserSingleton.h"
#include "IMaterialEditor.h"
#include "IStaticMeshEditor.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "MaterialEditorModule.h"
#include "Materials/Material.h"
#include "Misc/CoreDelegates.h"
#include "Misc/PackageName.h"
#include "Modules/ModuleManager.h"
#include "StaticMeshEditorModule.h"
#include "UObject/SavePackage.h"

namespace
{
constexpr TCHAR RoomPresentationAssetPath[] =
    TEXT("/Game/Client/Room/Presentation/"
         "BP_MahjongRoomPresentation.BP_MahjongRoomPresentation");
constexpr TCHAR MahjongTableAssetPath[] =
    TEXT("/Game/Art/Mahjong/Table/Meshes/"
         "SM_StandardMahjongTable.SM_StandardMahjongTable");
constexpr TCHAR MahjongTableWalnutMaterialPath[] =
    TEXT("/Game/Art/Mahjong/Table/Materials/"
         "M_Table_Walnut_Miter_PBR.M_Table_Walnut_Miter_PBR");
constexpr TCHAR MahjongTableFeltMaterialPath[] =
    TEXT("/Game/Art/Mahjong/Table/Materials/"
         "M_Table_Felt_Green_Fiber_PBR.M_Table_Felt_Green_Fiber_PBR");

UBlueprint* LoadAndRepairRoomPresentationBlueprint()
{
    UBlueprint* Blueprint = LoadObject<UBlueprint>(nullptr, RoomPresentationAssetPath);
    if (!Blueprint)
    {
        UE_LOG(
            LogTemp,
            Error,
            TEXT("Mahjong room presentation Blueprint was not found: %s"),
            RoomPresentationAssetPath);
        return nullptr;
    }

    bool bNeedsSave = false;
    USimpleConstructionScript* ConstructionScript = Blueprint->SimpleConstructionScript;
    if (ConstructionScript
        && !ConstructionScript->FindSCSNode(TEXT("RoomPresentationEditorRoot")))
    {
        Blueprint->Modify();
        USCS_Node* EditorRootNode = ConstructionScript->CreateNode(
            USceneComponent::StaticClass(),
            TEXT("RoomPresentationEditorRoot"));
        ConstructionScript->AddNode(EditorRootNode);
        FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(Blueprint);
        bNeedsSave = true;
    }

    // 本次会话立即使用完整编辑器；持久化的 SCS 节点则保证下次启动仍非“仅数据蓝图”。
    Blueprint->bForceFullEditor = true;

    if (bNeedsSave)
    {
        UPackage* Package = Blueprint->GetOutermost();
        Package->MarkPackageDirty();

        const FString Filename = FPackageName::LongPackageNameToFilename(
            Package->GetName(),
            FPackageName::GetAssetPackageExtension());
        FSavePackageArgs SaveArgs;
        SaveArgs.TopLevelFlags = RF_Public | RF_Standalone;
        SaveArgs.SaveFlags = SAVE_NoError;
        if (!UPackage::SavePackage(Package, Blueprint, *Filename, SaveArgs))
        {
            UE_LOG(
                LogTemp,
                Error,
                TEXT("Failed to persist full Blueprint editor mode: %s"),
                RoomPresentationAssetPath);
            return nullptr;
        }

        UE_LOG(
            LogTemp,
            Display,
            TEXT("MAHJONG_ROOM_PRESENTATION_COMPONENT_PERSISTED asset=%s"),
            RoomPresentationAssetPath);
    }

    return Blueprint;
}
}

void FGuiyangMahjongEditorToolsModule::StartupModule()
{
    OpenRoomPresentationCommand = IConsoleManager::Get().RegisterConsoleCommand(
        TEXT("Mahjong.OpenRoomPresentationEditor"),
        TEXT("Open the Mahjong room presentation Blueprint in the full Components/Viewport editor."),
        FConsoleCommandDelegate::CreateRaw(
            this, &FGuiyangMahjongEditorToolsModule::OpenRoomPresentationEditor),
        ECVF_Default);

    OpenMahjongTableCommand = IConsoleManager::Get().RegisterConsoleCommand(
        TEXT("Mahjong.OpenTableStaticMeshEditor"),
        TEXT("Open the Mahjong table in the full Static Mesh Editor with its 3D viewport."),
        FConsoleCommandDelegate::CreateRaw(
            this, &FGuiyangMahjongEditorToolsModule::OpenMahjongTableStaticMeshEditor),
        ECVF_Default);

    OpenMahjongTableMaterialsCommand = IConsoleManager::Get().RegisterConsoleCommand(
        TEXT("Mahjong.OpenTableMaterialEditors"),
        TEXT("Open both Mahjong table materials in the full Material Editor with node graphs."),
        FConsoleCommandDelegate::CreateRaw(
            this, &FGuiyangMahjongEditorToolsModule::OpenMahjongTableMaterialEditors),
        ECVF_Default);

    // Loading and saving an Actor Blueprint during StartupModule is too early for World
    // Partition class descriptors. Repair it only after the engine has fully initialized.
    PostEngineInitHandle = FCoreDelegates::GetOnPostEngineInit().AddRaw(
        this, &FGuiyangMahjongEditorToolsModule::HandlePostEngineInit);
}

void FGuiyangMahjongEditorToolsModule::ShutdownModule()
{
    if (PostEngineInitHandle.IsValid())
    {
        FCoreDelegates::GetOnPostEngineInit().Remove(PostEngineInitHandle);
        PostEngineInitHandle.Reset();
    }

    if (OpenRoomPresentationCommand)
    {
        IConsoleManager::Get().UnregisterConsoleObject(OpenRoomPresentationCommand);
        OpenRoomPresentationCommand = nullptr;
    }

    if (OpenMahjongTableCommand)
    {
        IConsoleManager::Get().UnregisterConsoleObject(OpenMahjongTableCommand);
        OpenMahjongTableCommand = nullptr;
    }

    if (OpenMahjongTableMaterialsCommand)
    {
        IConsoleManager::Get().UnregisterConsoleObject(OpenMahjongTableMaterialsCommand);
        OpenMahjongTableMaterialsCommand = nullptr;
    }
}

void FGuiyangMahjongEditorToolsModule::HandlePostEngineInit()
{
    // This asset is intentionally an artist-facing composition Blueprint. UE can otherwise
    // classify it as data-only and reopen it without Components or the 3D viewport.
    LoadAndRepairRoomPresentationBlueprint();
}

void FGuiyangMahjongEditorToolsModule::OpenRoomPresentationEditor()
{
    if (IConsoleVariable* PrimingLimit =
            IConsoleManager::Get().FindConsoleVariable(TEXT("bp.DatabasePrimingMaxPerFrame")))
    {
        // UE 5.8 can crash while priming an AnimGraph node template even when
        // the opened asset is an Actor Blueprint. Manual room composition
        // does not need this optional background cache.
        PrimingLimit->Set(0, ECVF_SetByCode);
    }

    UBlueprint* Blueprint = LoadAndRepairRoomPresentationBlueprint();
    if (!Blueprint)
    {
        return;
    }

    FContentBrowserModule& ContentBrowser =
        FModuleManager::LoadModuleChecked<FContentBrowserModule>(TEXT("ContentBrowser"));
    ContentBrowser.Get().SyncBrowserToAssets({FAssetData(Blueprint)});

    FBlueprintEditorModule& BlueprintEditorModule =
        FModuleManager::LoadModuleChecked<FBlueprintEditorModule>(TEXT("Kismet"));
    const TSharedRef<IBlueprintEditor> BlueprintEditor =
        BlueprintEditorModule.CreateBlueprintEditor(
            EToolkitMode::Standalone,
            TSharedPtr<IToolkitHost>(),
            Blueprint,
            false);
    BlueprintEditor->GetTabManager()->TryInvokeTab(
        FBlueprintEditorTabs::SCSViewportID);

    UE_LOG(
        LogTemp,
        Display,
        TEXT("MAHJONG_FULL_BLUEPRINT_EDITOR_OPEN_OK asset=%s mode=%s"),
        RoomPresentationAssetPath,
        *BlueprintEditor->GetCurrentMode().ToString());
}

void FGuiyangMahjongEditorToolsModule::OpenMahjongTableStaticMeshEditor()
{
    UStaticMesh* TableMesh = LoadObject<UStaticMesh>(nullptr, MahjongTableAssetPath);
    if (!TableMesh)
    {
        UE_LOG(
            LogTemp,
            Error,
            TEXT("Mahjong table static mesh was not found: %s"),
            MahjongTableAssetPath);
        return;
    }

    FContentBrowserModule& ContentBrowser =
        FModuleManager::LoadModuleChecked<FContentBrowserModule>(TEXT("ContentBrowser"));
    ContentBrowser.Get().SyncBrowserToAssets({FAssetData(TableMesh)});

    IStaticMeshEditorModule& StaticMeshEditorModule =
        FModuleManager::LoadModuleChecked<IStaticMeshEditorModule>(TEXT("StaticMeshEditor"));
    const TSharedRef<IStaticMeshEditor> StaticMeshEditor =
        StaticMeshEditorModule.CreateStaticMeshEditor(
            EToolkitMode::Standalone,
            TSharedPtr<IToolkitHost>(),
            TableMesh);
    StaticMeshEditor->GetTabManager()->TryInvokeTab(
        FName(TEXT("StaticMeshEditor_Viewport")));

    UE_LOG(
        LogTemp,
        Display,
        TEXT("MAHJONG_FULL_STATIC_MESH_EDITOR_OPEN_OK asset=%s"),
        MahjongTableAssetPath);
}

void FGuiyangMahjongEditorToolsModule::OpenMahjongTableMaterialEditors()
{
    UMaterial* FeltMaterial =
        LoadObject<UMaterial>(nullptr, MahjongTableFeltMaterialPath);
    UMaterial* WalnutMaterial =
        LoadObject<UMaterial>(nullptr, MahjongTableWalnutMaterialPath);
    if (!FeltMaterial || !WalnutMaterial)
    {
        UE_LOG(
            LogTemp,
            Error,
            TEXT("Mahjong table materials were not found: felt=%s walnut=%s"),
            FeltMaterial ? TEXT("ok") : TEXT("missing"),
            WalnutMaterial ? TEXT("ok") : TEXT("missing"));
        return;
    }

    FContentBrowserModule& ContentBrowser =
        FModuleManager::LoadModuleChecked<FContentBrowserModule>(TEXT("ContentBrowser"));
    ContentBrowser.Get().SyncBrowserToAssets(
        {FAssetData(FeltMaterial), FAssetData(WalnutMaterial)});

    IMaterialEditorModule& MaterialEditorModule =
        FModuleManager::LoadModuleChecked<IMaterialEditorModule>(TEXT("MaterialEditor"));

    // Open felt first and walnut last so the table's main visible material is foreground.
    const TSharedRef<IMaterialEditor> FeltEditor =
        MaterialEditorModule.CreateMaterialEditor(
            EToolkitMode::Standalone,
            TSharedPtr<IToolkitHost>(),
            FeltMaterial);
    FeltEditor->GetTabManager()->TryInvokeTab(FName(TEXT("Document")));

    const TSharedRef<IMaterialEditor> WalnutEditor =
        MaterialEditorModule.CreateMaterialEditor(
            EToolkitMode::Standalone,
            TSharedPtr<IToolkitHost>(),
            WalnutMaterial);
    WalnutEditor->GetTabManager()->TryInvokeTab(FName(TEXT("Document")));

    UE_LOG(
        LogTemp,
        Display,
        TEXT("MAHJONG_FULL_MATERIAL_EDITORS_OPEN_OK felt=%s walnut=%s"),
        MahjongTableFeltMaterialPath,
        MahjongTableWalnutMaterialPath);
}

IMPLEMENT_MODULE(FGuiyangMahjongEditorToolsModule, GuiyangMahjongEditorTools)
