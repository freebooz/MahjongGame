"""Clean-import the approved automatic Mahjong table into Unreal Engine 5.8.

The previous /Game/Art/Mahjong/Table asset set is deleted first. The script
then imports the production FBX, all new 2K PBR textures, creates nine
materials, binds every FBX material slot, validates dimensions and writes a
machine-readable report. It never uses Reimport or overwrite import settings.
"""

from __future__ import annotations

import json
from pathlib import Path

import unreal


PROJECT_ROOT = Path(__file__).resolve().parents[1]
SOURCE_ROOT = PROJECT_ROOT / "SourceArt" / "3D" / "MahjongTableProduction"
MODEL_FILE = SOURCE_ROOT / "SM_StandardMahjongTable.fbx"
MODEL_MANIFEST = SOURCE_ROOT / "AutomaticMahjongTableProductionManifest.json"
TEXTURE_ROOT = SOURCE_ROOT / "Textures"
TEXTURE_MANIFEST = TEXTURE_ROOT / "AutomaticMahjongTableTextureManifest.json"

DEST_ROOT = "/Game/Art/Mahjong/Table"
MESH_DEST = f"{DEST_ROOT}/Meshes"
TEXTURE_DEST = f"{DEST_ROOT}/Textures"
MATERIAL_DEST = f"{DEST_ROOT}/Materials"
MESH_PATH = f"{MESH_DEST}/SM_StandardMahjongTable"
REPORT_PATH = (
    PROJECT_ROOT / "Saved" / "Reports" / "AutomaticMahjongTableImportReport.json"
)

EXPECTED_DIMENSIONS_CM = (150.0, 150.0, 8.70252)
EXPECTED_TEXTURE_COUNT = 15
EXPECTED_MATERIAL_COUNT = 9


def log(message: str) -> None:
    unreal.log(f"[AutomaticMahjongTableImport] {message}")


def warn(message: str) -> None:
    unreal.log_warning(f"[AutomaticMahjongTableImport] {message}")


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
        raise RuntimeError(f"Production model is not approved: {model_manifest}")
    if model_manifest.get("uvless_objects"):
        raise RuntimeError(
            f"Production model contains UV-less meshes: "
            f"{model_manifest['uvless_objects']}"
        )
    material_specs = texture_manifest.get("materials", {})
    if len(material_specs) != EXPECTED_MATERIAL_COUNT:
        raise RuntimeError(
            f"Expected {EXPECTED_MATERIAL_COUNT} material definitions, "
            f"found {len(material_specs)}"
        )
    if set(model_manifest.get("materials", [])) != set(material_specs):
        raise RuntimeError(
            "FBX material slots and texture manifest definitions do not match"
        )
    texture_files = sorted(TEXTURE_ROOT.glob("T_*_2K.png"))
    if len(texture_files) != EXPECTED_TEXTURE_COUNT:
        raise RuntimeError(
            f"Expected {EXPECTED_TEXTURE_COUNT} production textures, "
            f"found {len(texture_files)}"
        )
    return model_manifest, texture_manifest


def delete_existing_table_assets() -> list[str]:
    """Delete only the approved Mahjong table target directory."""

    if not unreal.EditorAssetLibrary.does_directory_exist(DEST_ROOT):
        return []
    assets = list(
        unreal.EditorAssetLibrary.list_assets(
            DEST_ROOT,
            recursive=True,
            include_folder=False,
        )
    )

    def priority(asset_path: str) -> tuple[int, str]:
        if "/Meshes/" in asset_path:
            return (0, asset_path)
        if "/Materials/" in asset_path:
            return (1, asset_path)
        return (2, asset_path)

    deleted: list[str] = []
    for asset_path in sorted(assets, key=priority):
        package_path = asset_path.split(".")[0]
        log(f"delete old table asset: {package_path}")
        if not unreal.EditorAssetLibrary.delete_asset(package_path):
            raise RuntimeError(f"Could not delete old table asset {package_path}")
        deleted.append(package_path)
    return deleted


