# 为已导入 Mahjong50 图集设置运行时纹理质量、压缩和流送参数，不重新导入网格。
# 只修改目标纹理资产；参数必须兼顾移动端显存与远距离可读性，保存失败时返回错误。
"""Apply the runtime quality preset to the already imported Mahjong50 face atlas."""

import unreal


ROOT = "/Game/Art/Mahjong/Mahjong50/Textures"
VISIBLE_NAMES = (
    "T_Mahjong50_FaceAtlas_BaseColor",
    "T_Mahjong50_FaceAtlas_GlyphMask",
    "T_Mahjong50_FaceAtlas_EngraveMask",
)
PBR_NAMES = (
    "T_Mahjong50_FaceAtlas_Normal",
    "T_Mahjong50_FaceAtlas_Height",
    "T_Mahjong50_FaceAtlas_ORM",
)


for name in VISIBLE_NAMES + PBR_NAMES:
    texture = unreal.EditorAssetLibrary.load_asset(f"{ROOT}/{name}")
    if not texture:
        raise RuntimeError(f"Missing Mahjong50 atlas texture: {name}")
    texture.set_editor_property("lod_group", unreal.TextureGroup.TEXTUREGROUP_CHARACTER)
    is_visible = name in VISIBLE_NAMES
    texture.set_editor_property(
        "mip_gen_settings",
        (
            unreal.TextureMipGenSettings.TMGS_SHARPEN8
            if is_visible
            else unreal.TextureMipGenSettings.TMGS_SHARPEN6
        ),
    )
    # UE 5.8 selects anisotropy through the texture group and r.MaxAnisotropy.
    texture.set_editor_property("filter", unreal.TextureFilter.TF_DEFAULT)
    texture.set_editor_property("lod_bias", -1 if is_visible else 0)
    post_edit_change = getattr(texture, "post_edit_change", None)
    if post_edit_change:
        post_edit_change()
    unreal.EditorAssetLibrary.save_loaded_asset(texture, only_if_is_dirty=False)
    unreal.log(
        f"[Mahjong50Quality] configured {name} "
        f"size={texture.blueprint_get_size_x()}x{texture.blueprint_get_size_y()}"
    )

unreal.log("[Mahjong50Quality] complete")
