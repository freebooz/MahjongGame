# 导入“创建房间”背景并更新对应 UI 引用，只处理明确目标，不触碰其他界面资源。
# 更新前删除旧目标资产并全量创建新纹理/材质；保存失败时不得留下断裂引用。
"""Import the generated creating-room background without touching unrelated UI assets."""

from pathlib import Path

import unreal


project_root = Path(unreal.Paths.project_dir())
source = project_root / "SourceArt" / "UI" / "Backgrounds" / "T_BG_CreatingRoom_GuiyangMoon.png"
destination = "/Game/UI/Textures/Backgrounds"
asset_path = f"{destination}/T_BG_CreatingRoom_GuiyangMoon"

if not source.is_file():
    raise RuntimeError(f"Creating-room background is missing: {source}")

task = unreal.AssetImportTask()
task.filename = str(source)
task.destination_path = destination
task.destination_name = source.stem
task.automated = True
task.replace_existing = True
task.save = True
unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])

texture = unreal.EditorAssetLibrary.load_asset(asset_path)
if not texture:
    raise RuntimeError(f"Failed to import {asset_path}")

texture.set_editor_property("compression_settings", unreal.TextureCompressionSettings.TC_EDITOR_ICON)
texture.set_editor_property("lod_group", unreal.TextureGroup.TEXTUREGROUP_UI)
texture.set_editor_property("srgb", True)
texture.set_editor_property("never_stream", False)
unreal.EditorAssetLibrary.save_loaded_asset(texture, only_if_is_dirty=False)
unreal.log(f"[GuiyangUIImport] Imported creating-room background: {asset_path}")
