# 打开房间预览地图和表现 Blueprint 供人工编辑，确保目标窗口可见并聚焦。
# 不自动保存修改；预览地图与运行地图必须保持隔离，缺失资产时停止。
"""Open the room preview map and the presentation Blueprint for manual editing."""

import unreal


MAP_PATH = "/Game/Maps/MahjongRoomVisualPreviewMap"
ASSET_PATH = (
    "/Game/Client/Room/Presentation/"
    "BP_MahjongRoomPresentation.BP_MahjongRoomPresentation"
)


world = unreal.EditorLoadingAndSavingUtils.load_map(MAP_PATH)
if not world:
    raise RuntimeError(f"Could not load {MAP_PATH}")

blueprint = unreal.EditorAssetLibrary.load_asset(ASSET_PATH)
if not blueprint:
    raise RuntimeError(f"Could not load {ASSET_PATH}")

# The normal asset-opening path may still choose Blueprint Defaults mode even
# after the asset has gained components/graphs. This transient engine flag is
# exactly what the "Open Full Blueprint Editor" hyperlink sets internally.
try:
    blueprint.set_editor_property("force_full_editor", True)
    unreal.log("MAHJONG_PRESENTATION_FORCE_FULL_EDITOR_SET")
except Exception:
    # bForceFullEditor is a native transient UPROPERTY that is not exposed by
    # every Unreal Python build. The persistent marker graph remains the
    # fallback; the editor module command is the authoritative path.
    unreal.log_warning("MAHJONG_PRESENTATION_FORCE_FULL_EDITOR_NOT_EXPOSED")

unreal.EditorAssetLibrary.sync_browser_to_objects([ASSET_PATH])
asset_editor = unreal.get_editor_subsystem(unreal.AssetEditorSubsystem)
if not asset_editor.open_editor_for_assets([blueprint]):
    raise RuntimeError(f"Could not open {ASSET_PATH}")

unreal.log("MAHJONG_PRESENTATION_EDITOR_OPEN_OK")
