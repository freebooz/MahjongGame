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
key = find_component_template(subsystem, blueprint, "BP_KeyLight")
fill = find_component_template(subsystem, blueprint, "BP_FillLight")
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
    directional.set_editor_property("intensity", 10.0)
    directional.set_editor_property("cast_shadows", False)
if key:
    key.set_editor_property("relative_location", unreal.Vector(0.0, 0.0, 120.0))
    key.set_editor_property("attenuation_radius", 300.0)
    key.set_editor_property("cast_shadows", False)
if fill:
    fill.set_editor_property(
        "relative_location", unreal.Vector(0.0, -145.0, 52.0)
    )
    fill.set_editor_property("attenuation_radius", 180.0)
    fill.set_editor_property("cast_shadows", False)

camera.set_editor_property(
    "relative_location", unreal.Vector(0.0, -202.0, 120.0)
)
camera.set_editor_property(
    "relative_rotation", unreal.Rotator(0.0, -30.0, 90.0)
)
camera.set_editor_property("current_focal_length", 30.0)
camera.set_editor_property("constrain_aspect_ratio", False)

unreal.BlueprintEditorLibrary.compile_blueprint(blueprint)
unreal.EditorAssetLibrary.save_loaded_asset(blueprint, only_if_is_dirty=False)
unreal.log(
    f"REFERENCE_ROOM_CAMERA_ADJUSTED table=300x300cm scale={TABLE_RUNTIME_SCALE:.6f} "
    "location=(0,-202,120) table-normal-angle=60 pitch=-30 focal=30"
)
