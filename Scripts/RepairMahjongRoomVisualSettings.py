# 应用 schema-v6 夜间桌面相机、曝光和灯光默认值，使预览与批准视觉基线一致。
# 只修改目标预览/表现资产；保存前验证参数范围和移动端成本，不覆盖玩法相机。
"""Apply the schema-v6 nighttime tabletop camera, exposure and lighting defaults."""

import unreal


BLUEPRINT_PATH = "/Game/Client/Room/Presentation/BP_MahjongRoomPresentation"
TABLE_MESH_PATH = "/Game/Art/Mahjong/Table/Meshes/SM_StandardMahjongTable"
TABLE_CLASS_PATH = "/Script/GuiyangMahjongClient.Mahjong3DTableActor"
SCHEMA_METADATA_TAG = "MahjongPresentationSchemaVersion"
SCHEMA_VERSION = "7"


def find_component_template(subsystem, blueprint, variable_name):
    for handle in subsystem.k2_gather_subobject_data_for_blueprint(blueprint):
        data = unreal.SubobjectDataBlueprintFunctionLibrary.get_data(handle)
        name = unreal.SubobjectDataBlueprintFunctionLibrary.get_variable_name(data)
        if str(name) != variable_name:
            continue
        return unreal.SubobjectDataBlueprintFunctionLibrary.get_object_for_blueprint(
            data, blueprint
        )
    return None


def set_properties(component, properties):
    for name, value in properties.items():
        component.set_editor_property(name, value)


blueprint = unreal.EditorAssetLibrary.load_asset(BLUEPRINT_PATH)
if not blueprint:
    raise RuntimeError(f"Could not load {BLUEPRINT_PATH}")

subsystem = unreal.get_engine_subsystem(unreal.SubobjectDataSubsystem)
camera = find_component_template(subsystem, blueprint, "MahjongRoomCamera")
table_mesh_component = find_component_template(
    subsystem, blueprint, "MahjongTableMesh"
)
tile_layout = find_component_template(subsystem, blueprint, "MahjongTileLayout")
directional = find_component_template(subsystem, blueprint, "BP_DirectionalLight")
sky = find_component_template(subsystem, blueprint, "BP_SkyLight")
key = find_component_template(subsystem, blueprint, "BP_KeyLight")
fill = find_component_template(subsystem, blueprint, "BP_FillLight")
top_soft = find_component_template(subsystem, blueprint, "BP_TopSoftLight")
rim = find_component_template(subsystem, blueprint, "BP_RimLight")
table_mesh = unreal.EditorAssetLibrary.load_asset(TABLE_MESH_PATH)
table_class = unreal.load_class(None, TABLE_CLASS_PATH)
if not all(
    (
        camera,
        table_mesh_component,
        tile_layout,
        table_mesh,
        table_class,
        directional,
        sky,
        key,
        fill,
        top_soft,
        rim,
    )
):
    raise RuntimeError("Presentation Blueprint is missing a required visual component")

table_mesh_component.set_editor_property("static_mesh", table_mesh)
table_mesh_component.set_editor_property(
    "relative_scale3d",
    unreal.Vector(1.0, 1.0, 1.0),
)
tile_layout.set_editor_property("child_actor_class", table_class)

set_properties(
    camera,
    {
        "relative_location": unreal.Vector(0.0, -160.3494, 107.5),
        "relative_rotation": unreal.Rotator(0.0, -30.0, 90.0),
        "current_focal_length": 30.0,
        "current_aperture": 16.0,
        "constrain_aspect_ratio": False,
        "post_process_blend_weight": 1.0,
    },
)
focus_settings = camera.get_editor_property("focus_settings")
focus_settings.set_editor_property(
    "focus_method", unreal.CameraFocusMethod.DISABLE
)
camera.set_editor_property("focus_settings", focus_settings)
filmback = camera.get_editor_property("filmback")
filmback.set_editor_property("sensor_vertical_offset", -2.0)
camera.set_editor_property("filmback", filmback)
post_process = camera.get_editor_property("post_process_settings")
set_properties(
    post_process,
    {
        "override_auto_exposure_method": True,
        "auto_exposure_method": unreal.AutoExposureMethod.AEM_MANUAL,
        "override_auto_exposure_apply_physical_camera_exposure": True,
        "auto_exposure_apply_physical_camera_exposure": False,
        "override_auto_exposure_min_brightness": True,
        "auto_exposure_min_brightness": 1.0,
        "override_auto_exposure_max_brightness": True,
        "auto_exposure_max_brightness": 1.0,
        "override_auto_exposure_bias": True,
        "auto_exposure_bias": 9.5,
        "override_bloom_intensity": True,
        "bloom_intensity": 0.0,
        "override_lens_flare_intensity": True,
        "lens_flare_intensity": 0.0,
        "override_motion_blur_amount": True,
        "motion_blur_amount": 0.0,
        "override_sharpen": True,
        "sharpen": 0.75,
    },
)
camera.set_editor_property("post_process_settings", post_process)

