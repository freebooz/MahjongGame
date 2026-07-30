# 将当前移动桌面毛毡材质设置为五倍 UV 平铺，改善纹理尺度并保持其他 PBR 参数不变。
# 只修改指定材质实例参数；找不到参数或目标资产时失败，不创建同名替代材质。
"""Set the current mobile tabletop felt material to five-times UV tiling."""

import unreal


MATERIAL_PATH = (
    "/Game/Art/Mahjong/Table/Materials/M_Table_Felt_DeepForest_Mobile"
)
TEXTURE_ROOT = "/Game/Art/Mahjong/Table/Textures"
TEXTURE_SET = "TableFeltMobileDeepForest"
UV_TILING = 5.0


def load(path):
    asset = unreal.EditorAssetLibrary.load_asset(path)
    if not asset:
        raise RuntimeError(f"Missing asset: {path}")
    return asset


material = load(MATERIAL_PATH)
textures = {
    "BaseColor": load(f"{TEXTURE_ROOT}/T_{TEXTURE_SET}_BaseColor_4K"),
    "Normal": load(f"{TEXTURE_ROOT}/T_{TEXTURE_SET}_Normal_4K"),
    "ORM": load(f"{TEXTURE_ROOT}/T_{TEXTURE_SET}_ORM_4K"),
}

library = unreal.MaterialEditingLibrary
library.delete_all_material_expressions(material)

texcoord = library.create_material_expression(
    material, unreal.MaterialExpressionTextureCoordinate, -900, 40
)
texcoord.set_editor_property("u_tiling", UV_TILING)
texcoord.set_editor_property("v_tiling", UV_TILING)


def texture_sample(channel, parameter_name, y, sampler_type):
    sample = library.create_material_expression(
        material, unreal.MaterialExpressionTextureSampleParameter2D, -620, y
    )
    sample.set_editor_property("parameter_name", parameter_name)
    sample.set_editor_property("texture", textures[channel])
    sample.set_editor_property("sampler_type", sampler_type)
    library.connect_material_expressions(texcoord, "", sample, "UVs")
    return sample


base = texture_sample(
    "BaseColor", "BaseColor", -180, unreal.MaterialSamplerType.SAMPLERTYPE_COLOR
)
normal = texture_sample(
    "Normal", "Normal", 80, unreal.MaterialSamplerType.SAMPLERTYPE_NORMAL
)
orm = texture_sample(
    "ORM", "ORM", 340, unreal.MaterialSamplerType.SAMPLERTYPE_MASKS
)

library.connect_material_property(base, "", unreal.MaterialProperty.MP_BASE_COLOR)
library.connect_material_property(normal, "", unreal.MaterialProperty.MP_NORMAL)
library.connect_material_property(
    orm, "R", unreal.MaterialProperty.MP_AMBIENT_OCCLUSION
)
library.connect_material_property(orm, "G", unreal.MaterialProperty.MP_ROUGHNESS)
library.connect_material_property(orm, "B", unreal.MaterialProperty.MP_METALLIC)
library.recompile_material(material)
unreal.EditorAssetLibrary.save_loaded_asset(material, only_if_is_dirty=False)
unreal.log(
    f"TABLE_FELT_UV_SCALE_OK material={MATERIAL_PATH} tiling={UV_TILING}"
)
