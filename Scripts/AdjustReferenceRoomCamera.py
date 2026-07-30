# 按 300 cm 实体桌面尺寸调整参考房间相机位置、焦距和构图，用于可重复视觉审查。
# 输入为现有预览场景；不得修改游戏相机逻辑，无法找到目标相机时立即失败。
"""Apply the reference composition to the physical 300 cm square tabletop."""

import unreal


BLUEPRINT_PATH = "/Game/Client/Room/Presentation/BP_MahjongRoomPresentation"
TABLE_MESH_PATH = "/Game/Art/Mahjong/Table/Meshes/SM_StandardMahjongTable"
TABLE_AUTHORED_SIZE_CM = 115.0
TABLE_RUNTIME_SIZE_CM = 300.0
TABLE_RUNTIME_SCALE = TABLE_RUNTIME_SIZE_CM / TABLE_AUTHORED_SIZE_CM


def find_component_template(subsystem, blueprint, variable_name):
    for handle in subsystem.k2_gather_subobject_data_for_blueprint(blueprint):
        data = unreal.SubobjectDataBlueprintFunctionLibrary.get_data(handle)
        name = unreal.SubobjectDataBlueprintFunctionLibrary.get_variable_name(data)
        if str(name) == variable_name:
            return unreal.SubobjectDataBlueprintFunctionLibrary.get_object_for_blueprint(
                data, blueprint
            )
    return None


blueprint = unreal.EditorAssetLibrary.load_asset(BLUEPRINT_PATH)
if not blueprint:
    raise RuntimeError(f"Could not load {BLUEPRINT_PATH}")

subsystem = unreal.get_engine_subsystem(unreal.SubobjectDataSubsystem)
camera = find_component_template(subsystem, blueprint, "MahjongRoomCamera")
table = find_component_template(subsystem, blueprint, "MahjongTableMesh")
directional = find_component_template(subsystem, blueprint, "BP_DirectionalLight")
sky = find_component_template(subsystem, blueprint, "BP_SkyLight")
key = find_component_template(subsystem, blueprint, "BP_KeyLight")
fill = find_component_template(subsystem, blueprint, "BP_FillLight")
top_soft = find_component_template(subsystem, blueprint, "BP_TopSoftLight")
rim = find_component_template(subsystem, blueprint, "BP_RimLight")
if not camera or not table:
    raise RuntimeError("MahjongRoomCamera or MahjongTableMesh component was not found")

table_mesh = unreal.EditorAssetLibrary.load_asset(TABLE_MESH_PATH)
if not table_mesh:
    raise RuntimeError(f"Could not load {TABLE_MESH_PATH}")
table.set_editor_property("static_mesh", table_mesh)
table.set_editor_property(
    "relative_scale3d",
    unreal.Vector(TABLE_RUNTIME_SCALE, TABLE_RUNTIME_SCALE, TABLE_RUNTIME_SCALE),
)
if directional:
    directional.set_editor_property("visible", True)
    directional.set_editor_property("intensity", 0.015)
    directional.set_editor_property("use_temperature", True)
    directional.set_editor_property("temperature", 9000.0)
    directional.set_editor_property("cast_shadows", True)
if sky:
    sky.set_editor_property("intensity", 0.025)
    sky.set_editor_property(
        "light_color", unreal.Color(r=165, g=190, b=230, a=255)
    )
    sky.set_editor_property("cast_shadows", True)
if key:
    key.set_editor_property("relative_location", unreal.Vector(-180.0, -160.0, 260.0))
    key.set_editor_property("intensity", 90.0)
    key.set_editor_property("attenuation_radius", 600.0)
    key.set_editor_property("use_temperature", True)
    key.set_editor_property("temperature", 7200.0)
    key.set_editor_property("cast_shadows", True)
if fill:
    fill.set_editor_property(
        "relative_location", unreal.Vector(180.0, -120.0, 210.0)
    )
    fill.set_editor_property("intensity", 20.0)
    fill.set_editor_property("attenuation_radius", 550.0)
    fill.set_editor_property("use_temperature", True)
    fill.set_editor_property("temperature", 6800.0)
    fill.set_editor_property("cast_shadows", False)
if top_soft:
    top_soft.set_editor_property("relative_location", unreal.Vector(0.0, 0.0, 300.0))
    top_soft.set_editor_property("intensity", 35.0)
    top_soft.set_editor_property("attenuation_radius", 600.0)
    top_soft.set_editor_property("use_temperature", True)
    top_soft.set_editor_property("temperature", 6800.0)
    top_soft.set_editor_property("cast_shadows", True)
if rim:
    rim.set_editor_property("relative_location", unreal.Vector(0.0, 190.0, 240.0))
    rim.set_editor_property("intensity", 25.0)
    rim.set_editor_property("attenuation_radius", 500.0)
    rim.set_editor_property("use_temperature", True)
    rim.set_editor_property("temperature", 8500.0)
    rim.set_editor_property("cast_shadows", False)

camera.set_editor_property(
    "relative_location", unreal.Vector(0.0, -160.3494, 107.5)
)
camera.set_editor_property(
    "relative_rotation", unreal.Rotator(0.0, -30.0, 90.0)
)
camera.set_editor_property("current_focal_length", 30.0)
camera.set_editor_property("constrain_aspect_ratio", False)
post_process = camera.get_editor_property("post_process_settings")
post_process.set_editor_property("override_auto_exposure_method", True)
post_process.set_editor_property(
    "auto_exposure_method", unreal.AutoExposureMethod.AEM_MANUAL
)
post_process.set_editor_property(
    "override_auto_exposure_apply_physical_camera_exposure", True
)
post_process.set_editor_property(
    "auto_exposure_apply_physical_camera_exposure", False
)
post_process.set_editor_property("override_auto_exposure_min_brightness", True)
post_process.set_editor_property("auto_exposure_min_brightness", 1.0)
post_process.set_editor_property("override_auto_exposure_max_brightness", True)
post_process.set_editor_property("auto_exposure_max_brightness", 1.0)
post_process.set_editor_property("override_auto_exposure_bias", True)
post_process.set_editor_property("auto_exposure_bias", 9.5)
camera.set_editor_property("post_process_settings", post_process)

unreal.BlueprintEditorLibrary.compile_blueprint(blueprint)
unreal.EditorAssetLibrary.save_loaded_asset(blueprint, only_if_is_dirty=False)
unreal.log(
    f"REFERENCE_ROOM_CAMERA_ADJUSTED table=300x300cm scale={TABLE_RUNTIME_SCALE:.6f} "
    "location=(0,-202,120) table-normal-angle=60 pitch=-30 focal=30"
)
