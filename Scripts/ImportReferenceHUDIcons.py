import os
import unreal


SOURCE_DIR = os.path.join(
    unreal.Paths.project_dir(), "SourceArt", "UI", "Icons", "ReferenceHUD"
)
DESTINATION = "/Game/UI/Textures/Icons/ReferenceHUD"
ICON_NAMES = (
    "Icon_ReferenceRules",
    "Icon_ReferenceSettings",
    "Icon_ReferenceTrustee",
    "Icon_ReferenceExit",
)


def import_icon(icon_name):
    source_file = os.path.join(SOURCE_DIR, f"{icon_name}.png")
    if not os.path.isfile(source_file):
        raise RuntimeError(f"Reference HUD icon is missing: {source_file}")

    task = unreal.AssetImportTask()
    task.filename = source_file
    task.destination_path = DESTINATION
    task.destination_name = icon_name
    task.automated = True
    task.replace_existing = True
    task.replace_existing_settings = True
    task.save = True

    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])
    if not task.imported_object_paths:
        raise RuntimeError(f"Failed to import reference HUD icon: {source_file}")

    texture = unreal.load_asset(task.imported_object_paths[0])
    if not texture:
        raise RuntimeError(f"Imported icon could not be loaded: {icon_name}")

    texture.set_editor_property("compression_settings", unreal.TextureCompressionSettings.TC_EDITOR_ICON)
    texture.set_editor_property("lod_group", unreal.TextureGroup.TEXTUREGROUP_UI)
    texture.set_editor_property("srgb", True)
    texture.set_editor_property("never_stream", True)
    texture.set_editor_property("mip_gen_settings", unreal.TextureMipGenSettings.TMGS_NO_MIPMAPS)
    texture.set_editor_property("alpha_coverage_thresholds", unreal.Vector4(0.0, 0.0, 0.0, 0.0))
    texture.modify()
    unreal.EditorAssetLibrary.save_loaded_asset(texture)
    unreal.log(f"REFERENCE_HUD_ICON_IMPORTED {task.imported_object_paths[0]}")


for name in ICON_NAMES:
    import_icon(name)

unreal.log("REFERENCE_HUD_ICON_IMPORT_COMPLETE")
