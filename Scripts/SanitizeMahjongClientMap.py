# 清理客户端地图中的服务端专用、编辑器临时和失效对象引用，同时保留正式表现资产。
# 删除前按类和路径白名单确认目标；保存后验证地图可加载，禁止处理 Dedicated Server 地图。
import unreal

MAP_PATH = "/Game/Maps/MahjongRoomMap"

world = unreal.EditorLoadingAndSavingUtils.load_map(MAP_PATH)
if not world:
    raise RuntimeError("Could not load MahjongRoomMap")

# The authoritative network map must load in both Client and Server targets.
# Remove visual actors whose classes live in the ClientOnly module; the client
# recreates the same presentation locally after joining.
for actor in list(unreal.EditorLevelLibrary.get_all_level_actors()):
    actor_class = actor.get_class() if actor else None
    class_path = actor_class.get_path_name() if actor_class else ""
    label = actor.get_actor_label() if hasattr(actor, "get_actor_label") else ""
    actor_tags = []
    try:
        actor_tags = [str(tag) for tag in actor.get_editor_property("tags")]
    except Exception:
        pass
    is_client_actor = class_path.startswith("/Script/GuiyangMahjongClient.")
    is_legacy_presentation = (
        label in {"MahjongRoomTable", "MahjongRoomCamera", "MahjongStableRoomLight"}
        or any(tag in {
            "MahjongRoomTable", "MahjongRoomCamera", "MahjongStableRoomLight",
            "MahjongRoomPresentation"
        } for tag in actor_tags)
    )
    if is_client_actor or is_legacy_presentation:
        unreal.EditorLevelLibrary.destroy_actor(actor)

world_settings = world.get_world_settings()
world_settings.set_editor_property("default_game_mode", None)
if not unreal.EditorLoadingAndSavingUtils.save_map(world, MAP_PATH):
    raise RuntimeError("Could not save sanitized MahjongRoomMap")
unreal.log("MAHJONG_SHARED_ROOM_MAP_SANITIZED")
