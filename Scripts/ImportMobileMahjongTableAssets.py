"""Clean-import the mobile felt-panel Mahjong table into Unreal Engine 5.8.

The target directory must be physically absent before this script starts.
This prevents same-session delete/recreate file locks and guarantees that the
import is a full replacement rather than an overwrite or Reimport.
"""

from __future__ import annotations

import json
from pathlib import Path

import unreal


PROJECT_ROOT = Path(__file__).resolve().parents[1]
SOURCE_ROOT = (
    PROJECT_ROOT / "SourceArt" / "3D" / "MahjongTableMobileProduction"
)
MODEL_FILE = SOURCE_ROOT / "SM_StandardMahjongTable.fbx"
MODEL_MANIFEST = SOURCE_ROOT / "MahjongTableMobileProductionManifest.json"
TEXTURE_ROOT = SOURCE_ROOT / "Textures"
TEXTURE_MANIFEST = TEXTURE_ROOT / "MahjongTableMobileTextureManifest.json"

DEST_ROOT = "/Game/Art/Mahjong/Table"
MESH_DEST = f"{DEST_ROOT}/Meshes"
TEXTURE_DEST = f"{DEST_ROOT}/Textures"
MATERIAL_DEST = f"{DEST_ROOT}/Materials"
MESH_PATH = f"{MESH_DEST}/SM_StandardMahjongTable"
REPORT_PATH = (
    PROJECT_ROOT / "Saved" / "Reports" / "MobileMahjongTableImportReport.json"
)

EXPECTED_DIMENSIONS_CM = (300.0, 300.0, 5.9)
TRIANGLE_BUDGET = 7000
EXPECTED_TEXTURE_COUNT = 6
EXPECTED_MATERIAL_COUNT = 5


def log(message: str) -> None:
    unreal.log(f"[MobileMahjongTableImport] {message}")


def warn(message: str) -> None:
    unreal.log_warning(f"[MobileMahjongTableImport] {message}")


def set_prop(obj, name: str, value) -> bool:
    try:
        obj.set_editor_property(name, value)
        return True
    except Exception as exc:
        warn(f"skip property {obj.get_class().get_name()}.{name}: {exc}")
        return False


def ensure_sources() -> tuple[dict, dict]:
    for path in (MODEL_FILE, MODEL_MANIFEST, TEXTURE_MANIFEST):
        if not path.is_file():
            raise RuntimeError(f"Missing production source: {path}")
    model_manifest = json.loads(MODEL_MANIFEST.read_text(encoding="utf-8"))
    texture_manifest = json.loads(TEXTURE_MANIFEST.read_text(encoding="utf-8"))
    if model_manifest.get("status") != "approved_for_unreal_import":
        raise RuntimeError("Mobile model is not approved for import")
    if model_manifest.get("outer_size_mm") != [3000.0, 3000.0]:
        raise RuntimeError("Mobile model dimensions manifest is incorrect")
    triangles = int(model_manifest.get("triangle_count", 0))
    if triangles <= 0 or triangles > TRIANGLE_BUDGET:
        raise RuntimeError(f"Source triangle budget exceeded: {triangles}")
    if model_manifest.get("uvless_objects"):
        raise RuntimeError(
            f"UV-less source meshes: {model_manifest['uvless_objects']}"
        )
    material_specs = texture_manifest.get("materials", {})
    if len(material_specs) != EXPECTED_MATERIAL_COUNT:
        raise RuntimeError(
            f"Expected {EXPECTED_MATERIAL_COUNT} materials, "
            f"found {len(material_specs)}"
        )
    if set(model_manifest.get("materials", [])) != set(material_specs):
        raise RuntimeError("FBX slots and material definitions do not match")
    texture_files = sorted(TEXTURE_ROOT.glob("T_*.png"))
    if len(texture_files) != EXPECTED_TEXTURE_COUNT:
        raise RuntimeError(
            f"Expected {EXPECTED_TEXTURE_COUNT} textures, "
            f"found {len(texture_files)}"
        )
    return model_manifest, texture_manifest


def require_empty_destination() -> None:
    assets = []
    if unreal.EditorAssetLibrary.does_directory_exist(DEST_ROOT):
        assets = list(
            unreal.EditorAssetLibrary.list_assets(
                DEST_ROOT,
                recursive=True,
                include_folder=False,
            )
        )
    if assets:
        raise RuntimeError(
            "Target Mahjong table assets still exist; external precise "
            f"pre-clean is required before import: {assets}"
        )
    for path in (DEST_ROOT, MESH_DEST, TEXTURE_DEST, MATERIAL_DEST):
        unreal.EditorAssetLibrary.make_directory(path)


