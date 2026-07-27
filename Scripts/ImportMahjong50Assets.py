"""Import the F:/TT Mahjong50 PBR set and build all 34 playable tile assets."""

from __future__ import annotations

import json
from pathlib import Path

import unreal


SOURCE_ROOT = Path("F:/TT")
MODEL_FILE = SOURCE_ROOT / "Model" / "SM_Mahjong50.fbx"
TEXTURE_SOURCE = SOURCE_ROOT / "Textures"
INDEX_FILE = TEXTURE_SOURCE / "Mahjong50_FaceAtlas_Index.json"

DEST_ROOT = "/Game/Art/Mahjong/Mahjong50"
MESH_DEST = f"{DEST_ROOT}/Meshes"
TEXTURE_DEST = f"{DEST_ROOT}/Textures"
MATERIAL_DEST = f"{DEST_ROOT}/Materials"
INSTANCE_DEST = f"{DEST_ROOT}/MaterialInstances"
TILE_DEST = f"{DEST_ROOT}/Tiles"

BASE_MESH_PATH = f"{MESH_DEST}/SM_Mahjong50"
UNIFIED_MATERIAL_PATH = f"{MATERIAL_DEST}/M_Mahjong50_TileUnified"

DISTANT_GLYPH_LOD_BIAS = -1
DISTANT_GLYPH_COVERAGE_BOOST = 1.25
AUTHORING_ONLY_TEXTURE_STEMS = {
    "T_Mahjong50_FaceAtlas_AO",
    "T_Mahjong50_FaceAtlas_Roughness",
}


def log(message: str) -> None:
    unreal.log(f"[Mahjong50Import] {message}")


def warn(message: str) -> None:
    unreal.log_warning(f"[Mahjong50Import] {message}")


def set_prop(obj, name: str, value) -> bool:
    try:
        obj.set_editor_property(name, value)
        return True
    except Exception as exc:
        warn(f"skip property {obj.get_class().get_name()}.{name}: {exc}")
        return False


def ensure_sources() -> list[Path]:
    required = [MODEL_FILE, INDEX_FILE]
    source_texture_files = sorted(TEXTURE_SOURCE.glob("T_Mahjong50_*.png"))
    required.extend(source_texture_files)
    missing = [str(path) for path in required if not path.is_file()]
    if missing:
        raise RuntimeError("Missing source files: " + ", ".join(missing))
    if len(source_texture_files) != 14:
        raise RuntimeError(
            f"Expected 14 authoring PNG textures, found {len(source_texture_files)}"
        )
    runtime_texture_files = [
        path
        for path in source_texture_files
        if path.stem not in AUTHORING_ONLY_TEXTURE_STEMS
    ]
    if len(runtime_texture_files) != 12:
        raise RuntimeError(
            f"Expected 12 runtime PNG textures, found {len(runtime_texture_files)}"
        )
    return runtime_texture_files


def delete_old_asset_set() -> None:
    """Delete only the generated Mahjong50 set before a clean replacement."""
    if unreal.EditorAssetLibrary.does_directory_exist(DEST_ROOT):
        old_assets = unreal.EditorAssetLibrary.list_assets(
            DEST_ROOT, recursive=True, include_folder=False
        )
        log(f"deleting {len(old_assets)} old assets under {DEST_ROOT}")
        for asset_path in sorted(old_assets, reverse=True):
            if not unreal.EditorAssetLibrary.delete_asset(asset_path):
                raise RuntimeError(f"Could not delete old target asset: {asset_path}")
        for directory in (
            TILE_DEST,
            INSTANCE_DEST,
            MATERIAL_DEST,
            TEXTURE_DEST,
            MESH_DEST,
            DEST_ROOT,
        ):
            if unreal.EditorAssetLibrary.does_directory_exist(directory):
                unreal.EditorAssetLibrary.delete_directory(directory)
    else:
        log("no previous Mahjong50 asset directory found")

    for directory in (
        DEST_ROOT,
        MESH_DEST,
        TEXTURE_DEST,
        MATERIAL_DEST,
        INSTANCE_DEST,
        TILE_DEST,
    ):
        unreal.EditorAssetLibrary.make_directory(directory)


