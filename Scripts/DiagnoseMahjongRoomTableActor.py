# 检查房间内麻将桌 Actor、已创作组件和瞬态牌网格绑定，定位运行时表现差异。
# 脚本只读遍历当前地图；找不到目标时明确失败，不自动创建或替换 Actor。
"""Inspect authored Mahjong table actors and their transient tile mesh bindings."""

from __future__ import annotations

import unreal


MAP_PATH = "/Game/Maps/MahjongRoomVisualPreviewMap"


def path_name(value):
    return value.get_path_name() if value else None


unreal.EditorLevelLibrary.load_level(MAP_PATH)
actors = unreal.EditorLevelLibrary.get_all_level_actors()
tables = [actor for actor in actors if "Mahjong3DTableActor" in actor.get_class().get_name()]
unreal.log(f"[RoomTableDiag] table_count={len(tables)}")

for actor in tables:
    details = {
        "name": actor.get_name(),
        "label": actor.get_actor_label(),
        "class": actor.get_class().get_path_name(),
    }
    for property_name in ("default_tile_mesh", "tile_meshes", "table_mesh"):
        try:
            value = actor.get_editor_property(property_name)
            if isinstance(value, list):
                details[property_name] = [path_name(item) for item in value]
            else:
                details[property_name] = path_name(value)
        except Exception as exc:
            details[property_name] = f"<unavailable: {exc}>"
    unreal.log(f"[RoomTableDiag] actor={details}")

    components = actor.get_components_by_class(unreal.StaticMeshComponent)
    for index, component in enumerate(components):
        mesh = component.get_editor_property("static_mesh")
        materials = [path_name(component.get_material(slot)) for slot in range(component.get_num_materials())]
        unreal.log(
            f"[RoomTableDiag] component={index} name={component.get_name()} "
            f"mesh={path_name(mesh)} materials={materials}"
        )

unreal.log("[RoomTableDiag] complete")