def import_model():
    options = unreal.FbxImportUI()
    set_prop(options, "import_as_skeletal", False)
    set_prop(options, "mesh_type_to_import", unreal.FBXImportType.FBXIT_STATIC_MESH)
    set_prop(options, "import_materials", False)
    set_prop(options, "import_textures", False)
    set_prop(options, "automated_import_should_detect_type", False)

    data = options.get_editor_property("static_mesh_import_data")
    set_prop(data, "import_uniform_scale", 100.0)
    set_prop(data, "combine_meshes", True)
    set_prop(data, "auto_generate_collision", True)
    set_prop(data, "generate_lightmap_u_vs", True)
    set_prop(data, "convert_scene", True)
    set_prop(data, "force_front_x_axis", False)
    set_prop(data, "remove_degenerates", True)
    set_prop(
        data,
        "normal_import_method",
        unreal.FBXNormalImportMethod.FBXNIM_IMPORT_NORMALS_AND_TANGENTS,
    )

    task = unreal.AssetImportTask()
    task.filename = str(MODEL_FILE)
    task.destination_path = MESH_DEST
    task.destination_name = "SM_StandardMahjongTable"
    task.automated = True
    task.replace_existing = False
    task.replace_existing_settings = False
    task.save = True
    task.options = options
    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])

    mesh = unreal.EditorAssetLibrary.load_asset(MESH_PATH)
    if not mesh:
        raise RuntimeError(f"FBX import did not create {MESH_PATH}")
    set_prop(mesh, "allow_cpu_access", False)
    set_prop(mesh, "light_map_resolution", 64)
    try:
        nanite = mesh.get_editor_property("nanite_settings")
        nanite.enabled = False
        set_prop(mesh, "nanite_settings", nanite)
    except Exception as exc:
        warn(f"could not configure Nanite: {exc}")
    return mesh


def import_texture(source: Path):
    task = unreal.AssetImportTask()
    task.filename = str(source)
    task.destination_path = TEXTURE_DEST
    task.destination_name = source.stem
    task.automated = True
    task.replace_existing = False
    task.replace_existing_settings = False
    task.save = True
    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])
    texture = unreal.EditorAssetLibrary.load_asset(
        f"{TEXTURE_DEST}/{source.stem}"
    )
    if not texture:
        raise RuntimeError(f"Texture import failed: {source}")
    return texture


def configure_texture(texture, name: str) -> None:
    is_normal = "_Normal_" in name
    is_orm = "_ORM_" in name
    set_prop(texture, "srgb", not (is_normal or is_orm))
    if is_normal:
        set_prop(
            texture,
            "compression_settings",
            unreal.TextureCompressionSettings.TC_NORMALMAP,
        )
        set_prop(texture, "flip_green_channel", False)
    elif is_orm:
        set_prop(
            texture,
            "compression_settings",
            unreal.TextureCompressionSettings.TC_MASKS,
        )
    max_size = 512 if name.endswith("_512") else 4096
    set_prop(texture, "lod_group", unreal.TextureGroup.TEXTUREGROUP_WORLD)
    set_prop(texture, "max_texture_size", max_size)
    set_prop(texture, "never_stream", False)
    post_edit_change = getattr(texture, "post_edit_change", None)
    if post_edit_change:
        post_edit_change()
    unreal.EditorAssetLibrary.save_loaded_asset(texture, only_if_is_dirty=False)


def expression(material, expression_class, x: int, y: int):
    return unreal.MaterialEditingLibrary.create_material_expression(
        material,
        expression_class,
        x,
        y,
    )


def scalar(material, value: float, x: int, y: int):
    node = expression(material, unreal.MaterialExpressionConstant, x, y)
    set_prop(node, "r", float(value))
    return node


def color(material, values: list[float], x: int, y: int):
    node = expression(material, unreal.MaterialExpressionConstant3Vector, x, y)
    set_prop(
        node,
        "constant",
        unreal.LinearColor(
            float(values[0]),
            float(values[1]),
            float(values[2]),
            1.0,
        ),
    )
    return node


def texture_path(spec: dict, channel: str) -> str:
    return (
        f"{TEXTURE_DEST}/T_{spec['texture_set']}_{channel}_"
        f"{spec['resolution_suffix']}"
    )