def import_model():
    options = unreal.FbxImportUI()
    set_prop(options, "import_as_skeletal", False)
    set_prop(options, "mesh_type_to_import", unreal.FBXImportType.FBXIT_STATIC_MESH)
    set_prop(options, "import_materials", False)
    set_prop(options, "import_textures", False)
    set_prop(options, "automated_import_should_detect_type", False)

    static_data = options.get_editor_property("static_mesh_import_data")
    set_prop(static_data, "import_uniform_scale", 1.0)
    set_prop(static_data, "combine_meshes", False)
    set_prop(static_data, "auto_generate_collision", False)
    set_prop(static_data, "generate_lightmap_u_vs", True)
    set_prop(static_data, "convert_scene", True)
    set_prop(static_data, "force_front_x_axis", False)
    set_prop(
        static_data,
        "normal_import_method",
        unreal.FBXNormalImportMethod.FBXNIM_IMPORT_NORMALS_AND_TANGENTS,
    )
    if hasattr(unreal, "VertexColorImportOption"):
        set_prop(static_data, "vertex_color_import_option", unreal.VertexColorImportOption.REPLACE)

    task = unreal.AssetImportTask()
    task.filename = str(MODEL_FILE)
    task.destination_path = MESH_DEST
    task.destination_name = "SM_Mahjong50"
    task.automated = True
    task.replace_existing = False
    task.save = True
    task.options = options
    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])

    mesh = unreal.EditorAssetLibrary.load_asset(BASE_MESH_PATH)
    if not mesh:
        candidates = unreal.EditorAssetLibrary.list_assets(MESH_DEST, recursive=False, include_folder=False)
        raise RuntimeError(f"FBX import did not create {BASE_MESH_PATH}; imported={candidates}")
    return mesh


def import_texture(source: Path):
    task = unreal.AssetImportTask()
    task.filename = str(source)
    task.destination_path = TEXTURE_DEST
    task.destination_name = source.stem
    task.automated = True
    task.replace_existing = False
    task.save = True
    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])
    texture = unreal.EditorAssetLibrary.load_asset(f"{TEXTURE_DEST}/{source.stem}")
    if not texture:
        raise RuntimeError(f"Texture import failed: {source}")
    return texture


def configure_texture(texture, name: str) -> None:
    is_normal = name.endswith("_Normal")
    is_mask = (
        name.endswith("_ORM")
        or name.endswith("_AO")
        or name.endswith("_Roughness")
        or name.endswith("_Height")
        or name.endswith("_GlyphMask")
        or name.endswith("_EngraveMask")
    )
    is_face_atlas = "_FaceAtlas_" in name
    is_visible_face_atlas = is_face_atlas and (
        name.endswith("_BaseColor")
        or name.endswith("_GlyphMask")
        or name.endswith("_EngraveMask")
    )
    set_prop(texture, "srgb", not (is_normal or is_mask))
    if is_normal:
        set_prop(texture, "compression_settings", unreal.TextureCompressionSettings.TC_NORMALMAP)
    elif is_mask:
        set_prop(texture, "compression_settings", unreal.TextureCompressionSettings.TC_MASKS)
    set_prop(
        texture,
        "lod_group",
        unreal.TextureGroup.TEXTUREGROUP_CHARACTER if is_face_atlas else unreal.TextureGroup.TEXTUREGROUP_WORLD,
    )
    if is_face_atlas:
        # Visible glyph maps use one higher mip and stronger sharpening so
        # strokes remain readable when a tile covers only a few dozen pixels.
        # PBR support maps keep neutral LOD to limit mobile streaming cost.
        sharpen_name = "TMGS_SHARPEN8" if is_visible_face_atlas else "TMGS_SHARPEN6"
        sharpen = getattr(
            unreal.TextureMipGenSettings,
            sharpen_name,
            unreal.TextureMipGenSettings.TMGS_SHARPEN4,
        )
        set_prop(texture, "mip_gen_settings", sharpen)
        # UE 5.8 selects anisotropy through the texture group and r.MaxAnisotropy.
        set_prop(texture, "filter", unreal.TextureFilter.TF_DEFAULT)
        set_prop(
            texture,
            "lod_bias",
            DISTANT_GLYPH_LOD_BIAS if is_visible_face_atlas else 0,
        )
        texture_address = getattr(unreal, "TextureAddress", None)
        if texture_address is not None:
            clamp = getattr(texture_address, "TA_CLAMP", None)
            if clamp is not None:
                set_prop(texture, "address_x", clamp)
                set_prop(texture, "address_y", clamp)
    post_edit_change = getattr(texture, "post_edit_change", None)
    if post_edit_change:
        post_edit_change()
    unreal.EditorAssetLibrary.save_loaded_asset(texture, only_if_is_dirty=False)


