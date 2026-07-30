# 创建 Dedicated Server 使用的最小房间地图，只保留权威玩法和网络所需对象。
# 生成目标必须与客户端表现地图隔离；更新前精确删除旧目标地图，不携带 UI 或高成本美术资源。
import unreal

MAP_PATH = "/Game/Maps/MahjongServerMap"

if unreal.EditorAssetLibrary.does_asset_exist(MAP_PATH):
    world = unreal.EditorLoadingAndSavingUtils.load_map(MAP_PATH)
else:
    world = unreal.EditorLoadingAndSavingUtils.new_blank_map(False)
if not world:
    raise RuntimeError("Could not create or load MahjongServerMap")
world_settings = world.get_world_settings()
server_mode = unreal.load_class(None, "/Script/GuiyangMahjongServer.GuiyangMahjongGameMode")
if not server_mode:
    raise RuntimeError("GuiyangMahjongServer game mode class is unavailable")
world_settings.set_editor_property("default_game_mode", server_mode)
if not unreal.EditorLoadingAndSavingUtils.save_map(world, MAP_PATH):
    raise RuntimeError("Could not save MahjongServerMap")
unreal.log("MAHJONG_SERVER_MAP_OK")
