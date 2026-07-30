# 配置 Blender 移动麻将桌人工审查视口、材质预览和可见对象，不保存或改写 blend 场景。
# 目标窗口需由人工确认；脚本失败只影响视口状态，不应导出任何生产资产。
"""Configure the live Blender review viewport without changing the blend."""

import bpy


def configure_live_viewport():
    controllers = [
        obj
        for obj in bpy.context.scene.objects
        if obj.name.startswith("Controller_")
    ]
    if not controllers:
        raise RuntimeError("No center-controller objects were found")
    bpy.ops.object.select_all(action="DESELECT")
    for obj in controllers:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = bpy.data.objects.get(
        "Controller_DirectionDisplay"
    )

    configured = 0
    for window in bpy.context.window_manager.windows:
        screen = window.screen
        for area in screen.areas:
            if area.type != "VIEW_3D":
                continue
            region = next(
                (
                    candidate
                    for candidate in area.regions
                    if candidate.type == "WINDOW"
                ),
                None,
            )
            if region is None:
                continue
            space = area.spaces.active
            space.shading.type = "MATERIAL"
            space.overlay.show_floor = False
            space.overlay.show_axis_x = False
            space.overlay.show_axis_y = False
            space.overlay.show_axis_z = False
            with bpy.context.temp_override(
                window=window,
                screen=screen,
                area=area,
                region=region,
            ):
                bpy.ops.view3d.view_axis(
                    type="TOP",
                    align_active=False,
                    relative=False,
                )
                bpy.ops.view3d.view_selected(
                    use_all_regions=False,
                )
            space.region_3d.view_distance *= 1.35
            configured += 1
    print(
        "MOBILE_MAHJONG_TABLE_LIVE_REVIEW_VIEWPORT_OK",
        f"configured_viewports={configured}",
    )
    return None


bpy.app.timers.register(configure_live_viewport, first_interval=1.0)