set_properties(
    directional,
    {
        "visible": True,
        "intensity": 0.015,
        "use_temperature": True,
        "temperature": 9000.0,
        "light_color": unreal.Color(r=255, g=255, b=255, a=255),
        "cast_shadows": True,
        "light_source_angle": 3.0,
        "atmosphere_sun_light": False,
        "indirect_lighting_intensity": 0.45,
        "volumetric_scattering_intensity": 0.2,
        "forward_shading_priority": 1,
    },
)
set_properties(
    sky,
    {
        "intensity": 0.025,
        "light_color": unreal.Color(r=165, g=190, b=230, a=255),
        "cast_shadows": True,
        "real_time_capture": False,
        "lower_hemisphere_is_black": False,
        "lower_hemisphere_color": unreal.LinearColor(
            r=0.012, g=0.010, b=0.009, a=1.0
        ),
        "indirect_lighting_intensity": 0.9,
        "transmission": True,
    },
)
set_properties(
    key,
    {
        "visible": True,
        "relative_location": unreal.Vector(-180.0, -160.0, 260.0),
        "relative_rotation": unreal.Rotator(pitch=-42.0, yaw=42.0, roll=0.0),
        "intensity": 90.0,
        "attenuation_radius": 600.0,
        "source_width": 160.0,
        "source_height": 120.0,
        "use_temperature": True,
        "temperature": 7200.0,
        "light_color": unreal.Color(r=255, g=255, b=255, a=255),
        "cast_shadows": True,
        "volumetric_scattering_intensity": 0.2,
        "specular_scale": 1.0,
        "indirect_lighting_intensity": 1.0,
        "contact_shadow_length": 0.05,
        "shadow_bias": 0.4,
        "shadow_slope_bias": 0.4,
        "shadow_sharpen": 0.1,
    },
)
set_properties(
    fill,
    {
        "relative_location": unreal.Vector(180.0, -120.0, 210.0),
        "relative_rotation": unreal.Rotator(pitch=-35.0, yaw=-50.0, roll=0.0),
        "intensity": 20.0,
        "attenuation_radius": 550.0,
        "source_width": 200.0,
        "source_height": 150.0,
        "use_temperature": True,
        "temperature": 6800.0,
        "light_color": unreal.Color(r=255, g=255, b=255, a=255),
        "cast_shadows": False,
        "volumetric_scattering_intensity": 0.0,
        "specular_scale": 0.65,
        "indirect_lighting_intensity": 0.7,
    },
)
set_properties(
    top_soft,
    {
        "relative_location": unreal.Vector(0.0, 0.0, 300.0),
        "relative_rotation": unreal.Rotator(pitch=-90.0, yaw=0.0, roll=0.0),
        "intensity": 35.0,
        "attenuation_radius": 600.0,
        "source_width": 230.0,
        "source_height": 230.0,
        "use_temperature": True,
        "temperature": 6800.0,
        "light_color": unreal.Color(r=255, g=255, b=255, a=255),
        "cast_shadows": True,
        "volumetric_scattering_intensity": 0.1,
        "specular_scale": 0.85,
        "indirect_lighting_intensity": 0.9,
        "contact_shadow_length": 0.05,
        "shadow_bias": 0.4,
        "shadow_slope_bias": 0.4,
        "shadow_sharpen": 0.1,
    },
)
set_properties(
    rim,
    {
        "relative_location": unreal.Vector(0.0, 190.0, 240.0),
        "relative_rotation": unreal.Rotator(pitch=-35.0, yaw=180.0, roll=0.0),
        "intensity": 12.5,
        "attenuation_radius": 500.0,
        "source_width": 150.0,
        "source_height": 100.0,
        "use_temperature": True,
        "temperature": 8500.0,
        "light_color": unreal.Color(r=255, g=255, b=255, a=255),
        "cast_shadows": False,
        "volumetric_scattering_intensity": 0.0,
        "specular_scale": 1.0,
        "indirect_lighting_intensity": 0.2,
    },
)

unreal.EditorAssetLibrary.set_metadata_tag(
    blueprint, SCHEMA_METADATA_TAG, SCHEMA_VERSION
)
unreal.BlueprintEditorLibrary.compile_blueprint(blueprint)
unreal.EditorAssetLibrary.save_loaded_asset(blueprint, only_if_is_dirty=False)
unreal.log(
    "MAHJONG_ROOM_VISUAL_SETTINGS_REPAIRED "
    "camera=(0,-160.3494,107.5) tabletop-normal-angle=60 pitch=-30 focal=30 exposure=manual-night-ev100-9.5 "
    "night-lights-cool=(directional-9000K,sky-blue,key-7200K,fill-6800K,top-6800K,rim-8500K)"
)
