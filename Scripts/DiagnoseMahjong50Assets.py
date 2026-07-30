# 只读诊断 Mahjong50 材质绑定、图集参数、纹理质量和房间光照，输出可定位审查信息。
# 不保存资产或修改编辑器状态；日志包含路径时提交前需确认不存在本机敏感信息。
"""Print Mahjong50 material bindings, atlas parameters, texture quality, and room lighting."""

from __future__ import annotations

import unreal


ROOT = "/Game/Art/Mahjong/Mahjong50"
MAP = "/Game/Maps/MahjongRoomVisualPreviewMap"


def value(obj, name):
    try:
        return obj.get_editor_property(name)
    except Exception as exc:
        return f"<unavailable: {exc}>"


def inspect_mesh(path: str) -> None:
    mesh = unreal.EditorAssetLibrary.load_asset(path)
    if not mesh:
        unreal.log_error(f"[Mahjong50Diag] missing mesh={path}")
        return
    slots = []
    for index, slot in enumerate(value(mesh, "static_materials")):
        interface = value(slot, "material_interface")
        slots.append(
            {
                "index": index,
                "slot": str(value(slot, "material_slot_name")),
                "imported": str(value(slot, "imported_material_slot_name")),
                "material": interface.get_path_name() if interface else None,
            }
        )
    unreal.log(f"[Mahjong50Diag] mesh={path} slots={slots}")


def inspect_instance(name: str) -> None:
    path = f"{ROOT}/MaterialInstances/MI_Mahjong50_{name}"
    instance = unreal.EditorAssetLibrary.load_asset(path)
    if not instance:
        unreal.log_error(f"[Mahjong50Diag] missing instance={path}")
        return
    column = unreal.MaterialEditingLibrary.get_material_instance_scalar_parameter_value(
        instance, "Column"
    )
    row = unreal.MaterialEditingLibrary.get_material_instance_scalar_parameter_value(
        instance, "RowFromBottom"
    )
    parent = value(instance, "parent")
    unreal.log(
        f"[Mahjong50Diag] instance={path} column={column} row={row} "
        f"parent={parent.get_path_name() if parent else None}"
    )


def inspect_texture(name: str) -> None:
    path = f"{ROOT}/Textures/{name}"
    texture = unreal.EditorAssetLibrary.load_asset(path)
    if not texture:
        unreal.log_error(f"[Mahjong50Diag] missing texture={path}")
        return
    props = {
        key: str(value(texture, key))
        for key in (
            "blueprint_get_size_x",
            "blueprint_get_size_y",
            "lod_group",
            "lod_bias",
            "mip_gen_settings",
            "filter",
            "never_stream",
            "srgb",
            "compression_settings",
        )
    }
    try:
        props["size"] = f"{texture.blueprint_get_size_x()}x{texture.blueprint_get_size_y()}"
    except Exception:
        pass
    unreal.log(f"[Mahjong50Diag] texture={path} props={props}")


def inspect_room() -> None:
    if not unreal.EditorAssetLibrary.does_asset_exist(MAP):
        unreal.log_error(f"[Mahjong50Diag] missing map={MAP}")
        return
    unreal.EditorLevelLibrary.load_level(MAP)
    for actor in unreal.EditorLevelLibrary.get_all_level_actors():
        class_name = actor.get_class().get_name()
        if "Light" not in class_name and "Camera" not in class_name and "PostProcess" not in class_name:
            continue
        details = {
            "label": actor.get_actor_label(),
            "class": class_name,
            "location": str(actor.get_actor_location()),
        }
        light = actor.get_component_by_class(unreal.LightComponent)
        if light:
            details["intensity"] = str(value(light, "intensity"))
            details["visible"] = str(value(light, "visible"))
        camera = actor.get_component_by_class(unreal.CineCameraComponent)
        if camera:
            details["focal_length"] = str(value(camera, "current_focal_length"))
            details["post_process_blend_weight"] = str(value(camera, "post_process_blend_weight"))
        unreal.log(f"[Mahjong50Diag] room_actor={details}")


for mesh_path in (
    f"{ROOT}/Meshes/SM_Mahjong50",
    f"{ROOT}/Tiles/SM_Mahjong50_Characters_1",
    f"{ROOT}/Tiles/SM_Mahjong50_Bamboo_7",
    f"{ROOT}/Tiles/SM_Mahjong50_Dots_9",
):
    inspect_mesh(mesh_path)

for instance_name in ("North", "Characters_1", "Characters_9", "Bamboo_7", "Dots_9"):
    inspect_instance(instance_name)

for texture_name in (
    "T_Mahjong50_FaceAtlas_BaseColor",
    "T_Mahjong50_FaceAtlas_Normal",
    "T_Mahjong50_FaceAtlas_ORM",
):
    inspect_texture(texture_name)

inspect_room()
unreal.log("[Mahjong50Diag] complete")
