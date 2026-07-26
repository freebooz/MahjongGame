"""Create the client presentation Blueprint and migrate the visual preview map to it."""

import unreal


ASSET_NAME = "BP_MahjongRoomPresentation"
ASSET_DIR = "/Game/Client/Room/Presentation"
ASSET_PATH = f"{ASSET_DIR}/{ASSET_NAME}"
GENERATED_CLASS_PATH = f"{ASSET_PATH}.{ASSET_NAME}_C"
LEGACY_LABEL_PATH = f"{ASSET_DIR}/PAL_MahjongRoomPresentation_Client"
MAP_PATH = "/Game/Maps/MahjongRoomVisualPreviewMap"
NATIVE_CLASS_PATH = "/Script/GuiyangMahjongClient.MahjongRoomPresentationActor"
TABLE_CLASS_PATH = "/Script/GuiyangMahjongClient.Mahjong3DTableActor"
SCHEMA_METADATA_TAG = "MahjongPresentationSchemaVersion"
SCHEMA_VERSION = "5"
TABLE_MESH_PATH = (
    "/Game/Art/Mahjong/Table/Meshes/"
    "SM_StandardMahjongTable.SM_StandardMahjongTable"
)


def get_subobject(subsystem, blueprint, handle):
    data = unreal.SubobjectDataBlueprintFunctionLibrary.get_data(handle)
    return unreal.SubobjectDataBlueprintFunctionLibrary.get_object_for_blueprint(
        data, blueprint
    )


def find_handle(subsystem, blueprint, variable_name):
    for handle in subsystem.k2_gather_subobject_data_for_blueprint(blueprint):
        data = unreal.SubobjectDataBlueprintFunctionLibrary.get_data(handle)
        if str(
            unreal.SubobjectDataBlueprintFunctionLibrary.get_variable_name(data)
        ) == variable_name:
            return handle
    return None


def find_actor_handle(subsystem, blueprint):
    for handle in subsystem.k2_gather_subobject_data_for_blueprint(blueprint):
        data = unreal.SubobjectDataBlueprintFunctionLibrary.get_data(handle)
        if unreal.SubobjectDataBlueprintFunctionLibrary.is_root_actor(data):
            return handle
    raise RuntimeError("Could not find Blueprint actor root handle")


def add_component(subsystem, blueprint, parent_handle, component_class, name):
    existing = find_handle(subsystem, blueprint, name)
    if existing:
        return existing, get_subobject(subsystem, blueprint, existing), False
    params = unreal.AddNewSubobjectParams()
    params.set_editor_property("parent_handle", parent_handle)
    params.set_editor_property("new_class", component_class)
    params.set_editor_property("blueprint_context", blueprint)
    handle, failure_reason = subsystem.add_new_subobject(params=params)
    if not unreal.SubobjectDataBlueprintFunctionLibrary.is_handle_valid(handle):
        raise RuntimeError(f"Could not add {name}: {failure_reason}")
    if not subsystem.rename_subobject(handle, unreal.Text(name)):
        raise RuntimeError(f"Could not rename new component to {name}")
    component = get_subobject(subsystem, blueprint, handle)
    if not component:
        raise RuntimeError(f"Could not resolve component template {name}")
    return handle, component, True


def configure_new_component(component, properties):
    for property_name, value in properties.items():
        component.set_editor_property(property_name, value)


def configure_tabletop_post_process(camera):
    settings = camera.get_editor_property("post_process_settings")
    settings.set_editor_property("override_auto_exposure_method", True)
    settings.set_editor_property(
        "auto_exposure_method", unreal.AutoExposureMethod.AEM_HISTOGRAM
    )
    settings.set_editor_property(
        "override_auto_exposure_apply_physical_camera_exposure", True
    )
    settings.set_editor_property(
        "auto_exposure_apply_physical_camera_exposure", False
    )
    settings.set_editor_property("override_auto_exposure_bias", True)
    settings.set_editor_property("auto_exposure_bias", -1.0)
    settings.set_editor_property("override_bloom_intensity", True)
    settings.set_editor_property("bloom_intensity", 0.0)
    settings.set_editor_property("override_lens_flare_intensity", True)
    settings.set_editor_property("lens_flare_intensity", 0.0)
    settings.set_editor_property("override_motion_blur_amount", True)
    settings.set_editor_property("motion_blur_amount", 0.0)
    settings.set_editor_property("override_sharpen", True)
    settings.set_editor_property("sharpen", 0.75)
    camera.set_editor_property("post_process_settings", settings)
    camera.set_editor_property("post_process_blend_weight", 1.0)