def ensure_destination_folders() -> None:
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
    set_prop(data, "auto_generate_collision", False)
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
    path = f"{TEXTURE_DEST}/{source.stem}"
    texture = unreal.EditorAssetLibrary.load_asset(path)
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
    set_prop(texture, "lod_group", unreal.TextureGroup.TEXTUREGROUP_WORLD)
    set_prop(texture, "max_texture_size", 2048)
    post_edit_change = getattr(texture, "post_edit_change", None)
    if post_edit_change:
        post_edit_change()
    unreal.EditorAssetLibrary.save_loaded_asset(texture, only_if_is_dirty=False)


def load_texture(set_name: str, channel: str):
    path = f"{TEXTURE_DEST}/T_{set_name}_{channel}_2K"
    texture = unreal.EditorAssetLibrary.load_asset(path)
    if not texture:
        raise RuntimeError(f"Missing imported texture {path}")
    return texture


def expression(material, expression_class, x: int, y: int):
    return unreal.MaterialEditingLibrary.create_material_expression(
        material,
        expression_class,
        x,
        y,
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


def scalar(material, value: float, x: int, y: int):
    node = expression(material, unreal.MaterialExpressionConstant, x, y)
    set_prop(node, "r", float(value))
    return node


def color(material, values: list[float], x: int, y: int):
    node = expression(material, unreal.MaterialExpressionConstant3Vector, x, y)
    set_prop(
        node,
        "constant",
        unreal.LinearColor(float(values[0]), float(values[1]), float(values[2]), 1.0),
    )
    return node


def create_material(asset_name: str):
    material = unreal.AssetToolsHelpers.get_asset_tools().create_asset(
        asset_name,
        MATERIAL_DEST,
        unreal.Material,
        unreal.MaterialFactoryNew(),
    )
    if not material:
        raise RuntimeError(f"Could not create material {MATERIAL_DEST}/{asset_name}")
    set_prop(material, "two_sided", False)
    return material


def build_opaque_material(asset_name: str, spec: dict):
    material = create_material(asset_name)
    texture_set = spec["texture_set"]
    base = texture_parameter(
        material,
        load_texture(texture_set, "BaseColor"),
        "BaseColor",
        -620,
        -180,
        unreal.MaterialSamplerType.SAMPLERTYPE_COLOR,
    )
    normal = texture_parameter(
        material,
        load_texture(texture_set, "Normal"),
        "Normal",
        -620,
        80,
        unreal.MaterialSamplerType.SAMPLERTYPE_NORMAL,
    )
    orm = texture_parameter(
        material,
        load_texture(texture_set, "ORM"),
        "ORM",
        -620,
        360,
        unreal.MaterialSamplerType.SAMPLERTYPE_MASKS,
    )
    library = unreal.MaterialEditingLibrary
    library.connect_material_property(
        base,
        "",
        unreal.MaterialProperty.MP_BASE_COLOR,
    )
    library.connect_material_property(
        normal,
        "",
        unreal.MaterialProperty.MP_NORMAL,
    )
    library.connect_material_property(
        orm,
        "R",
        unreal.MaterialProperty.MP_AMBIENT_OCCLUSION,
    )
    library.connect_material_property(
        orm,
        "G",
        unreal.MaterialProperty.MP_ROUGHNESS,
    )
    library.connect_material_property(
        orm,
        "B",
        unreal.MaterialProperty.MP_METALLIC,
    )
    emissive_scale = float(spec.get("emissive_scale", 0.0))
    if emissive_scale > 0.0:
        multiply = expression(material, unreal.MaterialExpressionMultiply, -180, -60)
        strength = scalar(material, emissive_scale, -390, -20)
        library.connect_material_expressions(base, "", multiply, "A")
        library.connect_material_expressions(strength, "", multiply, "B")
        library.connect_material_property(
            multiply,
            "",
            unreal.MaterialProperty.MP_EMISSIVE_COLOR,
        )
    library.recompile_material(material)
    unreal.EditorAssetLibrary.save_loaded_asset(material, only_if_is_dirty=False)
    return material


def build_glass_material(asset_name: str, spec: dict):
    material = create_material(asset_name)
    set_prop(material, "blend_mode", unreal.BlendMode.BLEND_TRANSLUCENT)
    try:
        set_prop(
            material,
            "translucency_lighting_mode",
            unreal.TranslucencyLightingMode.TLM_SURFACE_PER_PIXEL_LIGHTING,
        )
    except Exception as exc:
        warn(f"could not set glass translucency lighting mode: {exc}")

    base = color(material, spec["base_color"], -460, -220)
    roughness = scalar(material, spec["roughness"], -460, -40)
    opacity = scalar(material, spec["opacity"], -460, 130)
    refraction = scalar(material, spec["refraction"], -460, 300)
    library = unreal.MaterialEditingLibrary
    library.connect_material_property(
        base,
        "",
        unreal.MaterialProperty.MP_BASE_COLOR,
    )
    library.connect_material_property(
        roughness,
        "",
        unreal.MaterialProperty.MP_ROUGHNESS,
    )
    library.connect_material_property(
        opacity,
        "",
        unreal.MaterialProperty.MP_OPACITY,
    )
    library.connect_material_property(
        refraction,
        "",
        unreal.MaterialProperty.MP_REFRACTION,
    )
    library.recompile_material(material)
    unreal.EditorAssetLibrary.save_loaded_asset(material, only_if_is_dirty=False)
    return material


def build_materials(texture_manifest: dict) -> dict[str, object]:
    materials: dict[str, object] = {}
    for slot_name, spec in texture_manifest["materials"].items():
        if spec["blend"] == "translucent":
            material = build_glass_material(spec["asset"], spec)
        else:
            material = build_opaque_material(spec["asset"], spec)
        materials[slot_name] = material
    return materials


def material_slot_names(mesh) -> list[str]:
    names: list[str] = []
    for slot in mesh.get_editor_property("static_materials"):
        imported = slot.get_editor_property("imported_material_slot_name")
        current = slot.get_editor_property("material_slot_name")
        names.append(str(imported or current))
    return names


def resolve_material(slot_name: str, materials: dict[str, object]):
    if slot_name in materials:
        return materials[slot_name]
    matches = [
        material
        for source_name, material in materials.items()
        if slot_name.startswith(source_name)
    ]
    return matches[0] if len(matches) == 1 else None


def assign_materials(mesh, materials: dict[str, object]) -> list[str]:
    slots = material_slot_names(mesh)
    if len(slots) != EXPECTED_MATERIAL_COUNT:
        raise RuntimeError(
            f"Expected {EXPECTED_MATERIAL_COUNT} FBX slots, found {slots}"
        )
    missing: list[str] = []
    for index, slot_name in enumerate(slots):
        material = resolve_material(slot_name, materials)
        if not material:
            missing.append(slot_name)
        else:
            mesh.set_material(index, material)
    if missing:
        raise RuntimeError(f"No generated material for slots: {missing}")
    post_edit_change = getattr(mesh, "post_edit_change", None)
    if post_edit_change:
        post_edit_change()
    unreal.EditorAssetLibrary.save_loaded_asset(mesh, only_if_is_dirty=False)
    return slots


def triangle_count(mesh) -> int:
    get_num_triangles = getattr(mesh, "get_num_triangles", None)
    if get_num_triangles:
        return int(get_num_triangles(0))
    try:
        subsystem = unreal.get_editor_subsystem(unreal.StaticMeshEditorSubsystem)
        return int(subsystem.get_number_triangles(mesh, 0))
    except Exception as exc:
        raise RuntimeError(
            "UE 5.8 does not expose a supported static-mesh triangle-count API"
        ) from exc


def validate(
    mesh,
    slots: list[str],
    texture_manifest: dict,
) -> tuple[tuple[float, float, float], int, list[str]]:
    expected_slots = set(texture_manifest["materials"])
    if set(slots) != expected_slots:
        raise RuntimeError(
            f"Unexpected imported material slots: {slots}; expected {expected_slots}"
        )
    for index, slot in enumerate(mesh.get_editor_property("static_materials")):
        if not slot.get_editor_property("material_interface"):
            raise RuntimeError(f"Material slot {index} is unassigned")

    size = mesh.get_bounds().box_extent * 2.0
    dimensions = (float(size.x), float(size.y), float(size.z))
    if any(
        abs(value - expected) > 0.5
        for value, expected in zip(
            sorted(dimensions),
            sorted(EXPECTED_DIMENSIONS_CM),
        )
    ):
        raise RuntimeError(f"Unexpected imported dimensions: {dimensions}")
    triangles = triangle_count(mesh)
    if triangles <= 0 or triangles >= 100000:
        raise RuntimeError(f"Unexpected production triangle count: {triangles}")

    assets = [
        path.split(".")[0]
        for path in unreal.EditorAssetLibrary.list_assets(
            DEST_ROOT,
            recursive=True,
            include_folder=False,
        )
    ]
    textures = [path for path in assets if "/Textures/T_" in path]
    materials = [path for path in assets if "/Materials/M_" in path]
    meshes = [path for path in assets if "/Meshes/SM_" in path]
    if len(textures) != EXPECTED_TEXTURE_COUNT:
        raise RuntimeError(f"Unexpected texture asset count: {len(textures)}")
    if len(materials) != EXPECTED_MATERIAL_COUNT:
        raise RuntimeError(f"Unexpected material asset count: {len(materials)}")
    if meshes != [MESH_PATH]:
        raise RuntimeError(f"Unexpected mesh assets: {meshes}")
    return dimensions, triangles, assets


def main() -> None:
    log(f"clean replacement source={MODEL_FILE} destination={MESH_PATH}")
    model_manifest, texture_manifest = ensure_sources()
    deleted_assets = delete_existing_table_assets()
    ensure_destination_folders()

    mesh = import_model()
    imported_textures = []
    for source in sorted(TEXTURE_ROOT.glob("T_*_2K.png")):
        texture = import_texture(source)
        configure_texture(texture, source.stem)
        imported_textures.append(texture)

    materials = build_materials(texture_manifest)
    slots = assign_materials(mesh, materials)
    dimensions, triangles, imported_assets = validate(
        mesh,
        slots,
        texture_manifest,
    )
    unreal.EditorAssetLibrary.save_directory(
        DEST_ROOT,
        only_if_is_dirty=False,
        recursive=True,
    )

    referencers = unreal.EditorAssetLibrary.find_package_referencers_for_asset(
        MESH_PATH,
        load_assets_to_confirm=True,
    )
    report = {
        "status": "ok",
        "replacement": "approved_automatic_mahjong_table_v2",
        "source_fbx": str(MODEL_FILE),
        "source_manifest": str(MODEL_MANIFEST),
        "unreal_mesh": MESH_PATH,
        "dimensions_cm": list(dimensions),
        "triangle_count": triangles,
        "material_slots": slots,
        "material_count": len(materials),
        "texture_count": len(imported_textures),
        "deleted_old_assets": deleted_assets,
        "imported_assets": imported_assets,
        "referencers": list(referencers),
        "model_sha256": next(
            item["sha256"]
            for item in model_manifest["files"]
            if item["name"] == MODEL_FILE.name
        ),
    }
    REPORT_PATH.parent.mkdir(parents=True, exist_ok=True)
    REPORT_PATH.write_text(
        json.dumps(report, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    log(
        "AUTOMATIC_MAHJONG_TABLE_IMPORT_OK "
        f"dimensions_cm=({dimensions[0]:.3f},{dimensions[1]:.3f},{dimensions[2]:.3f}) "
        f"triangles={triangles} slots={len(slots)} "
        f"materials={len(materials)} textures={len(imported_textures)} "
        f"deleted_old_assets={len(deleted_assets)}"
    )


if __name__ == "__main__":
    main()
