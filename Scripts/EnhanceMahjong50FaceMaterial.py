"""Apply the sharper engraved-face material to the imported Mahjong50 assets."""

from pathlib import Path
import sys

import unreal


sys.path.insert(0, str(Path(__file__).resolve().parent))
from ImportMahjong50Assets import (  # noqa: E402
    DEST_ROOT,
    INSTANCE_DEST,
    build_face_material,
    configure_texture,
    load_texture,
)


FACE_TEXTURES = (
    "T_Mahjong50_FaceAtlas_BaseColor",
    "T_Mahjong50_FaceAtlas_GlyphMask",
    "T_Mahjong50_FaceAtlas_EngraveMask",
    "T_Mahjong50_FaceAtlas_Normal",
    "T_Mahjong50_FaceAtlas_Height",
    "T_Mahjong50_FaceAtlas_ORM",
)


for texture_name in FACE_TEXTURES:
    texture = load_texture(texture_name)
    configure_texture(texture, texture_name)

material = build_face_material()
for asset_path in unreal.EditorAssetLibrary.list_assets(
    INSTANCE_DEST, recursive=False, include_folder=False
):
    instance = unreal.EditorAssetLibrary.load_asset(asset_path)
    if isinstance(instance, unreal.MaterialInstanceConstant):
        unreal.MaterialEditingLibrary.set_material_instance_parent(instance, material)
        unreal.EditorAssetLibrary.save_loaded_asset(
            instance, only_if_is_dirty=False
        )
unreal.EditorAssetLibrary.save_directory(
    DEST_ROOT, only_if_is_dirty=False, recursive=True
)
unreal.log(
    "[Mahjong50Enhance] MAHJONG50_FACE_ENHANCEMENT_OK "
    f"material={material.get_path_name()} normal_strength=2.15 "
    "parallax_depth=0.0035 mip=Sharpen8 lod_bias=-1"
)
