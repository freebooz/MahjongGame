# 只读诊断运行时正面/背面麻将牌网格、材质槽、碰撞和资产引用。
# 不创建瞬态替换或保存修改；缺失关键网格时返回失败供集成门禁处理。
"""Read-only diagnostics for the runtime face-up and face-down tile meshes."""

import unreal


ASSETS = (
    "/Game/Art/Mahjong/Mahjong50/Meshes/SM_Mahjong50",
    "/Game/Art/Mahjong/Mahjong50/Tiles/SM_Mahjong50_Characters_1",
)


for asset_path in ASSETS:
    mesh = unreal.EditorAssetLibrary.load_asset(asset_path)
    if not mesh:
        raise RuntimeError(f"Missing Mahjong50 runtime mesh: {asset_path}")
    bounds = mesh.get_bounds().box_extent * 2.0
    materials = []
    for slot in mesh.get_editor_property("static_materials"):
        material = slot.get_editor_property("material_interface")
        materials.append(material.get_path_name() if material else "None")
    unreal.log(
        "[Mahjong50RuntimeMesh] "
        f"path={asset_path} "
        f"size=({bounds.x:.3f},{bounds.y:.3f},{bounds.z:.3f}) "
        f"materials={materials}"
    )
