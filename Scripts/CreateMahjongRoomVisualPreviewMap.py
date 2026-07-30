# 创建仅供编辑器审查的房间预览地图，复用运行时客户端表现 Actor 保持视觉一致。
# 地图不得进入服务器 Cook 列表；缺失依赖时停止生成，不创建指向临时资产的引用。
"""Create an editor-only room preview using the same client presentation actor as runtime."""

import unreal


MAP_PATH = "/Game/Maps/MahjongRoomVisualPreviewMap"
PRESENTATION_CLASS = (
    "/Game/Client/Room/Presentation/BP_MahjongRoomPresentation."
    "BP_MahjongRoomPresentation_C"
)


world = unreal.EditorLoadingAndSavingUtils.new_blank_map(False)
if not world:
    raise RuntimeError("Could not create MahjongRoomVisualPreviewMap")

presentation_class = unreal.load_class(None, PRESENTATION_CLASS)
if not presentation_class:
    raise RuntimeError("MahjongRoomPresentationActor class is unavailable")

presentation = unreal.EditorLevelLibrary.spawn_actor_from_class(
    presentation_class, unreal.Vector(0.0, 0.0, 0.0), unreal.Rotator()
)
if not presentation:
    raise RuntimeError("Could not place MahjongRoomPresentationActor")
presentation.set_actor_label("MahjongRoomPresentation")

if not unreal.EditorLoadingAndSavingUtils.save_map(world, MAP_PATH):
    raise RuntimeError("Could not save MahjongRoomVisualPreviewMap")

unreal.log("MAHJONG_ROOM_VISUAL_PREVIEW_OK")
