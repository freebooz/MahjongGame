"""Read-only validation for the imported mobile Mahjong table assets."""

from __future__ import annotations

import json
from pathlib import Path

import unreal


PROJECT_ROOT = Path(__file__).resolve().parents[1]
DEST_ROOT = "/Game/Art/Mahjong/Table"
MESH_PATH = f"{DEST_ROOT}/Meshes/SM_StandardMahjongTable"
REPORT_PATH = (
    PROJECT_ROOT / "Saved" / "Reports" / "MobileMahjongTableValidation.json"
)
EXPECTED_SLOTS = {
    "M_Table_Felt_Mobile",
    "M_Table_Felt_Edge_Mobile",
    "M_Table_Controller_Gunmetal_Mobile",
    "M_Table_Controller_Display_Mobile",
    "M_Table_Controller_Glass_Mobile",
}


def main() -> None:
    mesh = unreal.EditorAssetLibrary.load_asset(MESH_PATH)
    if not mesh:
        raise RuntimeError(f"Missing mesh {MESH_PATH}")
    size = mesh.get_bounds().box_extent * 2.0
    dimensions = [float(size.x), float(size.y), float(size.z)]
    if abs(dimensions[0] - 300.0) > 0.5:
        raise RuntimeError(f"Unexpected X dimension: {dimensions}")
    if abs(dimensions[1] - 300.0) > 0.5:
        raise RuntimeError(f"Unexpected Y dimension: {dimensions}")
    triangles = int(mesh.get_num_triangles(0))
    if triangles <= 0 or triangles > 7000:
        raise RuntimeError(f"Triangle budget exceeded: {triangles}")

    slots = []
    assigned = []
    for index, slot in enumerate(mesh.get_editor_property("static_materials")):
        imported = slot.get_editor_property("imported_material_slot_name")
        current = slot.get_editor_property("material_slot_name")
        slots.append(str(imported or current))
        material = slot.get_editor_property("material_interface")
        if not material:
            raise RuntimeError(f"Unassigned material slot {index}")
        assigned.append(material.get_path_name())
    if set(slots) != EXPECTED_SLOTS:
        raise RuntimeError(f"Unexpected slots: {slots}")

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
    if len(assets) != 12 or len(textures) != 6 or len(materials) != 5:
        raise RuntimeError(
            f"Unexpected asset counts: total={len(assets)} "
            f"textures={len(textures)} materials={len(materials)}"
        )
    if meshes != [MESH_PATH]:
        raise RuntimeError(f"Unexpected mesh assets: {meshes}")

    texture_details = []
    for path in textures:
        texture = unreal.EditorAssetLibrary.load_asset(path)
        size_x = int(texture.blueprint_get_size_x())
        size_y = int(texture.blueprint_get_size_y())
        expected = 512 if path.endswith("_512") else 4096
        if size_x != expected or size_y != expected:
            raise RuntimeError(f"Unexpected texture size: {path}")
        texture_details.append(
            {
                "asset": path,
                "size": [size_x, size_y],
                "max_texture_size": int(
                    texture.get_editor_property("max_texture_size")
                ),
                "srgb": bool(texture.get_editor_property("srgb")),
                "compression": str(
                    texture.get_editor_property("compression_settings")
                ),
            }
        )

    try:
        nanite = mesh.get_editor_property("nanite_settings")
        nanite_enabled = bool(nanite.enabled)
    except Exception:
        nanite_enabled = False
    if nanite_enabled:
        raise RuntimeError("Nanite must remain disabled for the mobile asset")
    if bool(mesh.get_editor_property("allow_cpu_access")):
        raise RuntimeError("CPU access must remain disabled")

    report = {
        "status": "ok",
        "unreal_mesh": MESH_PATH,
        "dimensions_cm": dimensions,
        "triangle_count": triangles,
        "triangle_budget": 7000,
        "material_slots": slots,
        "assigned_materials": assigned,
        "material_count": len(materials),
        "texture_count": len(textures),
        "asset_count": len(assets),
        "texture_details": texture_details,
        "nanite_enabled": nanite_enabled,
        "allow_cpu_access": False,
    }
    REPORT_PATH.parent.mkdir(parents=True, exist_ok=True)
    REPORT_PATH.write_text(
        json.dumps(report, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    unreal.log(
        "[MobileMahjongTableValidation] MOBILE_MAHJONG_TABLE_VALIDATION_OK "
        f"dimensions_cm=({dimensions[0]:.3f},{dimensions[1]:.3f},"
        f"{dimensions[2]:.3f}) triangles={triangles}/7000 "
        f"materials={len(materials)} textures={len(textures)} "
        f"assets={len(assets)} nanite={nanite_enabled}"
    )
    unreal.SystemLibrary.execute_console_command(None, "QUIT_EDITOR")


if __name__ == "__main__":
    main()
