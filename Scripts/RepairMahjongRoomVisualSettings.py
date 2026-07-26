"""Apply the schema-v3 tabletop camera, exposure and lighting defaults."""

import unreal


BLUEPRINT_PATH = "/Game/Client/Room/Presentation/BP_MahjongRoomPresentation"
TABLE_MESH_PATH = "/Game/Art/Mahjong/Table/Meshes/SM_StandardMahjongTable"
TABLE_CLASS_PATH = "/Script/GuiyangMahjongClient.Mahjong3DTableActor"
SCHEMA_METADATA_TAG = "MahjongPresentationSchemaVersion"
SCHEMA_VERSION = "5"


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
    )
):
    raise RuntimeError("Presentation Blueprint is missing a required visual component")

table_mesh_component.set_editor_property("static_mesh", table_mesh)
tile_layout.set_editor_property("child_actor_class", table_class)

set_properties(
    camera,
    {
        "relative_location": unreal.Vector(0.0, -2350.0, 1392.0),
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
        "auto_exposure_method": unreal.AutoExposureMethod.AEM_HISTOGRAM,
        "override_auto_exposure_apply_physical_camera_exposure": True,
        "auto_exposure_apply_physical_camera_exposure": False,
        "override_auto_exposure_bias": True,
        "auto_exposure_bias": -1.0,
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
        "visible": False,
        "intensity": 0.0,
        "light_color": unreal.Color(r=255, g=250, b=242, a=255),
        "cast_shadows": True,
    },
)
set_properties(
    sky,
    {
        "intensity": 0.15,
        "light_color": unreal.Color(r=199, g=219, b=255, a=255),
        "cast_shadows": False,
    },
)
set_properties(
    key,
    {
        "intensity": 400.0,
        "light_color": unreal.Color(r=255, g=248, b=238, a=255),
        "cast_shadows": True,
    },
)
set_properties(
    fill,
    {
        "relative_location": unreal.Vector(0.0, -1450.0, 520.0),
        "relative_rotation": unreal.Rotator(0.0, -26.6, 90.0),
        "intensity": 300.0,
        "attenuation_radius": 1800.0,
        "inner_cone_angle": 50.0,
        "outer_cone_angle": 70.0,
        "light_color": unreal.Color(r=230, g=240, b=255, a=255),
        "cast_shadows": False,
    },
)

unreal.EditorAssetLibrary.set_metadata_tag(
    blueprint, SCHEMA_METADATA_TAG, SCHEMA_VERSION
)
unreal.BlueprintEditorLibrary.compile_blueprint(blueprint)
unreal.EditorAssetLibrary.save_loaded_asset(blueprint, only_if_is_dirty=False)
unreal.log(
    "MAHJONG_ROOM_VISUAL_SETTINGS_REPAIRED "
    "camera=(0,-2350,1392) tabletop-normal-angle=60 pitch=-30 focal=30 exposure=histogram-1.0 "
    "lights=(disabled-map-duplicate,0.15,400,300) south_fill=(0,-1450,520)"
)
