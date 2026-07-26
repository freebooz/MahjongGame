"""Save the review blend with material preview and a controller close-up."""

from mathutils import Quaternion, Vector

import bpy


configured = 0
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type != "VIEW_3D":
            continue
        space = area.spaces.active
        space.shading.type = "MATERIAL"
        space.shading.light = "STUDIO"
        space.shading.studiolight_rotate_z = 0.35
        space.shading.studiolight_background_alpha = 0.25
        space.overlay.show_floor = False
        space.overlay.show_axis_x = False
        space.overlay.show_axis_y = False
        space.overlay.show_axis_z = False
        region = space.region_3d
        region.view_location = Vector((0.0, 0.0, 0.0))
        region.view_rotation = Quaternion((1.0, 0.0, 0.0, 0.0))
        region.view_distance = 0.46
        region.view_perspective = "ORTHO"
        configured += 1

if configured == 0:
    raise RuntimeError("No Blender 3D viewport was available to configure")

for obj in bpy.context.scene.objects:
    obj.select_set(obj.name.startswith("Controller_"))
bpy.context.view_layer.objects.active = bpy.data.objects.get(
    "Controller_DirectionDisplay"
)

bpy.context.scene["review_viewport"] = (
    "material_preview_controller_top_closeup"
)
bpy.ops.wm.save_as_mainfile(
    filepath=bpy.data.filepath,
    check_existing=False,
)
print(
    "MOBILE_MAHJONG_TABLE_REVIEW_VIEWPORT_OK",
    f"configured_viewports={configured}",
    f"file={bpy.data.filepath}",
)
