import os

import unreal


SOURCE_DIR = os.path.join(
    unreal.Paths.project_dir(), "SourceArt", "UI", "Buttons", "ReferenceHUD"
)

ASSETS = {
    "T_Action_Pass_Reference": "/Game/UI/Textures/Buttons/ReferenceHUD",
    "T_Action_Peng_Reference": "/Game/UI/Textures/Buttons/ReferenceHUD",
    "T_Action_Gang_Reference": "/Game/UI/Textures/Buttons/ReferenceHUD",
    "T_Action_Hu_Reference": "/Game/UI/Textures/Buttons/ReferenceHUD",
    "T_Action_Ting_Reference": "/Game/UI/Textures/Buttons/ReferenceHUD",
    "T_Player_GoldBean_Reference": "/Game/UI/Textures/Icons/ReferenceHUD",
}


def import_texture(asset_name, destination_path):
    source_file = os.path.join(SOURCE_DIR, f"{asset_name}.png")
    if not os.path.isfile(source_file):
        raise RuntimeError(f"Reference room control image is missing: {source_file}")

    task = unreal.AssetImportTask()
    task.filename = source_file
    task.destination_path = destination_path
    task.destination_name = asset_name
    task.automated = True
    task.replace_existing = True
    task.replace_existing_settings = True
    task.save = True

    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])
    if not task.imported_object_paths:
        raise RuntimeError(f"Failed to import reference room control: {source_file}")

    texture = unreal.load_asset(task.imported_object_paths[0])
    if not texture:
        raise RuntimeError(f"Imported texture could not be loaded: {asset_name}")

    texture.set_editor_property(
        "compression_settings", unreal.TextureCompressionSettings.TC_EDITOR_ICON
    )
    texture.set_editor_property("lod_group", unreal.TextureGroup.TEXTUREGROUP_UI)
    texture.set_editor_property("srgb", True)
    texture.set_editor_property("never_stream", True)
    texture.set_editor_property(
        "mip_gen_settings", unreal.TextureMipGenSettings.TMGS_NO_MIPMAPS
    )
    texture.set_editor_property(
        "alpha_coverage_thresholds", unreal.Vector4(0.0, 0.0, 0.0, 0.0)
    )
    texture.modify()
    unreal.EditorAssetLibrary.save_loaded_asset(texture)
    unreal.log(f"REFERENCE_ROOM_CONTROL_IMPORTED {task.imported_object_paths[0]}")


for name, destination in ASSETS.items():
    import_texture(name, destination)

unreal.log("REFERENCE_ROOM_CONTROL_IMPORT_COMPLETE")