def load_texture(name: str):
    texture = unreal.EditorAssetLibrary.load_asset(f"{TEXTURE_DEST}/{name}")
    if not texture:
        raise RuntimeError(f"Missing imported texture: {name}")
    return texture


def create_material(name: str):
    path = f"{MATERIAL_DEST}/{name}"
    material = unreal.AssetToolsHelpers.get_asset_tools().create_asset(
        name, MATERIAL_DEST, unreal.Material, unreal.MaterialFactoryNew()
    )
    if not material:
        raise RuntimeError(f"Could not create material {path}")
    set_prop(material, "two_sided", False)
    return material


def expr(material, cls, x: int, y: int):
    return unreal.MaterialEditingLibrary.create_material_expression(material, cls, x, y)


def texture_sample(material, texture, parameter_name: str, x: int, y: int, sampler_type=None):
    node = expr(material, unreal.MaterialExpressionTextureSampleParameter2D, x, y)
    set_prop(node, "parameter_name", parameter_name)
    set_prop(node, "texture", texture)
    if sampler_type is not None:
        set_prop(node, "sampler_type", sampler_type)
    return node


def connect(a, output_name: str, b, input_name: str = "") -> None:
    if not unreal.MaterialEditingLibrary.connect_material_expressions(a, output_name, b, input_name):
        raise RuntimeError(
            f"Failed material connection {a.get_class().get_name()}.{output_name} -> "
            f"{b.get_class().get_name()}.{input_name}"
        )


def build_body_material():
    material = create_material("M_Mahjong50_BodyBlend")
    vertex_color = expr(material, unreal.MaterialExpressionVertexColor, -1050, 0)

    specular = expr(material, unreal.MaterialExpressionConstant, -260, 500)
    set_prop(specular, "r", 0.46)
    unreal.MaterialEditingLibrary.connect_material_property(
        specular, "", unreal.MaterialProperty.MP_SPECULAR
    )

    ivory_bc = texture_sample(material, load_texture("T_Mahjong50_Ivory_BaseColor"), "IvoryBaseColor", -1050, -430)
    green_bc = texture_sample(material, load_texture("T_Mahjong50_GreenWrap_BaseColor"), "GreenBaseColor", -1050, -310)
    bc_lerp = expr(material, unreal.MaterialExpressionLinearInterpolate, -650, -360)
    connect(ivory_bc, "", bc_lerp, "A")
    connect(green_bc, "", bc_lerp, "B")
    connect(vertex_color, "R", bc_lerp, "Alpha")
    unreal.MaterialEditingLibrary.connect_material_property(bc_lerp, "", unreal.MaterialProperty.MP_BASE_COLOR)

    ivory_n = texture_sample(
        material, load_texture("T_Mahjong50_Ivory_Normal"), "IvoryNormal", -1050, -160,
        unreal.MaterialSamplerType.SAMPLERTYPE_NORMAL,
    )
    green_n = texture_sample(
        material, load_texture("T_Mahjong50_GreenWrap_Normal"), "GreenNormal", -1050, -40,
        unreal.MaterialSamplerType.SAMPLERTYPE_NORMAL,
    )
    normal_lerp = expr(material, unreal.MaterialExpressionLinearInterpolate, -650, -100)
    connect(ivory_n, "", normal_lerp, "A")
    connect(green_n, "", normal_lerp, "B")
    connect(vertex_color, "R", normal_lerp, "Alpha")
    unreal.MaterialEditingLibrary.connect_material_property(normal_lerp, "", unreal.MaterialProperty.MP_NORMAL)

    ivory_orm = texture_sample(
        material, load_texture("T_Mahjong50_Ivory_ORM"), "IvoryORM", -1050, 220,
        unreal.MaterialSamplerType.SAMPLERTYPE_MASKS,
    )
    green_orm = texture_sample(
        material, load_texture("T_Mahjong50_GreenWrap_ORM"), "GreenORM", -1050, 340,
        unreal.MaterialSamplerType.SAMPLERTYPE_MASKS,
    )
    orm_lerp = expr(material, unreal.MaterialExpressionLinearInterpolate, -650, 280)
    connect(ivory_orm, "", orm_lerp, "A")
    connect(green_orm, "", orm_lerp, "B")
    connect(vertex_color, "R", orm_lerp, "Alpha")
    unreal.MaterialEditingLibrary.connect_material_property(orm_lerp, "R", unreal.MaterialProperty.MP_AMBIENT_OCCLUSION)
    unreal.MaterialEditingLibrary.connect_material_property(orm_lerp, "G", unreal.MaterialProperty.MP_ROUGHNESS)
    unreal.MaterialEditingLibrary.connect_material_property(orm_lerp, "B", unreal.MaterialProperty.MP_METALLIC)

    unreal.MaterialEditingLibrary.recompile_material(material)
    unreal.EditorAssetLibrary.save_loaded_asset(material, only_if_is_dirty=False)
    return material


