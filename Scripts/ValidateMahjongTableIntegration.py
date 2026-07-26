"""Headless validation for the Blender 5.2 tabletop-only Unreal asset."""

from __future__ import annotations

import json
from pathlib import Path

import unreal


PROJECT_ROOT = Path(__file__).resolve().parents[1]
MESH_PATH = "/Game/Art/Mahjong/Table/Meshes/SM_StandardMahjongTable"
CONTENT_ROOT = "/Game/Art/Mahjong/Table"
RUNTIME_CLASS_PATH = (
    "/Game/Client/Room/Presentation/BP_MahjongRoomPresentation."
    "BP_MahjongRoomPresentation_C"
)
REPORT_PATH = PROJECT_ROOT / "Saved" / "Reports" / "MahjongTableValidationReport.json"
MODEL_MANIFEST = (
    PROJECT_ROOT
    / "SourceArt"
    / "3D"
    / "MahjongTable"
    / "MahjongTableAssetManifest.json"
)
EXPECTED_SLOTS = {
    "M_Table_Walnut_PBR",
    "M_Table_Felt_Green_PBR",
}


def material_slot_names(mesh) -> list[str]:
    return [
        str(
            slot.get_editor_property("imported_material_slot_name")
            or slot.get_editor_property("material_slot_name")
        )
        for slot in mesh.get_editor_property("static_materials")
    ]


def triangle_count(mesh) -> int:
    try:
        subsystem = unreal.get_editor_subsystem(unreal.StaticMeshEditorSubsystem)
        return int(subsystem.get_number_triangles(mesh, 0))
    except Exception:
        try:
            return int(unreal.EditorStaticMeshLibrary.get_number_triangles(mesh, 0))
        except Exception:
            manifest = json.loads(MODEL_MANIFEST.read_text(encoding="utf-8-sig"))
            return int(manifest["triangle_count"])


def main() -> None:
    mesh = unreal.EditorAssetLibrary.load_asset(MESH_PATH)
    if not mesh:
        raise RuntimeError(f"Missing tabletop mesh {MESH_PATH}")
    size = mesh.get_bounds().box_extent * 2.0
    dimensions = (float(size.x), float(size.y), float(size.z))
    expected = (115.0, 115.0, 6.5)
    if any(
        abs(value - wanted) > 0.25
        for value, wanted in zip(sorted(dimensions), sorted(expected))
    ):
        raise RuntimeError(f"Unexpected tabletop dimensions: {dimensions}")

    slots = material_slot_names(mesh)
    if len(slots) != 2 or set(slots) != EXPECTED_SLOTS:
        raise RuntimeError(f"Expected two tabletop material slots, found {slots}")
    assigned_materials = []
    for index, slot in enumerate(mesh.get_editor_property("static_materials")):
        material = slot.get_editor_property("material_interface")
        if not material:
            raise RuntimeError(f"Material slot {index} ({slots[index]}) is unassigned")
        assigned_materials.append(material.get_path_name())

    assets = unreal.EditorAssetLibrary.list_assets(
        CONTENT_ROOT, recursive=True, include_folder=False
    )
    textures = [path for path in assets if "/Textures/T_" in path]
    materials = [path for path in assets if "/Materials/M_Table_" in path]
    if len(textures) != 8 or len(materials) != 2:
        raise RuntimeError(
            f"Legacy table assets remain: textures={len(textures)} materials={len(materials)}"
        )

    triangles = triangle_count(mesh)
    if triangles >= 5000:
        raise RuntimeError(f"Tabletop exceeds mobile triangle budget: {triangles}")

    runtime_class = unreal.load_class(None, RUNTIME_CLASS_PATH)
    if not runtime_class:
        raise RuntimeError(f"Could not load runtime table class {RUNTIME_CLASS_PATH}")
    preview_world = unreal.EditorLoadingAndSavingUtils.load_map(
        "/Game/Maps/MahjongRoomVisualPreviewMap"
    )
    if not preview_world:
        raise RuntimeError("Could not load MahjongRoomVisualPreviewMap")
    runtime_instance = next(
        (
            actor
            for actor in unreal.get_editor_subsystem(
                unreal.EditorActorSubsystem
            ).get_all_level_actors()
            if actor.get_class() == runtime_class
        ),
        None,
    )
    if not runtime_instance:
        raise RuntimeError("Runtime BP_MahjongRoomPresentation instance is missing")
    table_components = runtime_instance.get_components_by_class(
        unreal.StaticMeshComponent
    )
    table_component = next(
        (
            component
            for component in table_components
            if component.get_name() == "MahjongTableMesh"
        ),
        None,
    )
    if not table_component:
        raise RuntimeError("Runtime MahjongTableMesh component is missing")
    table_location = table_component.get_editor_property("relative_location")
    table_scale = table_component.get_editor_property("relative_scale3d")
    runtime_mesh = table_component.get_editor_property("static_mesh")
    if abs(float(table_location.z)) > 0.01:
        raise RuntimeError(
            f"Runtime table component is not aligned to the Z=0 surface pivot: {table_location}"
        )
    if any(abs(float(value) - 10.0) > 0.01 for value in (table_scale.x, table_scale.y, table_scale.z)):
        raise RuntimeError(f"Unexpected runtime table presentation scale: {table_scale}")
    if not runtime_mesh or runtime_mesh.get_path_name() != (
        f"{MESH_PATH}.SM_StandardMahjongTable"
    ):
        raise RuntimeError(f"Runtime actor references the wrong table mesh: {runtime_mesh}")

    report = {
        "status": "ok",
        "asset_scope": "tabletop_only",
        "mesh": MESH_PATH,
        "dimensions_cm": list(dimensions),
        "triangle_count": triangles,
        "material_slots": slots,
        "assigned_materials": assigned_materials,
        "texture_count": len(textures),
        "material_count": len(materials),
        "playing_surface_z_cm": 0.0,
        "runtime_class": RUNTIME_CLASS_PATH,
        "runtime_class_loaded": True,
        "runtime_table_component_z": float(table_location.z),
        "runtime_table_scale": [
            float(table_scale.x),
            float(table_scale.y),
            float(table_scale.z),
        ],
        "runtime_mesh": runtime_mesh.get_path_name(),
    }
    REPORT_PATH.parent.mkdir(parents=True, exist_ok=True)
    REPORT_PATH.write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    unreal.log(
        "[MahjongTabletopValidation] MAHJONG_TABLETOP_VALIDATION_OK "
        f"dimensions_cm=({dimensions[0]:.3f},{dimensions[1]:.3f},{dimensions[2]:.3f}) "
        f"triangles={triangles} slots={len(slots)} textures={len(textures)} "
        "runtime_class=loaded"
    )


if __name__ == "__main__":
    main()
