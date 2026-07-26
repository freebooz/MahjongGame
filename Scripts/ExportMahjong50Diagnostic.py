"""Export one Mahjong50 tile mesh for geometry/material-orientation inspection."""

from pathlib import Path

import unreal


ASSET_PATH = "/Game/Art/Mahjong/Mahjong50/Tiles/SM_Mahjong50_Characters_1"
OUTPUT_ROOT = (
    Path(unreal.Paths.project_saved_dir())
    / "Diagnostics"
    / "Mahjong50"
)
OUTPUT_PATH = OUTPUT_ROOT / "SM_Mahjong50_Characters_1.fbx"


asset = unreal.EditorAssetLibrary.load_asset(ASSET_PATH)
if not asset:
    raise RuntimeError(f"Missing diagnostic tile asset: {ASSET_PATH}")

OUTPUT_PATH.parent.mkdir(parents=True, exist_ok=True)
task = unreal.AssetExportTask()
task.object = asset
task.filename = str(OUTPUT_PATH)
task.automated = True
task.prompt = False
task.replace_identical = True
task.options = unreal.FbxExportOption()

if not unreal.Exporter.run_asset_export_task(task):
    raise RuntimeError(f"Could not export {ASSET_PATH} to {OUTPUT_PATH}")

unreal.log(f"MAHJONG50_DIAGNOSTIC_EXPORT_OK path={OUTPUT_PATH}")

for texture_name in (
    "T_Mahjong50_FaceAtlas_BaseColor",
    "T_Mahjong50_FaceAtlas_Normal",
    "T_Mahjong50_FaceAtlas_Height",
    "T_Mahjong50_FaceAtlas_ORM",
):
    texture_path = f"/Game/Art/Mahjong/Mahjong50/Textures/{texture_name}"
    texture = unreal.EditorAssetLibrary.load_asset(texture_path)
    if not texture:
        raise RuntimeError(f"Missing diagnostic texture: {texture_path}")
    texture_task = unreal.AssetExportTask()
    texture_task.object = texture
    texture_task.filename = str(OUTPUT_ROOT / f"{texture_name}.png")
    texture_task.automated = True
    texture_task.prompt = False
    texture_task.replace_identical = True
    if not unreal.Exporter.run_asset_export_task(texture_task):
        raise RuntimeError(f"Could not export {texture_path}")

unreal.log(f"MAHJONG50_DIAGNOSTIC_TEXTURES_OK path={OUTPUT_ROOT}")