def scalar_parameter(material, name: str, default: float, x: int, y: int):
    node = expr(material, unreal.MaterialExpressionScalarParameter, x, y)
    set_prop(node, "parameter_name", name)
    set_prop(node, "default_value", default)
    return node


def constant2(material, x_value: float, y_value: float, x: int, y: int):
    node = expr(material, unreal.MaterialExpressionConstant2Vector, x, y)
    set_prop(node, "r", x_value)
    set_prop(node, "g", y_value)
    return node


def build_face_material():
    material = create_material("M_Mahjong50_TileUnified")
    vertex_color = expr(material, unreal.MaterialExpressionVertexColor, -1450, -520)

    specular = expr(material, unreal.MaterialExpressionConstant, 180, 560)
    set_prop(specular, "r", 0.46)
    unreal.MaterialEditingLibrary.connect_material_property(
        specular, "", unreal.MaterialProperty.MP_SPECULAR
    )

    texcoord = expr(material, unreal.MaterialExpressionTextureCoordinate, -1450, -200)
    uv_scale = constant2(material, 704.0 / 8192.0, 1024.0 / 4096.0, -1450, -80)
    uv_scaled = expr(material, unreal.MaterialExpressionMultiply, -1160, -150)
    connect(texcoord, "", uv_scaled, "A")
    connect(uv_scale, "", uv_scaled, "B")

    column = scalar_parameter(material, "Column", 3.0, -1450, 100)
    column_scale = expr(material, unreal.MaterialExpressionMultiply, -1160, 80)
    set_prop(column_scale, "const_b", 896.0 / 8192.0)
    connect(column, "", column_scale, "A")
    u_offset = expr(material, unreal.MaterialExpressionAdd, -900, 80)
    set_prop(u_offset, "const_b", 160.0 / 8192.0)
    connect(column_scale, "", u_offset, "A")

    row = scalar_parameter(material, "RowFromBottom", 0.0, -1450, 240)
    row_scale = expr(material, unreal.MaterialExpressionMultiply, -1160, 230)
    set_prop(row_scale, "const_b", 1024.0 / 4096.0)
    connect(row, "", row_scale, "A")

    uv_offset = expr(material, unreal.MaterialExpressionAppendVector, -650, 140)
    connect(u_offset, "", uv_offset, "A")
    connect(row_scale, "", uv_offset, "B")
    atlas_uv = expr(material, unreal.MaterialExpressionAdd, -400, -80)
    connect(uv_scaled, "", atlas_uv, "A")
    connect(uv_offset, "", atlas_uv, "B")

    base_color = texture_sample(
        material, load_texture("T_Mahjong50_FaceAtlas_BaseColor"), "FaceBaseColor", -100, -330
    )
    glyph_mask = texture_sample(
        material, load_texture("T_Mahjong50_FaceAtlas_GlyphMask"), "FaceGlyphMask", -100, -230,
        unreal.MaterialSamplerType.SAMPLERTYPE_MASKS,
    )
    engrave_mask = texture_sample(
        material, load_texture("T_Mahjong50_FaceAtlas_EngraveMask"), "FaceEngraveMask", -100, -130,
        unreal.MaterialSamplerType.SAMPLERTYPE_MASKS,
    )
    normal = texture_sample(
        material, load_texture("T_Mahjong50_FaceAtlas_Normal"), "FaceNormal", -100, 0,
        unreal.MaterialSamplerType.SAMPLERTYPE_NORMAL,
    )
    height = texture_sample(
        material, load_texture("T_Mahjong50_FaceAtlas_Height"), "FaceHeight", -100, 130,
        unreal.MaterialSamplerType.SAMPLERTYPE_MASKS,
    )
    orm = texture_sample(
        material, load_texture("T_Mahjong50_FaceAtlas_ORM"), "FaceORM", -100, 360,
        unreal.MaterialSamplerType.SAMPLERTYPE_MASKS,
    )

    # Keep the visible glyph, masks and PBR values on the authored atlas UV.
    # Applying view-dependent parallax to these samples tears thin strokes
    # and can cross into neighbouring atlas cells at oblique camera angles.
    for sample in (base_color, glyph_mask, engrave_mask, orm):
        connect(atlas_uv, "", sample, "UVs")

    # Retain only a very small recessed offset for the engraved normal. The
    # previous 0.042 full-atlas ratio could move hundreds of pixels at a
    # grazing angle; 0.0015 stays within the cell artwork margin.
    connect(atlas_uv, "", height, "UVs")
    bump = expr(material, unreal.MaterialExpressionBumpOffset, 180, 20)
    set_prop(bump, "height_ratio", 0.0015)
    set_prop(bump, "reference_plane", 0.5)
    connect(atlas_uv, "", bump, "Coordinate")
    connect(height, "R", bump, "Height")
    connect(bump, "", normal, "UVs")

    cavity_color = expr(material, unreal.MaterialExpressionMultiply, 420, -300)
    connect(base_color, "", cavity_color, "A")
    connect(orm, "R", cavity_color, "B")

    # The atlas contributes only glyph pixels. Everywhere else, including the
    # entire face boundary, uses the exact same ivory PBR maps as the body.
    ivory_bc = texture_sample(
        material, load_texture("T_Mahjong50_Ivory_BaseColor"), "IvoryBaseColor", 180, -520
    )
    green_bc = texture_sample(
        material, load_texture("T_Mahjong50_GreenWrap_BaseColor"), "GreenBaseColor", 180, -620
    )
    body_base = expr(material, unreal.MaterialExpressionLinearInterpolate, 430, -560)
    connect(ivory_bc, "", body_base, "A")
    connect(green_bc, "", body_base, "B")
    connect(vertex_color, "R", body_base, "Alpha")
    glyph_coverage = scalar_parameter(
        material,
        "GlyphCoverageBoost",
        DISTANT_GLYPH_COVERAGE_BOOST,
        180,
        -430,
    )
    boosted_glyph = expr(material, unreal.MaterialExpressionMultiply, 400, -430)
    connect(glyph_mask, "R", boosted_glyph, "A")
    connect(glyph_coverage, "", boosted_glyph, "B")
    clamped_glyph = expr(material, unreal.MaterialExpressionSaturate, 570, -430)
    connect(boosted_glyph, "", clamped_glyph, "")
    glyph_factor = expr(material, unreal.MaterialExpressionMultiply, 730, -420)
    connect(clamped_glyph, "", glyph_factor, "A")
    connect(vertex_color, "G", glyph_factor, "B")
    seamless_base = expr(material, unreal.MaterialExpressionLinearInterpolate, 900, -370)
    connect(body_base, "", seamless_base, "A")
    connect(cavity_color, "", seamless_base, "B")
    connect(glyph_factor, "", seamless_base, "Alpha")
    unreal.MaterialEditingLibrary.connect_material_property(
        seamless_base, "", unreal.MaterialProperty.MP_BASE_COLOR
    )

    ivory_n = texture_sample(
        material, load_texture("T_Mahjong50_Ivory_Normal"), "IvoryNormal", 180, -100,
        unreal.MaterialSamplerType.SAMPLERTYPE_NORMAL,
    )
    green_n = texture_sample(
        material, load_texture("T_Mahjong50_GreenWrap_Normal"), "GreenNormal", 180, -10,
        unreal.MaterialSamplerType.SAMPLERTYPE_NORMAL,
    )
    body_normal = expr(material, unreal.MaterialExpressionLinearInterpolate, 430, -40)
    connect(ivory_n, "", body_normal, "A")
    connect(green_n, "", body_normal, "B")
    connect(vertex_color, "R", body_normal, "Alpha")
    engraving_factor = expr(material, unreal.MaterialExpressionMultiply, 430, 70)
    connect(engrave_mask, "R", engraving_factor, "A")
    connect(vertex_color, "G", engraving_factor, "B")
    seamless_normal = expr(material, unreal.MaterialExpressionLinearInterpolate, 700, -80)
    connect(body_normal, "", seamless_normal, "A")
    connect(normal, "", seamless_normal, "B")
    connect(engraving_factor, "", seamless_normal, "Alpha")
    unreal.MaterialEditingLibrary.connect_material_property(
        seamless_normal, "", unreal.MaterialProperty.MP_NORMAL
    )

    ivory_orm = texture_sample(
        material, load_texture("T_Mahjong50_Ivory_ORM"), "IvoryORM", 180, 300,
        unreal.MaterialSamplerType.SAMPLERTYPE_MASKS,
    )
    green_orm = texture_sample(
        material, load_texture("T_Mahjong50_GreenWrap_ORM"), "GreenORM", 180, 410,
        unreal.MaterialSamplerType.SAMPLERTYPE_MASKS,
    )
    body_roughness = expr(material, unreal.MaterialExpressionLinearInterpolate, 430, 300)
    connect(ivory_orm, "G", body_roughness, "A")
    connect(green_orm, "G", body_roughness, "B")
    connect(vertex_color, "R", body_roughness, "Alpha")
    body_ao = expr(material, unreal.MaterialExpressionLinearInterpolate, 430, 410)
    connect(ivory_orm, "R", body_ao, "A")
    connect(green_orm, "R", body_ao, "B")
    connect(vertex_color, "R", body_ao, "Alpha")
    seamless_roughness = expr(material, unreal.MaterialExpressionLinearInterpolate, 700, 210)
    connect(body_roughness, "", seamless_roughness, "A")
    connect(orm, "G", seamless_roughness, "B")
    connect(engraving_factor, "", seamless_roughness, "Alpha")
    unreal.MaterialEditingLibrary.connect_material_property(
        seamless_roughness, "", unreal.MaterialProperty.MP_ROUGHNESS
    )

    seamless_ao = expr(material, unreal.MaterialExpressionLinearInterpolate, 700, 390)
    connect(body_ao, "", seamless_ao, "A")
    connect(orm, "R", seamless_ao, "B")
    connect(engraving_factor, "", seamless_ao, "Alpha")
    unreal.MaterialEditingLibrary.connect_material_property(
        seamless_ao, "", unreal.MaterialProperty.MP_AMBIENT_OCCLUSION
    )
    metallic = expr(material, unreal.MaterialExpressionConstant, 700, 540)
    set_prop(metallic, "r", 0.0)
    unreal.MaterialEditingLibrary.connect_material_property(
        metallic, "", unreal.MaterialProperty.MP_METALLIC
    )

    unreal.MaterialEditingLibrary.recompile_material(material)
    unreal.EditorAssetLibrary.save_loaded_asset(material, only_if_is_dirty=False)
    return material