def texture_parameter(
    material,
    texture,
    parameter_name: str,
    x: int,
    y: int,
    sampler_type,
):
    node = expression(
        material,
        unreal.MaterialExpressionTextureSampleParameter2D,
        x,
        y,
    )
    set_prop(node, "parameter_name", parameter_name)
    set_prop(node, "texture", texture)
    set_prop(node, "sampler_type", sampler_type)
    return node


def create_material(asset_name: str):
    material = unreal.AssetToolsHelpers.get_asset_tools().create_asset(
        asset_name,
        MATERIAL_DEST,
        unreal.Material,
        unreal.MaterialFactoryNew(),
    )
    if not material:
        raise RuntimeError(f"Could not create material {asset_name}")
    set_prop(material, "two_sided", False)
    return material


def build_textured_material(spec: dict):
    material = create_material(spec["asset"])
    textures = {}
    for channel in ("BaseColor", "Normal", "ORM"):
        path = texture_path(spec, channel)
        texture = unreal.EditorAssetLibrary.load_asset(path)
        if not texture:
            raise RuntimeError(f"Missing imported texture {path}")
        textures[channel] = texture
    base = texture_parameter(
        material,
        textures["BaseColor"],
        "BaseColor",
        -620,
        -180,
        unreal.MaterialSamplerType.SAMPLERTYPE_COLOR,
    )
    normal = texture_parameter(
        material,
        textures["Normal"],
        "Normal",
        -620,
        80,
        unreal.MaterialSamplerType.SAMPLERTYPE_NORMAL,
    )
    orm = texture_parameter(
        material,
        textures["ORM"],
        "ORM",
        -620,
        340,
        unreal.MaterialSamplerType.SAMPLERTYPE_MASKS,
    )
    library = unreal.MaterialEditingLibrary
    library.connect_material_property(
        base, "", unreal.MaterialProperty.MP_BASE_COLOR
    )
    library.connect_material_property(
        normal, "", unreal.MaterialProperty.MP_NORMAL
    )
    library.connect_material_property(
        orm, "R", unreal.MaterialProperty.MP_AMBIENT_OCCLUSION
    )
    library.connect_material_property(
        orm, "G", unreal.MaterialProperty.MP_ROUGHNESS
    )
    library.connect_material_property(
        orm, "B", unreal.MaterialProperty.MP_METALLIC
    )
    library.recompile_material(material)
    unreal.EditorAssetLibrary.save_loaded_asset(material, only_if_is_dirty=False)
    return material


def build_constant_material(spec: dict):
    material = create_material(spec["asset"])
    base = color(material, spec["base_color"], -420, -160)
    metallic = scalar(material, spec["metallic"], -420, 20)
    roughness = scalar(material, spec["roughness"], -420, 180)
    library = unreal.MaterialEditingLibrary
    library.connect_material_property(
        base, "", unreal.MaterialProperty.MP_BASE_COLOR
    )
    library.connect_material_property(
        metallic, "", unreal.MaterialProperty.MP_METALLIC
    )
    library.connect_material_property(
        roughness, "", unreal.MaterialProperty.MP_ROUGHNESS
    )
    library.recompile_material(material)
    unreal.EditorAssetLibrary.save_loaded_asset(material, only_if_is_dirty=False)
    return material


def build_glass_material(spec: dict):
    material = create_material(spec["asset"])
    set_prop(material, "blend_mode", unreal.BlendMode.BLEND_TRANSLUCENT)
    base = color(material, spec["base_color"], -420, -180)
    roughness = scalar(material, spec["roughness"], -420, -20)
    opacity = scalar(material, spec["opacity"], -420, 130)
    refraction = scalar(material, spec["refraction"], -420, 280)
    library = unreal.MaterialEditingLibrary
    library.connect_material_property(
        base, "", unreal.MaterialProperty.MP_BASE_COLOR
    )
    library.connect_material_property(
        roughness, "", unreal.MaterialProperty.MP_ROUGHNESS
    )
    library.connect_material_property(
        opacity, "", unreal.MaterialProperty.MP_OPACITY
    )
    library.connect_material_property(
        refraction, "", unreal.MaterialProperty.MP_REFRACTION
    )
    library.recompile_material(material)
    unreal.EditorAssetLibrary.save_loaded_asset(material, only_if_is_dirty=False)
    return material


