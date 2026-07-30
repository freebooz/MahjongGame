# 在 Unreal Static Mesh Editor 中打开已导入自动麻将桌，供人工检查 LOD、材质、碰撞和尺寸。
# 只执行编辑器导航，不修改或保存资产；找不到目标时返回失败。
"""Open the imported automatic Mahjong table in Unreal's Static Mesh Editor."""

from pathlib import Path

import unreal


MESH_PATH = "/Game/Art/Mahjong/Table/Meshes/SM_StandardMahjongTable"
MARKER = (
    Path(__file__).resolve().parents[1]
    / "Saved"
    / "Reports"
    / "AutomaticMahjongTableEditorOpened.txt"
)


mesh = unreal.EditorAssetLibrary.load_asset(MESH_PATH)
if not mesh or not isinstance(mesh, unreal.StaticMesh):
    raise RuntimeError(f"Missing StaticMesh asset: {MESH_PATH}")

size = mesh.get_bounds().box_extent * 2.0
dimensions = (float(size.x), float(size.y), float(size.z))
if any(
    abs(value - expected) > 0.5
    for value, expected in zip(
        sorted(dimensions),
        sorted((300.0, 300.0, 5.9)),
    )
):
    raise RuntimeError(f"Unexpected table dimensions before opening: {dimensions}")

editor = unreal.get_editor_subsystem(unreal.AssetEditorSubsystem)
if not editor.open_editor_for_assets([mesh]):
    raise RuntimeError(f"Could not open Static Mesh Editor for {MESH_PATH}")

try:
    browser = unreal.get_editor_subsystem(unreal.ContentBrowserSubsystem)
    browser.sync_browser_to_assets([MESH_PATH])
except Exception as exc:
    unreal.log_warning(f"[AutomaticMahjongTableOpen] Content Browser sync skipped: {exc}")

MARKER.parent.mkdir(parents=True, exist_ok=True)
MARKER.write_text(
    "AUTOMATIC_MAHJONG_TABLE_EDITOR_OPENED "
    f"dimensions_cm={dimensions}\n",
    encoding="utf-8",
)
unreal.log(
    "[AutomaticMahjongTableOpen] AUTOMATIC_MAHJONG_TABLE_EDITOR_OPENED "
    f"dimensions_cm=({dimensions[0]:.3f},{dimensions[1]:.3f},{dimensions[2]:.3f})"
)
