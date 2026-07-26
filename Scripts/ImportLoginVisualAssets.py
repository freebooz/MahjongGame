import os
import unreal


SOURCE_DIR = os.path.join(unreal.Paths.project_dir(), "SourceArt", "UI", "Login")
DESTINATION = "/Game/UI/Textures/Login"
ASSET_NAMES = (
    "T_Login_Logo",
    "T_Checkbox_Unchecked",
    "T_Checkbox_Checked",
)


def import_ui_texture(asset_name):
    source_file = os.path.join(SOURCE_DIR, f"{asset_name}.png")
    if not os.path.isfile(source_file):
        raise RuntimeError(f"Login visual asset is missing: {source_file}")

    task = unreal.AssetImportTask()
    task.filename = source_file
    task.destination_path = DESTINATION
    task.destination_name = asset_name
    task.automated = True
    task.replace_existing = True
    task.replace_existing_settings = True
    task.save = True
    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])

    if not task.imported_object_paths:
        raise RuntimeError(f"Failed to import login visual asset: {source_file}")

    texture = unreal.load_asset(task.imported_object_paths[0])
    if not texture:
        raise RuntimeError(f"Imported login texture could not be loaded: {asset_name}")

    texture.set_editor_property("compression_settings", unreal.TextureCompressionSettings.TC_EDITOR_ICON)
    texture.set_editor_property("lod_group", unreal.TextureGroup.TEXTUREGROUP_UI)
    texture.set_editor_property("srgb", True)
    texture.set_editor_property("never_stream", True)
    texture.set_editor_property("mip_gen_settings", unreal.TextureMipGenSettings.TMGS_NO_MIPMAPS)
    texture.modify()
    unreal.EditorAssetLibrary.save_loaded_asset(texture)
    unreal.log(f"LOGIN_VISUAL_ASSET_IMPORTED {task.imported_object_paths[0]}")


for name in ASSET_NAMES:
    import_ui_texture(name)

unreal.log("LOGIN_VISUAL_ASSET_IMPORT_COMPLETE")