asset_tools = unreal.AssetToolsHelpers.get_asset_tools()
parent_class = unreal.load_class(None, NATIVE_CLASS_PATH)
if not parent_class:
    raise RuntimeError("MahjongRoomPresentationActor must be compiled before creating its Blueprint")

blueprint = (
    unreal.EditorAssetLibrary.load_asset(ASSET_PATH)
    if unreal.EditorAssetLibrary.does_asset_exist(ASSET_PATH)
    else None
)
if not blueprint:
    unreal.EditorAssetLibrary.make_directory(ASSET_DIR)
    factory = unreal.BlueprintFactory()
    factory.set_editor_property("parent_class", parent_class)
    blueprint = asset_tools.create_asset(ASSET_NAME, ASSET_DIR, None, factory)
    if not blueprint:
        raise RuntimeError(f"Could not create {ASSET_PATH}")
    unreal.log(f"MAHJONG_PRESENTATION_BLUEPRINT_CREATED={ASSET_PATH}")
else:
    unreal.log(f"MAHJONG_PRESENTATION_BLUEPRINT_REUSED={ASSET_PATH}")

# The native parent intentionally owns no scene components. Everything that affects
# composition or exposure lives in this Blueprint's SCS so a designer can select,
# move and tune it without recompiling C++.
subsystem = unreal.get_engine_subsystem(unreal.SubobjectDataSubsystem)
actor_handle = find_actor_handle(subsystem, blueprint)
root_handle, root_component, root_created = add_component(
    subsystem, blueprint, actor_handle, unreal.SceneComponent, "PresentationRoot"
)

table_mesh = unreal.EditorAssetLibrary.load_asset(TABLE_MESH_PATH.split(".")[0])
if not table_mesh:
    raise RuntimeError(f"Could not load table mesh {TABLE_MESH_PATH}")
_, table_component, table_created = add_component(
    subsystem,
    blueprint,
    root_handle,
    unreal.StaticMeshComponent,
    "MahjongTableMesh",
)
configure_new_component(
    table_component,
    {
        # Repair required references on every run without replacing artist transforms.
        "static_mesh": table_mesh,
    },
)
if table_created:
    configure_new_component(
        table_component,
        {
            "relative_location": unreal.Vector(0.0, 0.0, 0.0),
            "relative_scale3d": unreal.Vector(10.0, 10.0, 10.0),
            "cast_shadow": True,
            "mobility": unreal.ComponentMobility.MOVABLE,
        },
    )

table_class = unreal.load_class(None, TABLE_CLASS_PATH)
if not table_class:
    raise RuntimeError(f"Could not load table layout class {TABLE_CLASS_PATH}")
_, layout_component, layout_created = add_component(
    subsystem,
    blueprint,
    root_handle,
    unreal.ChildActorComponent,
    "MahjongTileLayout",
)
configure_new_component(
    layout_component,
    {
        "child_actor_class": table_class,
    },
)
if layout_created:
    configure_new_component(
        layout_component,
        {
            "relative_location": unreal.Vector(0.0, 0.0, 0.0),
            "relative_rotation": unreal.Rotator(0.0, 0.0, 0.0),
            "relative_scale3d": unreal.Vector(1.0, 1.0, 1.0),
        },
    )

