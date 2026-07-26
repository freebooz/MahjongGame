"""Replace the legacy table with the Blender 5.1 premium rounded PBR tabletop.

Run with UnrealEditor-Cmd and ``-ExecutePythonScript``.  The legacy static-mesh
package is deleted before importing the new FBX at the same content path.  This
prevents Unreal's reimport pipeline from retaining the old miter-joint material
layout while native code, maps, and Blueprints continue using the stable path.
The replacement uses only polished walnut and green felt PBR material slots.
"""

from __future__ import annotations

import json
from pathlib import Path

import unreal


PROJECT_ROOT = Path(__file__).resolve().parents[1]
SOURCE_ROOT = PROJECT_ROOT / "SourceArt" / "3D" / "MahjongTable"
MODEL_FILE = SOURCE_ROOT / "SM_StandardMahjongTable.fbx"
MODEL_MANIFEST = SOURCE_ROOT / "MahjongTableAssetManifest.json"
TEXTURE_SOURCE = SOURCE_ROOT / "Textures"
TEXTURE_MANIFEST = TEXTURE_SOURCE / "MahjongTableTextureManifest.json"

DEST_ROOT = "/Game/Art/Mahjong/Table"
MESH_DEST = f"{DEST_ROOT}/Meshes"
TEXTURE_DEST = f"{DEST_ROOT}/Textures"
MATERIAL_DEST = f"{DEST_ROOT}/Materials"
MESH_PATH = f"{MESH_DEST}/SM_StandardMahjongTable"
REPORT_PATH = PROJECT_ROOT / "Saved" / "Reports" / "MahjongTableImportReport.json"

TEXTURED_SLOTS = ("M_Table_Walnut_PBR", "M_Table_Felt_Green_PBR")
EXPECTED_SLOTS = TEXTURED_SLOTS
MATERIAL_ASSET_BY_SLOT = {
    "M_Table_Walnut_PBR": "M_Table_Walnut_PBR",
    "M_Table_Felt_Green_PBR": "M_Table_Felt_Green_PBR",
}
EXPECTED_DIMENSIONS_CM = (115.0, 115.0, 17.0)

def log(message: str) -> None:
    unreal.log(f"[MahjongTabletopImport] {message}")


def warn(message: str) -> None:
    unreal.log_warning(f"[MahjongTabletopImport] {message}")


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
            raise RuntimeError(f"Missing generated source: {path}")
    model_manifest = json.loads(MODEL_MANIFEST.read_text(encoding="utf-8-sig"))
    texture_manifest = json.loads(TEXTURE_MANIFEST.read_text(encoding="utf-8-sig"))

    if model_manifest.get("asset_scope") != "tabletop_only":
        raise RuntimeError("Model manifest is not the tabletop-only Blender asset")
    if model_manifest.get("frame_construction") != "continuous_rounded_profiled_frame":
        raise RuntimeError("Model manifest does not contain the rounded profiled frame")
    dimensions = tuple(float(value) for value in model_manifest.get("nominal_dimensions_mm", []))
    if dimensions != (1150.0, 1150.0, 170.0):
        raise RuntimeError(f"Unexpected model dimensions in manifest: {dimensions}")
    if int(model_manifest.get("triangle_count", 999999)) >= 5000:
        raise RuntimeError("Generated tabletop exceeds the 5000 triangle mobile budget")

    specs = texture_manifest.get("materials", [])
    slots = tuple(spec.get("material_slot") for spec in specs)
    if set(slots) != set(TEXTURED_SLOTS) or len(specs) != 2:
        raise RuntimeError(f"Expected exactly two PBR material definitions, found {slots}")
    texture_files = sorted(TEXTURE_SOURCE.glob("T_*_2K.png"))
    if len(texture_files) != 8:
        raise RuntimeError(f"Expected eight 2K tabletop PBR textures, found {len(texture_files)}")
    return model_manifest, texture_manifest


def delete_existing_table_assets() -> list[str]:
    """Delete the previous table asset set before every full import."""

    if not unreal.EditorAssetLibrary.does_directory_exist(DEST_ROOT):
        return []
    assets = list(unreal.EditorAssetLibrary.list_assets(DEST_ROOT, recursive=True))
    # Break the dependency chain from the top: mesh, materials, then textures.
    def delete_priority(asset_path: str) -> tuple[int, str]:
        if "/Meshes/" in asset_path:
            return (0, asset_path)
        if "/Materials/" in asset_path:
            return (1, asset_path)
        return (2, asset_path)

    deleted: list[str] = []
    for asset_path in sorted(assets, key=delete_priority):
        log(f"deleting previous table resource: {asset_path}")
        if not unreal.EditorAssetLibrary.delete_asset(asset_path):
            raise RuntimeError(f"Could not delete previous table resource {asset_path}")
        deleted.append(asset_path)
    return deleted


