# 只读检查麻将房间表现 Blueprint 的组件层级、默认资源和运行时绑定入口。
# 不编译或保存蓝图；发现失效引用时输出对象路径，由专用修复脚本处理。
import unreal


ASSET_PATH = (
    "/Game/Client/Room/Presentation/"
    "BP_MahjongRoomPresentation.BP_MahjongRoomPresentation"
)

blueprint = unreal.EditorAssetLibrary.load_asset(ASSET_PATH)
if blueprint is None:
    raise RuntimeError(f"Could not load {ASSET_PATH}")

unreal.log(f"MAHJONG_BP_CLASS={blueprint.get_class().get_name()}")
unreal.log(
    "MAHJONG_BP_GRAPHS="
    + ",".join(
        str(name)
        for name in unreal.BlueprintEditorLibrary.list_graph_names(blueprint)
    )
)

for property_name in ("ubergraph_pages", "function_graphs", "macro_graphs"):
    try:
        graphs = blueprint.get_editor_property(property_name)
    except Exception as error:
        unreal.log_warning(
            f"MAHJONG_BP_PROPERTY_UNAVAILABLE={property_name}:{error}"
        )
        continue

    for graph in graphs:
        schema_name = "None"
        try:
            schema = graph.get_editor_property("schema")
            if schema is not None:
                schema_name = schema.get_name()
        except Exception as error:
            schema_name = f"ERROR:{error}"
        unreal.log(
            "MAHJONG_BP_GRAPH "
            f"collection={property_name} "
            f"name={graph.get_name()} "
            f"class={graph.get_class().get_name()} "
            f"schema={schema_name}"
        )