_, camera_component, camera_created = add_component(
    subsystem,
    blueprint,
    root_handle,
    unreal.CineCameraComponent,
    "MahjongRoomCamera",
)
if camera_created:
    configure_new_component(
        camera_component,
        {
            "relative_location": unreal.Vector(0.0, -2350.0, 1392.0),
            "relative_rotation": unreal.Rotator(0.0, -30.0, 90.0),
            "current_focal_length": 30.0,
            "current_aperture": 16.0,
            "constrain_aspect_ratio": False,
        },
    )
    configure_tabletop_post_process(camera_component)

_, directional, directional_created = add_component(
    subsystem,
    blueprint,
    root_handle,
    unreal.DirectionalLightComponent,
    "BP_DirectionalLight",
)
if directional_created:
    configure_new_component(
        directional,
        {
            "relative_rotation": unreal.Rotator(-105.0, -31.0, -14.0),
            "visible": False,
            "intensity": 0.0,
            "light_color": unreal.Color(r=255, g=250, b=242, a=255),
            "cast_shadows": True,
            "mobility": unreal.ComponentMobility.MOVABLE,
        },
    )

_, sky, sky_created = add_component(
    subsystem,
    blueprint,
    root_handle,
    unreal.SkyLightComponent,
    "BP_SkyLight",
)
if sky_created:
    configure_new_component(
        sky,
        {
            "intensity": 0.15,
            "light_color": unreal.Color(r=199, g=219, b=255, a=255),
            "cast_shadows": False,
            "mobility": unreal.ComponentMobility.MOVABLE,
        },
    )

_, key, key_created = add_component(
    subsystem,
    blueprint,
    root_handle,
    unreal.SpotLightComponent,
    "BP_KeyLight",
)
if key_created:
    configure_new_component(
        key,
        {
            "relative_location": unreal.Vector(0.0, 0.0, 1200.0),
            "relative_rotation": unreal.Rotator(0.0, -90.0, 0.0),
            "intensity_units": unreal.LightUnits.LUMENS,
            "intensity": 400.0,
            "attenuation_radius": 3000.0,
            "inner_cone_angle": 40.0,
            "outer_cone_angle": 65.0,
            "light_color": unreal.Color(r=255, g=248, b=238, a=255),
            "cast_shadows": True,
            "mobility": unreal.ComponentMobility.MOVABLE,
        },
    )

_, fill, fill_created = add_component(
    subsystem,
    blueprint,
    root_handle,
    unreal.SpotLightComponent,
    "BP_FillLight",
)
if fill_created:
    configure_new_component(
        fill,
        {
            "relative_location": unreal.Vector(0.0, -1450.0, 520.0),
            "relative_rotation": unreal.Rotator(0.0, -26.6, 90.0),
            "intensity_units": unreal.LightUnits.LUMENS,
            "intensity": 300.0,
            "attenuation_radius": 1800.0,
            "inner_cone_angle": 50.0,
            "outer_cone_angle": 70.0,
            "light_color": unreal.Color(r=230, g=240, b=255, a=255),
            "cast_shadows": False,
            "mobility": unreal.ComponentMobility.MOVABLE,
        },
    )

