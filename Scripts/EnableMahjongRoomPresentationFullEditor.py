# 为 BP_MahjongRoomPresentation 持久启用完整 Blueprint 编辑器模式，方便人工检查组件和逻辑。
# 仅修改该目标 Blueprint 的编辑器元数据；不得更改运行时类、默认玩法值或其他蓝图。
"""Persistently enable the full editor for BP_MahjongRoomPresentation.

Unreal's bForceFullEditor flag is transient, so it is not sufficient for an
asset that must keep opening with Components and Viewport tabs. An empty,
unused function graph makes the Blueprint non-data-only without changing its
runtime presentation components or designer-authored transforms.
"""

import unreal


ASSET_PATH = (
    "/Game/Client/Room/Presentation/"
    "BP_MahjongRoomPresentation.BP_MahjongRoomPresentation"
)
MARKER_GRAPH = "EditorViewportMarker"


blueprint = unreal.EditorAssetLibrary.load_asset(ASSET_PATH)
if not blueprint:
    raise RuntimeError(f"Could not load {ASSET_PATH}")

graph_names = {str(name) for name in unreal.BlueprintEditorLibrary.list_graph_names(blueprint)}
if MARKER_GRAPH not in graph_names:
    marker_graph = unreal.BlueprintEditorLibrary.add_function_graph(
        blueprint, MARKER_GRAPH
    )
    if not marker_graph:
        raise RuntimeError(f"Could not add {MARKER_GRAPH} to {ASSET_PATH}")
    unreal.log(f"MAHJONG_FULL_EDITOR_MARKER_ADDED={MARKER_GRAPH}")
else:
    unreal.log(f"MAHJONG_FULL_EDITOR_MARKER_REUSED={MARKER_GRAPH}")

unreal.BlueprintEditorLibrary.compile_blueprint(blueprint)
if not unreal.EditorAssetLibrary.save_loaded_asset(
    blueprint, only_if_is_dirty=False
):
    raise RuntimeError(f"Could not save {ASSET_PATH}")

verified_names = {
    str(name) for name in unreal.BlueprintEditorLibrary.list_graph_names(blueprint)
}
if MARKER_GRAPH not in verified_names:
    raise RuntimeError(f"{MARKER_GRAPH} was not persisted")

unreal.log("MAHJONG_PRESENTATION_FULL_EDITOR_OK")
