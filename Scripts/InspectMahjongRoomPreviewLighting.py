# 记录麻将房间视觉预览地图中的相机、曝光、灯光和后处理设置，用于审查差异。
# 脚本只读加载目标地图，不更改关卡；日志不得被当作正式运行配置来源。
"""Log camera and lighting settings stored in the Mahjong room visual preview map."""

import unreal


MAP_PATH = "/Game/Maps/MahjongRoomVisualPreviewMap"


world = unreal.EditorLoadingAndSavingUtils.load_map(MAP_PATH)
if not world:
    raise RuntimeError(f"Could not load {MAP_PATH}")

actors = unreal.EditorLevelLibrary.get_all_level_actors()
unreal.log(f"MAHJONG_PREVIEW_ACTOR_COUNT={len(actors)}")
for actor in actors:
    class_name = actor.get_class().get_name()
    if "Light" not in class_name and "MahjongRoomPresentation" not in class_name:
        continue
    transform = actor.get_actor_transform()
    unreal.log(
        "MAHJONG_PREVIEW_ACTOR "
        f"label={actor.get_actor_label()} class={class_name} "
        f"location={transform.translation} rotation={transform.rotation.rotator()}"
    )
    for component in actor.get_components_by_class(unreal.ActorComponent):
        component_class = component.get_class().get_name()
        if isinstance(component, unreal.SceneComponent):
            try:
                unreal.log(
                    "MAHJONG_PREVIEW_COMPONENT "
                    f"actor={actor.get_actor_label()} component={component.get_name()} "
                    f"class={component_class} "
                    f"location={component.get_editor_property('relative_location')} "
                    f"rotation={component.get_editor_property('relative_rotation')} "
                    f"scale={component.get_editor_property('relative_scale3d')}"
                )
            except Exception:
                pass
        if "Light" not in component_class:
            continue
        values = []
        for property_name in (
            "intensity",
            "intensity_units",
            "light_color",
            "cast_shadows",
            "attenuation_radius",
            "inner_cone_angle",
            "outer_cone_angle",
            "source_width",
            "source_height",
        ):
            try:
                values.append(f"{property_name}={component.get_editor_property(property_name)}")
            except Exception:
                pass
        unreal.log(
            "MAHJONG_PREVIEW_LIGHT "
            f"actor={actor.get_actor_label()} component={component.get_name()} "
            f"class={component_class} {' '.join(values)}"
        )

unreal.log("MAHJONG_PREVIEW_LIGHTING_INSPECTION_OK")
