# 将已批准的自动麻将桌审查 blend 整理为生产源文件，清理临时对象并生成确定性导出。
# 更新前只删除目标桌台及明确废弃依赖；不得覆盖无关 Blender 集合或保留旧导入设置。
"""Promote the approved automatic Mahjong table review blend to production.

Run with Blender 5.2 against the approved review blend. The script adds UVs
where required, triangulates export meshes, saves a production blend, exports
FBX/GLB, and writes a deterministic manifest. It never modifies the approved
review files in place.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import sys
from pathlib import Path

import bpy
import bmesh
from mathutils import Vector


PRODUCTION_ROOT_NAME = "SM_StandardMahjongTable"
EXPECTED_MATERIALS = {
    "M_Table_Controller_BlackPanel",
    "M_Table_Controller_DirectionGold",
    "M_Table_Controller_GoldLabel",
    "M_Table_Controller_Gunmetal",
    "M_Table_Controller_SectorDividerGold",
    "M_Table_Controller_TransparentGlass",
    "M_Table_Felt_Green_PBR",
    "M_Table_Frame_BlackAlloy",
    "M_Table_Frame_GoldInlay",
}


def arguments() -> argparse.Namespace:
    argv = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project_root", type=Path)
    return parser.parse_args(argv)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def mesh_objects() -> list[bpy.types.Object]:
    objects = sorted(
        (obj for obj in bpy.context.scene.objects if obj.type == "MESH"),
        key=lambda obj: obj.name,
    )
    if not objects:
        raise RuntimeError("Approved review blend contains no mesh objects")
    return objects


def select_only(obj: bpy.types.Object) -> None:
    bpy.ops.object.mode_set(mode="OBJECT") if bpy.context.object and bpy.context.object.mode != "OBJECT" else None
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj


def triangulate(objects: list[bpy.types.Object]) -> None:
    for obj in objects:
        select_only(obj)
        modifier = obj.modifiers.new("ProductionTriangulation", "TRIANGULATE")
        modifier.quad_method = "BEAUTY"
        modifier.ngon_method = "BEAUTY"
        bpy.ops.object.modifier_apply(modifier=modifier.name)


def ensure_uvs(objects: list[bpy.types.Object]) -> list[str]:
    generated: list[str] = []
    for obj in objects:
        if obj.data.uv_layers:
            continue
        select_only(obj)
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.uv.smart_project(
            angle_limit=math.radians(66.0),
            island_margin=0.02,
            area_weight=0.0,
            correct_aspect=True,
            scale_to_bounds=False,
        )
        bpy.ops.object.mode_set(mode="OBJECT")
        if not obj.data.uv_layers:
            raise RuntimeError(f"UV generation failed for {obj.name}")
        obj.data.uv_layers.active.name = "UVMap"
        generated.append(obj.name)
    return generated


def recalculate_normals(objects: list[bpy.types.Object]) -> None:
    for obj in objects:
        mesh = bmesh.new()
        mesh.from_mesh(obj.data)
        bmesh.ops.recalc_face_normals(mesh, faces=mesh.faces)
        mesh.to_mesh(obj.data)
        mesh.free()
        obj.data.update()


def bounds(objects: list[bpy.types.Object]) -> tuple[Vector, Vector]:
    points = [
        obj.matrix_world @ vertex.co
        for obj in objects
        for vertex in obj.data.vertices
    ]
    minimum = Vector(
        tuple(min(point[index] for point in points) for index in range(3))
    )
    maximum = Vector(
        tuple(max(point[index] for point in points) for index in range(3))
    )
    return minimum, maximum


def validate(objects: list[bpy.types.Object]) -> tuple[Vector, list[str]]:
    materials = sorted(
        {
            material.name
            for obj in objects
            for material in obj.data.materials
            if material is not None
        }
    )
    if set(materials) != EXPECTED_MATERIALS:
        raise RuntimeError(f"Unexpected production materials: {materials}")
    uvless = [obj.name for obj in objects if not obj.data.uv_layers]
    if uvless:
        raise RuntimeError(f"Production meshes still lack UVs: {uvless}")
    minimum, maximum = bounds(objects)
    size = maximum - minimum
    if abs(size.x - 1.5) > 0.0005 or abs(size.y - 1.5) > 0.0005:
        raise RuntimeError(f"Unexpected production bounds: {tuple(size)}")
    return size, materials


def select_objects(objects: list[bpy.types.Object]) -> None:
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]


def export_files(output_dir: Path, objects: list[bpy.types.Object]) -> list[Path]:
    fbx = output_dir / f"{PRODUCTION_ROOT_NAME}.fbx"
    glb = output_dir / f"{PRODUCTION_ROOT_NAME}.glb"
    select_objects(objects)
    bpy.ops.export_scene.fbx(
        filepath=str(fbx),
        use_selection=True,
        object_types={"MESH"},
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        use_space_transform=True,
        bake_space_transform=False,
        axis_forward="-Z",
        axis_up="Y",
        mesh_smooth_type="FACE",
        use_mesh_modifiers=True,
        use_tspace=True,
        add_leaf_bones=False,
        path_mode="AUTO",
        embed_textures=False,
    )
    select_objects(objects)
    bpy.ops.export_scene.gltf(
        filepath=str(glb),
        use_selection=True,
        export_format="GLB",
        export_apply=True,
        export_materials="EXPORT",
        export_yup=True,
    )
    return [fbx, glb]


def main() -> None:
    args = arguments()
    project_root = args.project_root.resolve()
    output_dir = project_root / "SourceArt" / "3D" / "MahjongTableProduction"
    output_dir.mkdir(parents=True, exist_ok=True)

    objects = mesh_objects()
    triangulate(objects)
    generated_uvs = ensure_uvs(objects)
    recalculate_normals(objects)
    size, materials = validate(objects)

    root = bpy.data.objects.get("SM_AutomaticMahjongTable_Review")
    if root:
        root.name = PRODUCTION_ROOT_NAME
        root["asset_status"] = "approved_for_unreal_import"
        root["source_review"] = (
            "SourceArt/3D/MahjongTableReview/"
            "SM_AutomaticMahjongTable_Review.blend"
        )

    blend = output_dir / f"{PRODUCTION_ROOT_NAME}.blend"
    bpy.ops.wm.save_as_mainfile(filepath=str(blend), check_existing=False)
    exports = export_files(output_dir, objects)

    files = [blend, *exports]
    manifest = {
        "status": "approved_for_unreal_import",
        "generator": Path(__file__).name,
        "blender_version": bpy.app.version_string,
        "source_review": (
            "SourceArt/3D/MahjongTableReview/"
            "SM_AutomaticMahjongTable_Review.blend"
        ),
        "outer_size_mm": [round(size.x * 1000.0, 4), round(size.y * 1000.0, 4)],
        "height_mm": round(size.z * 1000.0, 4),
        "mesh_object_count": len(objects),
        "triangle_count": sum(len(obj.data.polygons) for obj in objects),
        "materials": materials,
        "generated_uv_objects": generated_uvs,
        "uvless_objects": [
            obj.name for obj in objects if not obj.data.uv_layers
        ],
        "files": [
            {
                "name": path.name,
                "bytes": path.stat().st_size,
                "sha256": sha256(path),
            }
            for path in files
        ],
    }
    manifest_path = output_dir / "AutomaticMahjongTableProductionManifest.json"
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    print(
        "AUTOMATIC_MAHJONG_TABLE_PRODUCTION_OK",
        f"blender={bpy.app.version_string}",
        f"size_mm=({size.x * 1000.0:.3f},{size.y * 1000.0:.3f},{size.z * 1000.0:.3f})",
        f"triangles={manifest['triangle_count']}",
        f"materials={len(materials)}",
        f"generated_uvs={len(generated_uvs)}",
        f"manifest={manifest_path}",
    )


if __name__ == "__main__":
    main()
