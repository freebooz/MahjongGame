"""Move the artist-editable room camera closer while preserving its 60-degree table-normal angle."""

import unreal


BLUEPRINT_PATH = "/Game/Client/Room/Presentation/BP_MahjongRoomPresentation"


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
if not camera:
    raise RuntimeError("MahjongRoomCamera component was not found")

camera.set_editor_property(
    "relative_location", unreal.Vector(0.0, -1950.0, 1155.0)
)
camera.set_editor_property(
    "relative_rotation", unreal.Rotator(0.0, -30.0, 90.0)
)
camera.set_editor_property("current_focal_length", 30.0)
camera.set_editor_property("constrain_aspect_ratio", False)

unreal.BlueprintEditorLibrary.compile_blueprint(blueprint)
unreal.EditorAssetLibrary.save_loaded_asset(blueprint, only_if_is_dirty=False)
unreal.log(
    "REFERENCE_ROOM_CAMERA_ADJUSTED location=(0,-1950,1155) "
    "table-normal-angle=60 pitch=-30 focal=30"
)