# Apply the safe migration defaults exactly once. Subsequent script runs preserve
# every value edited by an artist in the Blueprint.
if (
    unreal.EditorAssetLibrary.get_metadata_tag(blueprint, SCHEMA_METADATA_TAG)
    != SCHEMA_VERSION
):
    configure_new_component(
        camera_component,
        {
            "relative_location": unreal.Vector(0.0, -2350.0, 1392.0),
            "relative_rotation": unreal.Rotator(0.0, -30.0, 90.0),
            "current_focal_length": 30.0,
            "current_aperture": 16.0,
            "constrain_aspect_ratio": False,
        },
    )
    configure_tabletop_post_process(camera_component)
    configure_new_component(
        directional,
        {
            "visible": False,
            "intensity": 0.0,
            "light_color": unreal.Color(r=255, g=250, b=242, a=255),
            "cast_shadows": True,
        },
    )
    configure_new_component(
        sky,
        {
            "intensity": 0.15,
            "light_color": unreal.Color(r=199, g=219, b=255, a=255),
            "cast_shadows": False,
        },
    )
    configure_new_component(
        key,
        {
            "intensity": 400.0,
            "light_color": unreal.Color(r=255, g=248, b=238, a=255),
            "cast_shadows": True,
        },
    )
    configure_new_component(
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

# Mobile gameplay needs the whole tabletop and near hand sharp at once.
focus_settings = camera_component.get_editor_property("focus_settings")
focus_settings.set_editor_property(
    "focus_method", unreal.CameraFocusMethod.DISABLE
)
camera_component.set_editor_property("focus_settings", focus_settings)
filmback = camera_component.get_editor_property("filmback")
filmback.set_editor_property("sensor_vertical_offset", -2.0)
camera_component.set_editor_property("filmback", filmback)

unreal.BlueprintEditorLibrary.compile_blueprint(blueprint)
unreal.EditorAssetLibrary.save_loaded_asset(blueprint, only_if_is_dirty=False)
presentation_class = unreal.load_class(None, GENERATED_CLASS_PATH)
if not presentation_class:
    raise RuntimeError(f"Could not load generated class {GENERATED_CLASS_PATH}")

# Client platform configs explicitly cook this directory. A global AlwaysCook
# PrimaryAssetLabel would override the Dedicated Server NeverCook boundary and
# pull the complete presentation dependency graph into the server package.
legacy_label_registered = unreal.EditorAssetLibrary.does_asset_exist(LEGACY_LABEL_PATH)
print(f"MAHJONG_PRESENTATION_LEGACY_LABEL_REGISTERED={legacy_label_registered}")
if legacy_label_registered:
    legacy_label = unreal.EditorAssetLibrary.load_asset(LEGACY_LABEL_PATH)
    legacy_label.set_editor_property("label_assets_in_my_directory", False)
    legacy_label.set_editor_property("is_runtime_label", False)
    legacy_rules = legacy_label.get_editor_property("rules")
    legacy_rules.set_editor_property("cook_rule", unreal.PrimaryAssetCookRule.NEVER_COOK)
    legacy_label.set_editor_property("rules", legacy_rules)
    unreal.EditorAssetLibrary.save_loaded_asset(legacy_label, only_if_is_dirty=False)
    unreal.log(
        f"MAHJONG_PRESENTATION_LEGACY_LABEL_DISABLED={LEGACY_LABEL_PATH}"
    )

world = unreal.EditorLoadingAndSavingUtils.load_map(MAP_PATH)
if not world:
    raise RuntimeError(f"Could not load {MAP_PATH}")

actor_subsystem = unreal.get_editor_subsystem(unreal.EditorActorSubsystem)
actors = actor_subsystem.get_all_level_actors()
removed_labels = []
for actor in actors:
    class_name = actor.get_class().get_name()
    if (
        "MahjongRoomPresentation" in class_name
        or class_name in ("DirectionalLight", "SkyLight")
    ):
        removed_labels.append(actor.get_actor_label())
        actor_subsystem.destroy_actor(actor)

presentation = actor_subsystem.spawn_actor_from_class(
    presentation_class, unreal.Vector(), unreal.Rotator()
)
if not presentation:
    raise RuntimeError("Could not place BP_MahjongRoomPresentation")
presentation.set_actor_label("MahjongRoomPresentation")

if not unreal.EditorLoadingAndSavingUtils.save_map(world, MAP_PATH):
    raise RuntimeError(f"Could not save {MAP_PATH}")

unreal.log(f"MAHJONG_PRESENTATION_REMOVED_PREVIEW_ACTORS={removed_labels}")
unreal.log(f"MAHJONG_PRESENTATION_RUNTIME_CLASS={presentation.get_class().get_path_name()}")
unreal.log(
    "MAHJONG_PRESENTATION_BLUEPRINT_COMPONENTS_OK="
    "PresentationRoot,MahjongTableMesh,MahjongTileLayout,MahjongRoomCamera,"
    "BP_DirectionalLight,BP_SkyLight,BP_KeyLight,BP_FillLight"
)
unreal.log("MAHJONG_PRESENTATION_ASSETS_OK")
