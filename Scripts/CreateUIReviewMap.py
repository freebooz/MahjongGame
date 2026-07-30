# 创建 UI 人工审查地图，统一加载目标 Widget、字体和设备尺寸场景，不参与正式玩法。
# 输出只用于编辑器/测试；生成失败时不得加入默认地图或发布 Cook 清单。
import unreal

asset_path = "/Game/Maps/UIReviewMap"
if not unreal.EditorAssetLibrary.does_asset_exist(asset_path):
    unreal.EditorLevelLibrary.new_level(asset_path)
else:
    unreal.EditorLevelLibrary.load_level(asset_path)
unreal.EditorLevelLibrary.save_current_level()
unreal.log("[GuiyangUIReview] review map ready: /Game/Maps/UIReviewMap")