def ensure_destination_folders() -> None:
    for path in (DEST_ROOT, MESH_DEST, TEXTURE_DEST, MATERIAL_DEST):
        unreal.EditorAssetLibrary.make_directory(path)


def import_model():
    if unreal.EditorAssetLibrary.does_asset_exist(MESH_PATH):
        log(f"deleting legacy static mesh package before clean import: {MESH_PATH}")
        if not unreal.EditorAssetLibrary.delete_asset(MESH_PATH):
            raise RuntimeError(f"Could not delete legacy static mesh {MESH_PATH}")

    options = unreal.FbxImportUI()
    set_prop(options, "import_as_skeletal", False)
    set_prop(options, "mesh_type_to_import", unreal.FBXImportType.FBXIT_STATIC_MESH)
    set_prop(options, "import_materials", False)
    set_prop(options, "import_textures", False)
    set_prop(options, "automated_import_should_detect_type", False)

    static_data = options.get_editor_property("static_mesh_import_data")
    # Unreal consumes FBX geometry in centimeters; Blender authored this asset
    # in meters, so apply the explicit 100x meter-to-centimeter conversion.
    set_prop(static_data, "import_uniform_scale", 100.0)
    set_prop(static_data, "combine_meshes", True)
    set_prop(static_data, "auto_generate_collision", False)
    set_prop(static_data, "generate_lightmap_u_vs", True)
    set_prop(static_data, "convert_scene", True)
    set_prop(static_data, "force_front_x_axis", False)
    set_prop(
        static_data,
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
        warn(f"could not disable Nanite explicitly: {exc}")
    return mesh


def import_texture(source: Path):
    task = unreal.AssetImportTask()
    task.filename = str(source)
    task.destination_path = TEXTURE_DEST
    task.destination_name = source.stem
    task.automated = True
    task.replace_existing = True
    task.replace_existing_settings = True
    task.save = True
    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])
    texture = unreal.EditorAssetLibrary.load_asset(f"{TEXTURE_DEST}/{source.stem}")
    if not texture:
        raise RuntimeError(f"Texture import failed: {source}")
    return texture


def configure_texture(texture, name: str) -> None:
    is_normal = "_Normal_" in name
    is_mask = "_Roughness_" in name or "_AO_" in name
    set_prop(texture, "srgb", not (is_normal or is_mask))
    if is_normal:
        set_prop(texture, "compression_settings", unreal.TextureCompressionSettings.TC_NORMALMAP)
        set_prop(texture, "flip_green_channel", False)
    elif is_mask:
        set_prop(texture, "compression_settings", unreal.TextureCompressionSettings.TC_MASKS)
    set_prop(texture, "lod_group", unreal.TextureGroup.TEXTUREGROUP_WORLD)
    set_prop(texture, "max_texture_size", 2048)
    post_edit_change = getattr(texture, "post_edit_change", None)
    if post_edit_change:
        post_edit_change()
    unreal.EditorAssetLibrary.save_loaded_asset(texture, only_if_is_dirty=False)


def load_texture(name: str):
    texture = unreal.EditorAssetLibrary.load_asset(f"{TEXTURE_DEST}/{name}")
    if not texture:
        raise RuntimeError(f"Missing imported texture: {name}")
    return texture


def get_or_create_material(name: str):
    path = f"{MATERIAL_DEST}/{name}"
    material = unreal.AssetToolsHelpers.get_asset_tools().create_asset(
        name, MATERIAL_DEST, unreal.Material, unreal.MaterialFactoryNew()
    )
    if not material:
        material = unreal.EditorAssetLibrary.load_asset(path)
        if material:
            return material, False
    if not material:
        raise RuntimeError(f"Could not create material {path}")
    set_prop(material, "two_sided", False)
    return material, True


def expression(material, expression_class, x: int, y: int):
    return unreal.MaterialEditingLibrary.create_material_expression(
        material, expression_class, x, y
    )


def texture_parameter(material, texture, parameter_name: str, x: int, y: int, sampler_type):
    node = expression(material, unreal.MaterialExpressionTextureSampleParameter2D, x, y)
    set_prop(node, "parameter_name", parameter_name)
    set_prop(node, "texture", texture)
    set_prop(node, "sampler_type", sampler_type)
    return node


