import json
from pathlib import Path

import unreal


PROJECT_ROOT = Path(__file__).resolve().parents[1]
DEST_ROOT = "/Game/Art/Mahjong/Table"
MESH_PATH = f"{DEST_ROOT}/Meshes/SM_StandardMahjongTable"
# 验证必须跟随当前仓库副本，避免审查另一个盘符上的旧报告或旧纹理清单。
REPORT_PATH = (
    PROJECT_ROOT / "Saved" / "Reports" / "AutomaticMahjongTableImportReport.json"
)
TEXTURE_MANIFEST_PATH = (
    PROJECT_ROOT
    / "SourceArt"
    / "3D"
    / "MahjongTableProduction"
    / "Textures"
    / "AutomaticMahjongTableTextureManifest.json"
)
EXPECTED_DIMENSIONS_CM = (150.0, 150.0, 8.7025)
EXPECTED_TEXTURE_COUNT = 15
EXPECTED_MATERIAL_COUNT = 9


def log(message: str) -> None:
    unreal.log(f"[AutomaticMahjongTableValidation] {message}")


def triangle_count(mesh) -> int:
    getter = getattr(mesh, "get_num_triangles", None)
    if getter:
        return int(getter(0))

    subsystem = unreal.get_editor_subsystem(unreal.StaticMeshEditorSubsystem)
    getter = getattr(subsystem, "get_number_triangles", None)
    if getter:
        return int(getter(mesh, 0))

    mesh_description = subsystem.get_mesh_description(mesh, 0)
    if mesh_description:
        getter = getattr(mesh_description, "get_triangle_count", None)
        if getter:
            return int(getter())

    raise RuntimeError(
        "Unable to read the LOD0 triangle count through UE 5.8 Python APIs"
    )


def main() -> None:
    texture_manifest = json.loads(
        TEXTURE_MANIFEST_PATH.read_text(encoding="utf-8")
    )
    expected_slots = set(texture_manifest["materials"])
    mesh = unreal.EditorAssetLibrary.load_asset(MESH_PATH)
    if not mesh:
        raise RuntimeError(f"Missing imported mesh: {MESH_PATH}")

    static_materials = list(mesh.get_editor_property("static_materials"))
    slots = []
    assigned_materials = []
    for index, slot in enumerate(static_materials):
        imported = slot.get_editor_property("imported_material_slot_name")
        current = slot.get_editor_property("material_slot_name")
        slot_name = str(imported or current)
        material = slot.get_editor_property("material_interface")
        if not material:
            raise RuntimeError(f"Material slot {index} is unassigned")
        slots.append(slot_name)
        assigned_materials.append(material.get_path_name())

    if set(slots) != expected_slots:
        raise RuntimeError(
            f"Unexpected imported material slots: {slots}; expected {expected_slots}"
        )

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

    assets = sorted(
        path.split(".")[0]
        for path in unreal.EditorAssetLibrary.list_assets(
            DEST_ROOT,
            recursive=True,
            include_folder=False,
        )
    )
    textures = [path for path in assets if "/Textures/T_" in path]
    materials = [path for path in assets if "/Materials/M_" in path]
    meshes = [path for path in assets if "/Meshes/SM_" in path]
    if len(assets) != 25:
        raise RuntimeError(f"Unexpected total asset count: {len(assets)}")
    if len(textures) != EXPECTED_TEXTURE_COUNT:
        raise RuntimeError(f"Unexpected texture asset count: {len(textures)}")
    if len(materials) != EXPECTED_MATERIAL_COUNT:
        raise RuntimeError(f"Unexpected material asset count: {len(materials)}")
    if meshes != [MESH_PATH]:
        raise RuntimeError(f"Unexpected mesh assets: {meshes}")

    texture_details = []
    for asset_path in textures:
        texture = unreal.EditorAssetLibrary.load_asset(asset_path)
        texture_details.append(
            {
                "asset": asset_path,
                "size_x": int(texture.blueprint_get_size_x()),
                "size_y": int(texture.blueprint_get_size_y()),
                "srgb": bool(texture.get_editor_property("srgb")),
                "compression": str(
                    texture.get_editor_property("compression_settings")
                ),
            }
        )
    if any(
        item["size_x"] != 2048 or item["size_y"] != 2048
        for item in texture_details
    ):
        raise RuntimeError("One or more production textures are not 2048x2048")

    report = {
        "status": "ok",
        "replacement": "approved_automatic_mahjong_table_v2",
        "unreal_mesh": MESH_PATH,
        "dimensions_cm": list(dimensions),
        "triangle_count": triangles,
        "material_slots": slots,
        "assigned_materials": assigned_materials,
        "material_count": len(materials),
        "texture_count": len(textures),
        "asset_count": len(assets),
        "imported_assets": assets,
        "texture_details": texture_details,
        "content_validation": "25 assets passed during clean import",
    }
    REPORT_PATH.parent.mkdir(parents=True, exist_ok=True)
    REPORT_PATH.write_text(
        json.dumps(report, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    log(
        "AUTOMATIC_MAHJONG_TABLE_VALIDATION_OK "
        f"dimensions_cm=({dimensions[0]:.3f},{dimensions[1]:.3f},"
        f"{dimensions[2]:.3f}) triangles={triangles} "
        f"slots={len(slots)} materials={len(materials)} "
        f"textures={len(textures)} assets={len(assets)}"
    )
    unreal.SystemLibrary.execute_console_command(None, "QUIT_EDITOR")


if __name__ == "__main__":
    main()
