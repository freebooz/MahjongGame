# 为麻将房间表现资产添加低成本深色毛毡背景平面，改善移动端构图且不改变玩法碰撞。
# 仅修改明确的表现 Blueprint；保存前验证材质引用，失败时不得触碰运行房间地图。
"""Add a single low-cost dark felt backdrop plane to the artist-editable room presentation."""

import unreal


BLUEPRINT_PATH = "/Game/Client/Room/Presentation/BP_MahjongRoomPresentation"
PLANE_PATH = "/Engine/BasicShapes/Plane.Plane"
FELT_MATERIAL_PATH = (
    "/Game/Art/Mahjong/Table/Materials/"
    "M_Table_Felt_Green_Fiber_PBR.M_Table_Felt_Green_Fiber_PBR"
)


def get_subobject(subsystem, blueprint, handle):
    data = unreal.SubobjectDataBlueprintFunctionLibrary.get_data(handle)
    return unreal.SubobjectDataBlueprintFunctionLibrary.get_object_for_blueprint(
        data, blueprint
    )


def find_handle(subsystem, blueprint, variable_name):
    for handle in subsystem.k2_gather_subobject_data_for_blueprint(blueprint):
        data = unreal.SubobjectDataBlueprintFunctionLibrary.get_data(handle)
        if str(unreal.SubobjectDataBlueprintFunctionLibrary.get_variable_name(data)) == variable_name:
            return handle
    return None


def find_actor_handle(subsystem, blueprint):
    for handle in subsystem.k2_gather_subobject_data_for_blueprint(blueprint):
        data = unreal.SubobjectDataBlueprintFunctionLibrary.get_data(handle)
        if unreal.SubobjectDataBlueprintFunctionLibrary.is_root_actor(data):
            return handle
    raise RuntimeError("Could not find Blueprint actor root handle")


blueprint = unreal.EditorAssetLibrary.load_asset(BLUEPRINT_PATH)
plane_mesh = unreal.EditorAssetLibrary.load_asset(PLANE_PATH)
felt_material = unreal.EditorAssetLibrary.load_asset(FELT_MATERIAL_PATH)
if not blueprint or not plane_mesh or not felt_material:
    raise RuntimeError("Backdrop dependencies could not be loaded")

subsystem = unreal.get_engine_subsystem(unreal.SubobjectDataSubsystem)
parent_handle = find_handle(subsystem, blueprint, "PresentationRoot")
if not parent_handle:
    parent_handle = find_actor_handle(subsystem, blueprint)

handle = find_handle(subsystem, blueprint, "RoomBackdropPlane")
if handle:
    component = get_subobject(subsystem, blueprint, handle)
else:
    params = unreal.AddNewSubobjectParams()
    params.set_editor_property("parent_handle", parent_handle)
    params.set_editor_property("new_class", unreal.StaticMeshComponent)
    params.set_editor_property("blueprint_context", blueprint)
    handle, failure_reason = subsystem.add_new_subobject(params=params)
    if not unreal.SubobjectDataBlueprintFunctionLibrary.is_handle_valid(handle):
        raise RuntimeError(f"Could not add RoomBackdropPlane: {failure_reason}")
    if not subsystem.rename_subobject(handle, unreal.Text("RoomBackdropPlane")):
        raise RuntimeError("Could not name RoomBackdropPlane")
    component = get_subobject(subsystem, blueprint, handle)

component.set_editor_property("static_mesh", plane_mesh)
component.set_editor_property("relative_location", unreal.Vector(0.0, 0.0, -120.0))
component.set_editor_property("relative_scale3d", unreal.Vector(50.0, 50.0, 1.0))
component.set_editor_property("cast_shadow", False)
component.set_editor_property("mobility", unreal.ComponentMobility.MOVABLE)
component.set_material(0, felt_material)

# The reference uses a clean presentation backdrop. Keep tile-to-table shadows,
# but prevent the whole table mesh from projecting a large hard silhouette onto
# the inexpensive backdrop plane.
table_handle = find_handle(subsystem, blueprint, "MahjongTableMesh")
if table_handle:
    table_component = get_subobject(subsystem, blueprint, table_handle)
    table_component.set_editor_property("cast_shadow", False)

unreal.BlueprintEditorLibrary.compile_blueprint(blueprint)
unreal.EditorAssetLibrary.save_loaded_asset(blueprint, only_if_is_dirty=False)
unreal.log(
    "MAHJONG_ROOM_BACKDROP_READY component=RoomBackdropPlane "
    "mesh=Plane scale=50 material=M_Table_Felt_Green_Fiber_PBR"
)