def build_material(material_spec: dict):
    slot_name = material_spec["material_slot"]
    textures = material_spec["textures"]
    material, created = get_or_create_material(MATERIAL_ASSET_BY_SLOT[slot_name])
    if not created:
        return material
    base_color = texture_parameter(
        material,
        load_texture(Path(textures["BaseColor"]).stem),
        "BaseColor",
        -520,
        -220,
        unreal.MaterialSamplerType.SAMPLERTYPE_COLOR,
    )
    normal = texture_parameter(
        material,
        load_texture(Path(textures["Normal"]).stem),
        "Normal",
        -520,
        20,
        unreal.MaterialSamplerType.SAMPLERTYPE_NORMAL,
    )
    roughness = texture_parameter(
        material,
        load_texture(Path(textures["Roughness"]).stem),
        "Roughness",
        -520,
        260,
        unreal.MaterialSamplerType.SAMPLERTYPE_MASKS,
    )
    ao = texture_parameter(
        material,
        load_texture(Path(textures["AO"]).stem),
        "AO",
        -520,
        470,
        unreal.MaterialSamplerType.SAMPLERTYPE_MASKS,
    )
    library = unreal.MaterialEditingLibrary
    library.connect_material_property(base_color, "", unreal.MaterialProperty.MP_BASE_COLOR)
    library.connect_material_property(normal, "", unreal.MaterialProperty.MP_NORMAL)
    library.connect_material_property(roughness, "R", unreal.MaterialProperty.MP_ROUGHNESS)
    library.connect_material_property(ao, "R", unreal.MaterialProperty.MP_AMBIENT_OCCLUSION)
    metallic = expression(material, unreal.MaterialExpressionConstant, -220, 640)
    set_prop(metallic, "r", 0.0)
    specular = expression(material, unreal.MaterialExpressionConstant, -20, 640)
    set_prop(specular, "r", 0.42 if slot_name == "M_Table_Walnut_PBR" else 0.18)
    library.connect_material_property(
        metallic, "", unreal.MaterialProperty.MP_METALLIC
    )
    library.connect_material_property(
        specular, "", unreal.MaterialProperty.MP_SPECULAR
    )
    library.recompile_material(material)
    unreal.EditorAssetLibrary.save_loaded_asset(material, only_if_is_dirty=False)
    return material


def material_slot_names(mesh) -> list[str]:
    names = []
    for slot in mesh.get_editor_property("static_materials"):
        imported = slot.get_editor_property("imported_material_slot_name")
        current = slot.get_editor_property("material_slot_name")
        names.append(str(imported or current))
    return names


def assign_materials(mesh, materials: dict[str, object]) -> list[str]:
    slots = material_slot_names(mesh)
    if len(slots) != 2:
        raise RuntimeError(f"Reimported mesh must have two material slots, found {slots}")
    missing = []
    for index, slot_name in enumerate(slots):
        material = materials.get(slot_name)
        if not material:
            missing.append(slot_name)
        else:
            mesh.set_material(index, material)
    if missing:
        raise RuntimeError(f"No generated material for FBX slots: {missing}")
    post_edit_change = getattr(mesh, "post_edit_change", None)
    if post_edit_change:
        post_edit_change()
    unreal.EditorAssetLibrary.save_loaded_asset(mesh, only_if_is_dirty=False)
    return slots


def triangle_count(mesh) -> int:
    try:
        subsystem = unreal.get_editor_subsystem(unreal.StaticMeshEditorSubsystem)
        return int(subsystem.get_number_triangles(mesh, 0))
    except Exception:
        try:
            return int(unreal.EditorStaticMeshLibrary.get_number_triangles(mesh, 0))
        except Exception as exc:
            warn(f"triangle count API unavailable: {exc}")
            manifest = json.loads(MODEL_MANIFEST.read_text(encoding="utf-8-sig"))
            return int(manifest["triangle_count"])


def validate(mesh, slots: list[str], texture_count: int) -> tuple[tuple[float, float, float], int]:
    if set(slots) != set(EXPECTED_SLOTS) or len(slots) != 2:
        raise RuntimeError(f"Unexpected material slots: {slots}")
    if texture_count != 8:
        raise RuntimeError(f"Expected eight imported PBR textures, found {texture_count}")
    for index, slot in enumerate(mesh.get_editor_property("static_materials")):
        material = slot.get_editor_property("material_interface")
        if not material:
            raise RuntimeError(f"Material slot {index} is unassigned")

    size = mesh.get_bounds().box_extent * 2.0
    actual = (float(size.x), float(size.y), float(size.z))
    if any(
        abs(value - expected) > 0.25
        for value, expected in zip(sorted(actual), sorted(EXPECTED_DIMENSIONS_CM))
    ):
        raise RuntimeError(f"Unexpected imported dimensions in centimeters: {actual}")
    triangles = triangle_count(mesh)
    if triangles >= 5000:
        raise RuntimeError(f"Imported mesh exceeds mobile triangle budget: {triangles}")
    return actual, triangles


