# 导入已批准登录背景并绑定到登录界面，保持其他 UI 纹理、材质和布局不变。
# 目标旧资产必须先删除再全量导入；色彩空间、压缩和移动端尺寸需要验证。
"""Import the approved login-screen background without touching unrelated UI assets."""

from pathlib import Path

import unreal


project_root = Path(unreal.Paths.project_dir())
source = project_root / "SourceArt" / "UI" / "Backgrounds" / "T_BG_Login_Guiyang.png"
destination = "/Game/UI/Textures/Backgrounds"
asset_path = f"{destination}/T_BG_Login_Guiyang"

if not source.is_file():
    raise RuntimeError(f"Login background is missing: {source}")

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

# Keep photographic UI backgrounds color-correct and memory-friendly while allowing
# the renderer to stream the full-resolution image on constrained devices.
texture.set_editor_property("compression_settings", unreal.TextureCompressionSettings.TC_EDITOR_ICON)
texture.set_editor_property("lod_group", unreal.TextureGroup.TEXTUREGROUP_UI)
texture.set_editor_property("srgb", True)
texture.set_editor_property("never_stream", False)
post_edit_change = getattr(texture, "post_edit_change", None)
if post_edit_change:
    post_edit_change()
unreal.EditorAssetLibrary.save_loaded_asset(texture, only_if_is_dirty=False)

size_x = texture.blueprint_get_size_x()
size_y = texture.blueprint_get_size_y()
if size_x != 1672 or size_y != 941:
    raise RuntimeError(f"Unexpected imported size: {size_x}x{size_y}")

unreal.log(f"[GuiyangUIImport] Imported login background: {asset_path} ({size_x}x{size_y})")
