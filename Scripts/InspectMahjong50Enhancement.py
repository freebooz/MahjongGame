"""Read-only verification for Mahjong50 engraved-face assets."""

import unreal


ROOT = "/Game/Art/Mahjong/Mahjong50"
MATERIAL_PATH = f"{ROOT}/Materials/M_Mahjong50_FaceAtlas_EngravedV3"
INSTANCE_PATH = f"{ROOT}/MaterialInstances/MI_Mahjong50_Characters_1"
TEXTURE_NAMES = (
    "T_Mahjong50_FaceAtlas_BaseColor",
    "T_Mahjong50_FaceAtlas_Normal",
    "T_Mahjong50_FaceAtlas_Height",
    "T_Mahjong50_FaceAtlas_ORM",
)


material = unreal.EditorAssetLibrary.load_asset(MATERIAL_PATH)
instance = unreal.EditorAssetLibrary.load_asset(INSTANCE_PATH)
if not material or not instance:
    raise RuntimeError(
        f"Missing engraved material or test instance: material={material} instance={instance}"
    )

parent = instance.get_editor_property("parent")
unreal.log_warning(
    "MAHJONG50_ENHANCED_MATERIAL "
    f"material={material.get_path_name()} parent={parent.get_path_name() if parent else 'None'}"
)

for texture_name in TEXTURE_NAMES:
    path = f"{ROOT}/Textures/{texture_name}"
    texture = unreal.EditorAssetLibrary.load_asset(path)
    if not texture:
        raise RuntimeError(f"Missing texture: {path}")
    unreal.log_warning(
        "MAHJONG50_ENHANCED_TEXTURE "
        f"name={texture_name} size={texture.blueprint_get_size_x()}x{texture.blueprint_get_size_y()} "
        f"mip={texture.get_editor_property('mip_gen_settings')} "
        f"lod_bias={texture.get_editor_property('lod_bias')} "
        f"srgb={texture.get_editor_property('srgb')} "
        f"compression={texture.get_editor_property('compression_settings')}"
    )

unreal.log_warning("MAHJONG50_ENHANCEMENT_INSPECTION_OK")