def create_face_instance(tile: dict, face_material):
    name = f"MI_Mahjong50_{tile['name']}"
    path = f"{INSTANCE_DEST}/{name}"
    instance = unreal.AssetToolsHelpers.get_asset_tools().create_asset(
        name, INSTANCE_DEST, unreal.MaterialInstanceConstant, unreal.MaterialInstanceConstantFactoryNew()
    )
    if not instance:
        raise RuntimeError(f"Could not create {path}")
    unreal.MaterialEditingLibrary.set_material_instance_parent(instance, face_material)
    unreal.MaterialEditingLibrary.set_material_instance_scalar_parameter_value(instance, "Column", float(tile["column"]))
    unreal.MaterialEditingLibrary.set_material_instance_scalar_parameter_value(
        instance, "RowFromBottom", float(tile["row_from_bottom"])
    )
    unreal.EditorAssetLibrary.save_loaded_asset(instance, only_if_is_dirty=False)
    return instance


def material_slot_names(mesh) -> list[str]:
    names = []
    for slot in mesh.get_editor_property("static_materials"):
        imported = slot.get_editor_property("imported_material_slot_name")
        current = slot.get_editor_property("material_slot_name")
        names.append(str(imported or current))
    return names


def assign_tile_materials(mesh, tile_instance) -> None:
    slots = material_slot_names(mesh)
    if len(slots) != 1:
        raise RuntimeError(f"Expected one unified material slot, found {slots}")
    mesh.set_material(0, tile_instance)
    post_edit_change = getattr(mesh, "post_edit_change", None)
    if post_edit_change:
        post_edit_change()
    unreal.EditorAssetLibrary.save_loaded_asset(mesh, only_if_is_dirty=False)