def delete_obsolete_assets() -> list[str]:
    deleted = []
    keep = {
        f"{MATERIAL_DEST}/{asset_name}"
        for asset_name in MATERIAL_ASSET_BY_SLOT.values()
    }
    keep.update(
        f"{TEXTURE_DEST}/{Path(filename).stem}"
        for filename in (
            "T_Wood_BaseColor_2K.png",
            "T_Wood_Normal_2K.png",
            "T_Wood_Roughness_2K.png",
            "T_Wood_AO_2K.png",
            "T_Felt_BaseColor_2K.png",
            "T_Felt_Normal_2K.png",
            "T_Felt_Roughness_2K.png",
            "T_Felt_AO_2K.png",
        )
    )
    candidates = []
    for directory in (MATERIAL_DEST, TEXTURE_DEST):
        for asset in unreal.EditorAssetLibrary.list_assets(
            directory, recursive=True, include_folder=False
        ):
            candidates.append(asset.split(".")[0])
    candidates.sort(key=lambda path: (0 if path.startswith(MATERIAL_DEST) else 1, path))
    for path in candidates:
        if path in keep:
            continue
        if not unreal.EditorAssetLibrary.does_asset_exist(path):
            continue
        referencers = unreal.EditorAssetLibrary.find_package_referencers_for_asset(
            path, load_assets_to_confirm=True
        )
        live_referencers = [
            ref
            for ref in referencers
            if ref != path and not ref.startswith("/Temp/")
        ]
        if live_referencers:
            raise RuntimeError(
                f"Refusing to delete obsolete asset still referenced by {live_referencers}: {path}"
            )
        if not unreal.EditorAssetLibrary.delete_asset(path):
            raise RuntimeError(f"Could not delete obsolete asset: {path}")
        deleted.append(path)
    return deleted


def write_report(
    dimensions: tuple[float, float, float],
    triangles: int,
    slots: list[str],
    deleted_assets: list[str],
    model_manifest: dict,
) -> None:
    report = {
        "status": "ok",
        "replacement": "legacy_miter_table_to_blender_5_1_rounded_pbr_table",
        "source_fbx": str(MODEL_FILE),
        "unreal_mesh": MESH_PATH,
        "dimensions_cm": list(dimensions),
        "triangle_count": triangles,
        "material_slots": slots,
        "texture_count": 8,
        "pivot": model_manifest.get("pivot"),
        "playing_surface_z_cm": 0.0,
        "deleted_obsolete_assets": deleted_assets,
        "runtime_actor": "/Script/GuiyangMahjongClient.Mahjong3DTableActor",
    }
    REPORT_PATH.parent.mkdir(parents=True, exist_ok=True)
    REPORT_PATH.write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )


def main() -> None:
    log(f"replace source={MODEL_FILE} destination={MESH_PATH}")
    model_manifest, texture_manifest = ensure_sources()
    deleted_assets = delete_existing_table_assets()
    ensure_destination_folders()

    mesh = import_model()
    imported_textures = []
    for source in sorted(TEXTURE_SOURCE.glob("T_*_2K.png")):
        texture = import_texture(source)
        configure_texture(texture, source.stem)
        imported_textures.append(texture)

    materials = {}
    for material_spec in texture_manifest["materials"]:
        material = build_material(material_spec)
        materials[material_spec["material_slot"]] = material
    slots = assign_materials(mesh, materials)
    dimensions, triangles = validate(mesh, slots, len(imported_textures))

    unreal.EditorAssetLibrary.save_directory(
        DEST_ROOT, only_if_is_dirty=False, recursive=True
    )
    deleted_assets.extend(delete_obsolete_assets())
    unreal.EditorAssetLibrary.save_directory(
        DEST_ROOT, only_if_is_dirty=False, recursive=True
    )
    write_report(dimensions, triangles, slots, deleted_assets, model_manifest)
    log(
        "MAHJONG_TABLETOP_IMPORT_OK "
        f"dimensions_cm=({dimensions[0]:.3f},{dimensions[1]:.3f},{dimensions[2]:.3f}) "
        f"triangles={triangles} slots={len(slots)} textures={len(imported_textures)} "
        f"deleted_obsolete_assets={len(deleted_assets)}"
    )


if __name__ == "__main__":
    main()
