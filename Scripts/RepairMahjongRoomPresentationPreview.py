"""Repair required Blueprint preview references without replacing level instances."""

import unreal


BLUEPRINT_PATH = "/Game/Client/Room/Presentation/BP_MahjongRoomPresentation"
TABLE_MESH_PATH = "/Game/Art/Mahjong/Table/Meshes/SM_StandardMahjongTable"
TABLE_CLASS_PATH = "/Script/GuiyangMahjongClient.Mahjong3DTableActor"


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


blueprint = unreal.EditorAssetLibrary.load_asset(BLUEPRINT_PATH)
table_mesh = unreal.EditorAssetLibrary.load_asset(TABLE_MESH_PATH)
table_class = unreal.load_class(None, TABLE_CLASS_PATH)
if not blueprint or not table_mesh or not table_class:
    raise RuntimeError(
        "Could not load required preview assets: "
        f"blueprint={blueprint} mesh={table_mesh} class={table_class}"
    )

subsystem = unreal.get_engine_subsystem(unreal.SubobjectDataSubsystem)
mesh_component = find_component_template(
    subsystem, blueprint, "MahjongTableMesh"
)
layout_component = find_component_template(
    subsystem, blueprint, "MahjongTileLayout"
)
if not mesh_component or not layout_component:
    raise RuntimeError(
        "BP_MahjongRoomPresentation is missing required preview components"
    )

mesh_component.set_editor_property("static_mesh", table_mesh)
layout_component.set_editor_property("child_actor_class", table_class)

unreal.BlueprintEditorLibrary.compile_blueprint(blueprint)
unreal.EditorAssetLibrary.save_loaded_asset(blueprint, only_if_is_dirty=False)
unreal.log(
    "MAHJONG_PRESENTATION_PREVIEW_REPAIRED "
    f"mesh={mesh_component.get_editor_property('static_mesh').get_path_name()} "
    f"child_class={layout_component.get_editor_property('child_actor_class').get_path_name()}"
)
