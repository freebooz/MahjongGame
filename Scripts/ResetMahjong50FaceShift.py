# 将 Mahjong50 牌面恢复到创作时居中的 UV 位置，清理错误的材质偏移覆盖。
# 只更新目标牌面材质实例；修改后验证全部牌型映射，不改变底层图集内容。
"""Restore the Mahjong50 face artwork to its authored, centred UV position."""

from __future__ import annotations

import sys
from pathlib import Path

import unreal

sys.path.insert(0, str(Path(__file__).resolve().parent))

from ImportMahjong50Assets import (
    INSTANCE_DEST,
    MATERIAL_DEST,
    UNIFIED_MATERIAL_PATH,
    build_face_material,
)


SHIFTED_MATERIAL_PATH = (
    f"{MATERIAL_DEST}/M_Mahjong50_TileUnified_FaceShift5mm"
)


def main() -> None:
    if unreal.EditorAssetLibrary.does_asset_exist(UNIFIED_MATERIAL_PATH):
        if not unreal.EditorAssetLibrary.delete_asset(UNIFIED_MATERIAL_PATH):
            raise RuntimeError(
                f"Could not replace face material: {UNIFIED_MATERIAL_PATH}"
            )

    centred_material = build_face_material()
    instance_paths = unreal.EditorAssetLibrary.list_assets(
        INSTANCE_DEST, recursive=False, include_folder=False
    )
    updated = 0
    for instance_path in instance_paths:
        instance = unreal.EditorAssetLibrary.load_asset(instance_path)
        if not isinstance(instance, unreal.MaterialInstanceConstant):
            continue
        unreal.MaterialEditingLibrary.set_material_instance_parent(
            instance, centred_material
        )
        unreal.MaterialEditingLibrary.set_material_instance_scalar_parameter_value(
            instance, "FaceShiftMillimeters", 0.0
        )
        unreal.EditorAssetLibrary.save_loaded_asset(
            instance, only_if_is_dirty=False
        )
        updated += 1

    unreal.EditorAssetLibrary.save_loaded_asset(
        centred_material, only_if_is_dirty=False
    )
    if updated < 27:
        raise RuntimeError(
            f"Expected at least 27 Mahjong face instances, updated {updated}"
        )

    if unreal.EditorAssetLibrary.does_asset_exist(SHIFTED_MATERIAL_PATH):
        if not unreal.EditorAssetLibrary.delete_asset(SHIFTED_MATERIAL_PATH):
            raise RuntimeError(
                f"Could not delete obsolete shifted material: "
                f"{SHIFTED_MATERIAL_PATH}"
            )

    unreal.log(
        "[Mahjong50FaceShift] FACE_SHIFT_RESET_OK "
        f"material={UNIFIED_MATERIAL_PATH} instances={updated}"
    )


if __name__ == "__main__":
    main()