def create_tile_mesh(tile: dict, base_mesh, tile_instance):
    name = f"SM_Mahjong50_{tile['name']}"
    path = f"{TILE_DEST}/{name}"
    tile_mesh = unreal.EditorAssetLibrary.duplicate_asset(BASE_MESH_PATH, path)
    if not tile_mesh:
        raise RuntimeError(f"Could not create tile mesh {path}")
    assign_tile_materials(tile_mesh, tile_instance)
    return tile_mesh


def validate(base_mesh, tile_specs: list[dict], textures: list) -> None:
    active_tiles = [tile for tile in tile_specs if not tile.get("reserved", False)]
    if len(active_tiles) != 34:
        raise RuntimeError(f"Expected 34 active tile definitions, found {len(active_tiles)}")
    if len(textures) != 12:
        raise RuntimeError(f"Expected 12 runtime textures, found {len(textures)}")
    slots = material_slot_names(base_mesh)
    if len(slots) != 1 or "unified" not in slots[0].lower():
        raise RuntimeError(f"Expected one unified material slot on base mesh, found {slots}")
    bounds = base_mesh.get_bounds()
    size = bounds.box_extent * 2.0
    expected = sorted([3.6, 2.6, 5.0])
    actual = sorted([size.x, size.y, size.z])
    if any(abs(a - e) > 0.35 for a, e in zip(actual, expected)):
        raise RuntimeError(f"Unexpected mesh dimensions cm: ({size.x:.3f}, {size.y:.3f}, {size.z:.3f})")
    for tile in active_tiles:
        mesh_path = f"{TILE_DEST}/SM_Mahjong50_{tile['name']}"
        mi_path = f"{INSTANCE_DEST}/MI_Mahjong50_{tile['name']}"
        if not unreal.EditorAssetLibrary.does_asset_exist(mesh_path):
            raise RuntimeError(f"Missing tile mesh {mesh_path}")
        if not unreal.EditorAssetLibrary.does_asset_exist(mi_path):
            raise RuntimeError(f"Missing face material instance {mi_path}")
    log(
        f"validated mesh dimensions=({size.x:.3f}, {size.y:.3f}, {size.z:.3f}) cm, "
        f"slots={slots}, textures=12, tile variants=34"
    )


