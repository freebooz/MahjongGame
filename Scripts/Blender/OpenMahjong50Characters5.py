# 打开 Mahjong50 主模型并聚焦五万牌，用于检查雕刻方向、法线和材质绑定。
# 该脚本只控制 Blender 显示选择，不保存模型或覆盖当前生产导出。
"""Open the Mahjong50 master model in Blender and display Characters_5."""

from __future__ import annotations

import bpy
from mathutils import Vector


ASSET_NAME = "SM_Mahjong50"
DISPLAY_TILE = "Characters_5"
ATLAS_WIDTH = 8192.0
ATLAS_HEIGHT = 4096.0
CELL_HEIGHT = 1024.0
SLOT_WIDTH = 896.0
LEFT_MARGIN = 160.0
COLUMN = 4
ROW_FROM_BOTTOM = 3


asset = bpy.data.objects.get(ASSET_NAME)
if asset is None:
    raise RuntimeError(f"Could not find {ASSET_NAME} in the opened blend file")

face_material = bpy.data.materials.get("M_Mahjong50_FaceAtlas")
if face_material is None or not face_material.use_nodes:
    raise RuntimeError("Could not find the Mahjong50 face-atlas material")

mapping = face_material.node_tree.nodes.get("AtlasUV_Mapping")
if mapping is None:
    raise RuntimeError("Face material has no AtlasUV_Mapping node")

offset_u = (LEFT_MARGIN + COLUMN * SLOT_WIDTH) / ATLAS_WIDTH
offset_v = ROW_FROM_BOTTOM * CELL_HEIGHT / ATLAS_HEIGHT
mapping.inputs["Location"].default_value = (offset_u, offset_v, 0.0)
face_material["DefaultTile"] = DISPLAY_TILE
face_material["DefaultTileColumn"] = COLUMN
face_material["DefaultTileRowFromBottom"] = ROW_FROM_BOTTOM
bpy.context.scene["InteractiveDisplayTile"] = DISPLAY_TILE

bpy.ops.object.select_all(action="DESELECT")
asset.hide_set(False)
asset.select_set(True)
bpy.context.view_layer.objects.active = asset

screen = bpy.context.screen
if screen is not None:
    target = Vector((0.0, 0.0, 0.025))
    # Close three-quarter inspection angle so bevels and engraving gradients
    # occupy enough screen pixels to judge in Material Preview.
    eye = Vector((0.035, 0.075, 0.055))
    for area in screen.areas:
        if area.type != "VIEW_3D":
            continue
        space = area.spaces.active
        space.shading.type = "MATERIAL"
        space.shading.use_scene_world = False
        space.overlay.show_outline_selected = True
        region_3d = space.region_3d
        region_3d.view_location = target
        region_3d.view_distance = (eye - target).length
        region_3d.view_rotation = (target - eye).to_track_quat("-Z", "Y")

print(
    f"[Mahjong50Open] Displaying {DISPLAY_TILE}; "
    f"atlas offset=({offset_u:.6f}, {offset_v:.6f})"
)
