# 只读检查麻将房间当前视觉设置及其来源资产，定位地图默认值与 Blueprint 覆盖差异。
# 不保存任何对象；缺失目标地图或 Actor 时明确失败，不自动创建替代项。
import unreal


ASSET_PATH = (
    "/Game/Client/Room/Presentation/"
    "BP_MahjongRoomPresentation"
)


def component_templates(blueprint):
    subsystem = unreal.get_engine_subsystem(unreal.SubobjectDataSubsystem)
    for handle in subsystem.k2_gather_subobject_data_for_blueprint(blueprint):
        data = unreal.SubobjectDataBlueprintFunctionLibrary.get_data(handle)
        name = str(
            unreal.SubobjectDataBlueprintFunctionLibrary.get_variable_name(data)
        )
        component = (
            unreal.SubobjectDataBlueprintFunctionLibrary.get_object_for_blueprint(
                data, blueprint
            )
        )
        if component:
            yield name, component


blueprint = unreal.EditorAssetLibrary.load_asset(ASSET_PATH)
if not blueprint:
    raise RuntimeError(f"Could not load {ASSET_PATH}")

components = list(component_templates(blueprint))
if not components:
    generated_class = unreal.load_class(
        None,
        (
            "/Game/Client/Room/Presentation/BP_MahjongRoomPresentation."
            "BP_MahjongRoomPresentation_C"
        ),
    )
    default_actor = unreal.get_default_object(generated_class)
    components = [
        (component.get_name(), component)
        for component in default_actor.get_components_by_class(
            unreal.ActorComponent
        )
    ]

for name, component in components:
    unreal.log_warning(
        "MAHJONG_VISUAL_COMPONENT "
        f"name={name} class={component.get_class().get_name()}"
    )
    if isinstance(component, unreal.StaticMeshComponent):
        mesh = component.get_editor_property("static_mesh")
        unreal.log_warning(
            "MAHJONG_VISUAL_STATIC_MESH "
            f"name={name} "
            f"mesh={mesh.get_path_name() if mesh else 'None'} "
            f"location={component.get_editor_property('relative_location')} "
            f"rotation={component.get_editor_property('relative_rotation')} "
            f"scale={component.get_editor_property('relative_scale3d')} "
            f"visible={component.get_editor_property('visible')} "
            f"hidden_in_game={component.get_editor_property('hidden_in_game')}"
        )
    if name == "MahjongRoomCamera":
        settings = component.get_editor_property("post_process_settings")
        focus = component.get_editor_property("focus_settings")
        filmback = component.get_editor_property("filmback")
        unreal.log_warning(
            "MAHJONG_VISUAL_CAMERA "
            f"location={component.get_editor_property('relative_location')} "
            f"rotation={component.get_editor_property('relative_rotation')} "
            f"focal={component.get_editor_property('current_focal_length')} "
            f"aperture={component.get_editor_property('current_aperture')} "
            f"focus_method={focus.get_editor_property('focus_method')} "
            f"sensor_vertical_offset={filmback.get_editor_property('sensor_vertical_offset')} "
            f"blend={component.get_editor_property('post_process_blend_weight')} "
            f"override_method={settings.get_editor_property('override_auto_exposure_method')} "
            f"method={settings.get_editor_property('auto_exposure_method')} "
            f"override_bias={settings.get_editor_property('override_auto_exposure_bias')} "
            f"bias={settings.get_editor_property('auto_exposure_bias')} "
            f"override_physical={settings.get_editor_property('override_auto_exposure_apply_physical_camera_exposure')} "
            f"physical={settings.get_editor_property('auto_exposure_apply_physical_camera_exposure')} "
            f"override_bloom={settings.get_editor_property('override_bloom_intensity')} "
            f"bloom={settings.get_editor_property('bloom_intensity')}"
        )
    elif name in (
        "BP_DirectionalLight",
        "BP_SkyLight",
        "BP_KeyLight",
        "BP_FillLight",
        "BP_TopSoftLight",
        "BP_RimLight",
    ):
        unreal.log_warning(
            "MAHJONG_VISUAL_LIGHT "
            f"name={name} "
            f"class={component.get_class().get_name()} "
            f"intensity={component.get_editor_property('intensity')} "
            f"color={component.get_editor_property('light_color')}"
        )
    elif name == "MahjongTableMesh":
        mesh = component.get_editor_property("static_mesh")
        unreal.log_warning(
            "MAHJONG_VISUAL_TABLE "
            f"mesh={mesh.get_path_name() if mesh else 'None'} "
            f"scale={component.get_editor_property('relative_scale3d')}"
        )
    elif name == "MahjongTileLayout":
        child_class = component.get_editor_property("child_actor_class")
        unreal.log_warning(
            "MAHJONG_VISUAL_LAYOUT "
            f"class={child_class.get_path_name() if child_class else 'None'}"
        )