def build_materials(texture_manifest: dict) -> dict[str, object]:
    materials = {}
    for slot_name, spec in texture_manifest["materials"].items():
        if spec["blend"] == "opaque":
            material = build_textured_material(spec)
        elif spec["blend"] == "opaque_constant":
            material = build_constant_material(spec)
        elif spec["blend"] == "translucent":
            material = build_glass_material(spec)
        else:
            raise RuntimeError(f"Unsupported material type: {spec['blend']}")
        materials[slot_name] = material
    return materials


def material_slot_names(mesh) -> list[str]:
    names = []
    for slot in mesh.get_editor_property("static_materials"):
        imported = slot.get_editor_property("imported_material_slot_name")
        current = slot.get_editor_property("material_slot_name")
        names.append(str(imported or current))
    return names


def assign_materials(mesh, materials: dict[str, object]) -> list[str]:
    slots = material_slot_names(mesh)
    if set(slots) != set(materials):
        raise RuntimeError(
            f"Unexpected material slots: {slots}; expected {list(materials)}"
        )
    for index, slot_name in enumerate(slots):
        mesh.set_material(index, materials[slot_name])
    post_edit_change = getattr(mesh, "post_edit_change", None)
    if post_edit_change:
        post_edit_change()
    unreal.EditorAssetLibrary.save_loaded_asset(mesh, only_if_is_dirty=False)
    return slots


def validate(mesh, slots: list[str]) -> tuple[list[float], int, list[str]]:
    size = mesh.get_bounds().box_extent * 2.0
    dimensions = [float(size.x), float(size.y), float(size.z)]
    if any(
        abs(value - expected) > 0.5
        for value, expected in zip(
            sorted(dimensions),
            sorted(EXPECTED_DIMENSIONS_CM),
        )
    ):
        raise RuntimeError(f"Unexpected imported dimensions: {dimensions}")
    triangles = int(mesh.get_num_triangles(0))
    if triangles <= 0 or triangles > TRIANGLE_BUDGET:
        raise RuntimeError(
            f"Imported triangle budget exceeded: {triangles}"
        )
    for index, slot in enumerate(mesh.get_editor_property("static_materials")):
        if not slot.get_editor_property("material_interface"):
            raise RuntimeError(f"Unassigned material slot {index}")
    assets = sorted(
        path.split(".")[0]
        for path in unreal.EditorAssetLibrary.list_assets(
            DEST_ROOT,
            recursive=True,
            include_folder=False,
        )
    )
    if len(assets) != 12:
        raise RuntimeError(f"Unexpected total asset count: {len(assets)}")
    return dimensions, triangles, assets


def main() -> None:
    model_manifest, texture_manifest = ensure_sources()
    require_empty_destination()
    mesh = import_model()

    imported_textures = []
    for source in sorted(TEXTURE_ROOT.glob("T_*.png")):
        texture = import_texture(source)
        configure_texture(texture, source.stem)
        imported_textures.append(texture)

    materials = build_materials(texture_manifest)
    slots = assign_materials(mesh, materials)
    dimensions, triangles, assets = validate(mesh, slots)
    unreal.EditorAssetLibrary.save_directory(
        DEST_ROOT,
        only_if_is_dirty=False,
        recursive=True,
    )

    report = {
        "status": "ok",
        "replacement": "mobile_felt_panel_with_center_controller_v1",
        "source_fbx": str(MODEL_FILE),
        "unreal_mesh": MESH_PATH,
        "dimensions_cm": dimensions,
        "triangle_count": triangles,
        "triangle_budget": TRIANGLE_BUDGET,
        "material_slots": slots,
        "material_count": len(materials),
        "texture_count": len(imported_textures),
        "asset_count": len(assets),
        "imported_assets": assets,
        "mobile_settings": {
            "nanite": False,
            "allow_cpu_access": False,
            "light_map_resolution": 64,
            "felt_texture_max_size": 4096,
            "controller_texture_max_size": 512,
        },
        "source_triangle_count": model_manifest["triangle_count"],
    }
    REPORT_PATH.parent.mkdir(parents=True, exist_ok=True)
    REPORT_PATH.write_text(
        json.dumps(report, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    log(
        "MOBILE_MAHJONG_TABLE_IMPORT_OK "
        f"dimensions_cm=({dimensions[0]:.3f},{dimensions[1]:.3f},"
        f"{dimensions[2]:.3f}) triangles={triangles}/"
        f"{TRIANGLE_BUDGET} materials={len(materials)} "
        f"textures={len(imported_textures)} assets={len(assets)}"
    )
    unreal.SystemLibrary.execute_console_command(None, "QUIT_EDITOR")


if __name__ == "__main__":
    main()
