"""Validate the imported Mahjong50 distance-readability texture policy."""

from __future__ import annotations

import unreal


TEXTURE_ROOT = "/Game/Art/Mahjong/Mahjong50/Textures"
VISIBLE_ATLAS_TEXTURES = (
    "T_Mahjong50_FaceAtlas_BaseColor",
    "T_Mahjong50_FaceAtlas_GlyphMask",
    "T_Mahjong50_FaceAtlas_EngraveMask",
)
PBR_ATLAS_TEXTURES = (
    "T_Mahjong50_FaceAtlas_Normal",
    "T_Mahjong50_FaceAtlas_Height",
    "T_Mahjong50_FaceAtlas_ORM",
)


def load_texture(name: str):
    texture = unreal.EditorAssetLibrary.load_asset(f"{TEXTURE_ROOT}/{name}")
    if texture is None:
        raise RuntimeError(f"Missing texture: {name}")
    return texture


def main() -> None:
    for name in VISIBLE_ATLAS_TEXTURES:
        texture = load_texture(name)
        lod_bias = int(texture.get_editor_property("lod_bias"))
        mip_setting = str(texture.get_editor_property("mip_gen_settings")).upper()
        if lod_bias != -1:
            raise RuntimeError(f"{name} expected LOD Bias -1, found {lod_bias}")
        if "SHARPEN8" not in mip_setting:
            raise RuntimeError(
                f"{name} expected Sharpen8 mip generation, found {mip_setting}"
            )

    for name in PBR_ATLAS_TEXTURES:
        texture = load_texture(name)
        lod_bias = int(texture.get_editor_property("lod_bias"))
        if lod_bias != 0:
            raise RuntimeError(f"{name} expected LOD Bias 0, found {lod_bias}")

    unreal.log(
        "[Mahjong50Distance] DISTANT_READABILITY_VALIDATION_OK "
        "visible_atlas_lod_bias=-1 visible_atlas_mip=Sharpen8 "
        "pbr_atlas_lod_bias=0 glyph_coverage_boost=1.25"
    )


if __name__ == "__main__":
    main()