def main() -> None:
    log(f"source={SOURCE_ROOT} destination={DEST_ROOT}")
    texture_files = ensure_sources()
    index_data = json.loads(INDEX_FILE.read_text(encoding="utf-8-sig"))
    orientation = index_data.get("authoring", {}).get("glyph_orientation")
    if orientation != "source-unmirrored; left-to-right; no runtime UV flip":
        raise RuntimeError(f"Unexpected face-atlas orientation metadata: {orientation!r}")
    tile_specs = [tile for tile in index_data["tiles"] if not tile.get("reserved", False)]

    delete_old_asset_set()
    base_mesh = import_model()
    imported_textures = []
    for source in texture_files:
        texture = import_texture(source)
        configure_texture(texture, source.stem)
        imported_textures.append(texture)

    unified_material = build_face_material()

    face_instances = {
        tile["name"]: create_face_instance(tile, unified_material)
        for tile in tile_specs
    }
    assign_tile_materials(
        base_mesh,
        face_instances["Red_Dragon"],
    )
    for tile in tile_specs:
        create_tile_mesh(
            tile,
            base_mesh,
            face_instances[tile["name"]],
        )

    validate(base_mesh, tile_specs, imported_textures)
    unreal.EditorAssetLibrary.save_directory(DEST_ROOT, only_if_is_dirty=False, recursive=True)
    log(
        "distance readability policy="
        f"visible_atlas_lod_bias={DISTANT_GLYPH_LOD_BIAS}, "
        "visible_atlas_mip=Sharpen8, pbr_atlas_lod_bias=0, "
        f"glyph_coverage_boost={DISTANT_GLYPH_COVERAGE_BOOST:.2f}"
    )
    log("MAHJONG50_IMPORT_OK")


if __name__ == "__main__":
    main()
