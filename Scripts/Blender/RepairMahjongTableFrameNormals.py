# 修复当前麻将桌框架朝外法线并重新生成目标导出，避免 Unreal 中出现背面剔除和错误高光。
# 处理范围限定到桌框网格；导出前校验非流形面和负缩放，不修改无关对象。
"""Repair outward normals on the current Mahjong table and regenerate exports."""

from pathlib import Path

import bmesh
import bpy


OUTPUT_DIR = Path(__file__).resolve().parents[2] / "SourceArt" / "3D" / "MahjongTable"
BLEND_PATH = OUTPUT_DIR / "SM_StandardMahjongTable.blend"
FBX_PATH = OUTPUT_DIR / "SM_StandardMahjongTable.fbx"
GLB_PATH = OUTPUT_DIR / "SM_StandardMahjongTable.glb"
PREVIEW_PATH = OUTPUT_DIR / "StandardMahjongTable_Preview.png"
FRAME_NAME = "SM_Mahjong_Table_Frame_MiterJoint"
FELT_NAME = "Mahjong_Felt_Surface"


def signed_island_volumes(obj: bpy.types.Object) -> list[float]:
    duplicate = obj.copy()
    duplicate.data = obj.data.copy()
    bpy.context.scene.collection.objects.link(duplicate)
    bpy.ops.object.select_all(action="DESELECT")
    duplicate.select_set(True)
    bpy.context.view_layer.objects.active = duplicate
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.separate(type="LOOSE")
    bpy.ops.object.mode_set(mode="OBJECT")
    parts = list(bpy.context.selected_objects)
    volumes: list[float] = []
    for part in parts:
        mesh = bmesh.new()
        mesh.from_mesh(part.data)
        volumes.append(mesh.calc_volume(signed=True))
        mesh.free()
    for part in parts:
        bpy.data.objects.remove(part, do_unlink=True)
    return volumes


def recalculate_outside(obj: bpy.types.Object) -> None:
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.normals_make_consistent(inside=False)
    bpy.ops.object.mode_set(mode="OBJECT")
    obj.data.update()


def select_export_meshes(objects: list[bpy.types.Object]) -> None:
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]


def main() -> None:
    frame = bpy.data.objects.get(FRAME_NAME)
    felt = bpy.data.objects.get(FELT_NAME)
    if not frame or frame.type != "MESH":
        raise RuntimeError(f"Missing frame mesh {FRAME_NAME}")
    if not felt or felt.type != "MESH":
        raise RuntimeError(f"Missing felt mesh {FELT_NAME}")

    before = signed_island_volumes(frame)
    recalculate_outside(frame)
    after = signed_island_volumes(frame)
    if not before or not all(volume < 0.0 for volume in before):
        raise RuntimeError(f"Unexpected pre-repair frame volumes: {before}")
    if not all(volume > 0.0 for volume in after):
        raise RuntimeError(f"Frame normals are not all outward after repair: {after}")

    frame["normal_orientation"] = "outward"
    frame["normal_repair"] = "recalculate_outside_all_loose_islands"
    frame["joint_fill_geometry"] = False

    bpy.ops.wm.save_as_mainfile(filepath=str(BLEND_PATH), check_existing=False)
    objects = [frame, felt]
    select_export_meshes(objects)
    bpy.ops.export_scene.fbx(
        filepath=str(FBX_PATH),
        use_selection=True,
        object_types={"MESH"},
        use_mesh_modifiers=True,
        mesh_smooth_type="FACE",
        use_tspace=True,
        add_leaf_bones=False,
        bake_anim=False,
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        axis_forward="-Y",
        axis_up="Z",
    )
    select_export_meshes(objects)
    bpy.ops.export_scene.gltf(
        filepath=str(GLB_PATH),
        export_format="GLB",
        use_selection=True,
        export_apply=True,
        export_materials="EXPORT",
        export_yup=True,
    )

    bpy.context.scene.render.engine = "BLENDER_EEVEE"
    bpy.context.scene.render.resolution_x = 1280
    bpy.context.scene.render.resolution_y = 720
    bpy.context.scene.render.resolution_percentage = 100
    bpy.context.scene.render.filepath = str(PREVIEW_PATH)
    bpy.ops.render.render(write_still=True)

    print(
        "MAHJONG_TABLE_NORMAL_REPAIR_OK",
        f"islands={len(after)}",
        f"min_signed_volume={min(after):.9f}",
        f"fbx={FBX_PATH}",
    )


if __name__ == "__main__":
    main()
