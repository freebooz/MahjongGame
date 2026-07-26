"""重置编辑器布局后，打开所有需要三维视口的代表性资产进行人工验证。"""

import unreal


LEVEL_PATH = "/Game/Maps/MahjongRoomVisualPreviewMap"
ASSET_PATHS = (
    "/Game/Client/Room/Presentation/BP_MahjongRoomPresentation",
    "/Game/Art/Mahjong/Mahjong50/Materials/M_Mahjong50_BodyBlend",
    "/Game/Art/Mahjong/Mahjong50/Tiles/SM_Mahjong50_Characters_1",
)


level_editor = unreal.get_editor_subsystem(unreal.LevelEditorSubsystem)
level_loaded = (
    level_editor.load_level(LEVEL_PATH)
    if level_editor is not None
    else unreal.EditorLevelLibrary.load_level(LEVEL_PATH)
)
if not level_loaded:
    unreal.log_error(f"EDITOR_VIEWPORT_VALIDATE_LEVEL_FAILED path={LEVEL_PATH}")
else:
    unreal.log(f"EDITOR_VIEWPORT_VALIDATE_LEVEL_OPEN_OK path={LEVEL_PATH}")

assets = []
for asset_path in ASSET_PATHS:
    asset = unreal.EditorAssetLibrary.load_asset(asset_path)
    if asset is None:
        unreal.log_error(f"EDITOR_VIEWPORT_VALIDATE_ASSET_MISSING path={asset_path}")
    else:
        assets.append(asset)

if assets:
    asset_editor = unreal.get_editor_subsystem(unreal.AssetEditorSubsystem)
    if asset_editor is None:
        unreal.log_error("EDITOR_VIEWPORT_VALIDATE_SUBSYSTEM_MISSING name=AssetEditorSubsystem")
    else:
        asset_editor.open_editor_for_assets(assets)
        for asset in assets:
            unreal.log(
                "EDITOR_VIEWPORT_VALIDATE_ASSET_OPEN_REQUESTED "
                f"path={asset.get_path_name()} class={asset.get_class().get_name()}"
            )
