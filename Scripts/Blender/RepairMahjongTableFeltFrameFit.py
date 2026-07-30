# 扩展目标麻将桌现有毛毡至木框下方，消除周边露缝并保持桌面有效尺寸不变。
# 仅修改指定毛毡网格；修复后必须重新导出目标资产并复核 UV、碰撞和边界。
"""Extend the existing felt under the wood frame to remove the visible perimeter gap."""

from pathlib import Path

import bpy


OUTPUT_DIR = Path(__file__).resolve().parents[2] / "SourceArt" / "3D" / "MahjongTable"
BLEND_PATH = OUTPUT_DIR / "SM_StandardMahjongTable.blend"
FBX_PATH = OUTPUT_DIR / "SM_StandardMahjongTable.fbx"
GLB_PATH = OUTPUT_DIR / "SM_StandardMahjongTable.glb"
PREVIEW_PATH = OUTPUT_DIR / "StandardMahjongTable_Preview.png"
CLOSEUP_PATH = OUTPUT_DIR / "StandardMahjongTable_FeltFrameFit_Closeup.png"
FRAME_NAME = "SM_Mahjong_Table_Frame_MiterJoint"
FELT_NAME = "Mahjong_Felt_Surface"

EXPECTED_OLD_HALF_EXTENT_M = 0.483
TARGET_HALF_EXTENT_M = 0.492
FRAME_INNER_TOP_HALF_EXTENT_M = 0.485
MIN_HIDDEN_OVERLAP_M = 0.006


def world_half_extents(obj: bpy.types.Object) -> tuple[float, float]:
    vertices = [obj.matrix_world @ vertex.co for vertex in obj.data.vertices]
    return (
        max(abs(vertex.x) for vertex in vertices),
        max(abs(vertex.y) for vertex in vertices),
    )


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

    old_x, old_y = world_half_extents(felt)
    if (
        abs(old_x - EXPECTED_OLD_HALF_EXTENT_M) > 0.0001
        or abs(old_y - EXPECTED_OLD_HALF_EXTENT_M) > 0.0001
    ):
        raise RuntimeError(
            f"Unexpected pre-repair felt half extents: ({old_x:.6f}, {old_y:.6f})"
        )

    # Scale only the existing felt vertices in XY. No filler mesh or extra faces are added.
    scale_x = TARGET_HALF_EXTENT_M / old_x
    scale_y = TARGET_HALF_EXTENT_M / old_y
    for vertex in felt.data.vertices:
        vertex.co.x *= scale_x
        vertex.co.y *= scale_y
    felt.data.update()

    new_x, new_y = world_half_extents(felt)
    overlap_x = new_x - FRAME_INNER_TOP_HALF_EXTENT_M
    overlap_y = new_y - FRAME_INNER_TOP_HALF_EXTENT_M
    if min(overlap_x, overlap_y) < MIN_HIDDEN_OVERLAP_M:
        raise RuntimeError(
            f"Insufficient felt/frame overlap: ({overlap_x:.6f}, {overlap_y:.6f})"
        )
    if len(felt.data.vertices) != 130 or len(felt.data.polygons) != 192:
        raise RuntimeError("Felt topology changed; filler geometry must not be introduced")
    if len(frame.data.vertices) != 1232 or len(frame.data.polygons) != 2432:
        raise RuntimeError("Wood frame topology changed unexpectedly")

    felt["frame_fit"] = "hidden_overlap_under_inner_wood_lip"
    felt["finished_size_mm"] = "984x984"
    felt["hidden_overlap_each_side_mm"] = round(min(overlap_x, overlap_y) * 1000.0, 3)
    felt["filler_geometry"] = False
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

    original_camera = bpy.context.scene.camera
    camera_data = bpy.data.cameras.new("FeltFrameFitValidationCamera")
    camera_data.type = "ORTHO"
    camera_data.ortho_scale = 0.28
    camera = bpy.data.objects.new("FeltFrameFitValidationCamera", camera_data)
    bpy.context.scene.collection.objects.link(camera)
    camera.location = (0.47, 0.47, 1.0)
    camera.rotation_euler = (0.0, 0.0, 0.0)
    bpy.context.scene.camera = camera
    bpy.context.scene.render.resolution_x = 900
    bpy.context.scene.render.resolution_y = 900
    bpy.context.scene.render.filepath = str(CLOSEUP_PATH)
    bpy.ops.render.render(write_still=True)
    bpy.context.scene.camera = original_camera
    bpy.data.objects.remove(camera, do_unlink=True)
    bpy.data.cameras.remove(camera_data)

    print(
        "MAHJONG_TABLE_FELT_FRAME_FIT_OK",
        f"old_size_mm=({old_x * 2000.0:.3f},{old_y * 2000.0:.3f})",
        f"new_size_mm=({new_x * 2000.0:.3f},{new_y * 2000.0:.3f})",
        f"hidden_overlap_mm={min(overlap_x, overlap_y) * 1000.0:.3f}",
        "filler_geometry=false",
        f"fbx={FBX_PATH}",
    )


if __name__ == "__main__":
    main()
