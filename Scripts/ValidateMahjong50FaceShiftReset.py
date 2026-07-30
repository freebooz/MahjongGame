# 验证所有 Mahjong50 牌面实例均使用居中材质且不存在遗留 UV 偏移参数。
# 只读检查完整牌型集合；缺失实例或参数不一致时返回非零结果。
"""Validate that every Mahjong50 face instance uses the centred material."""

from __future__ import annotations

import unreal


INSTANCE_DEST = "/Game/Art/Mahjong/Mahjong50/MaterialInstances"
UNIFIED_MATERIAL_PATH = (
    "/Game/Art/Mahjong/Mahjong50/Materials/M_Mahjong50_TileUnified"
)
SHIFTED_MATERIAL_PATH = (
    "/Game/Art/Mahjong/Mahjong50/Materials/"
    "M_Mahjong50_TileUnified_FaceShift5mm"
)


def main() -> None:
    expected_parent = unreal.EditorAssetLibrary.load_asset(
        UNIFIED_MATERIAL_PATH
    )
    if not expected_parent:
        raise RuntimeError(f"Missing centred material: {UNIFIED_MATERIAL_PATH}")

    instance_paths = unreal.EditorAssetLibrary.list_assets(
        INSTANCE_DEST, recursive=False, include_folder=False
    )
    checked = 0
    invalid: list[str] = []
    for instance_path in instance_paths:
        instance = unreal.EditorAssetLibrary.load_asset(instance_path)
        if not isinstance(instance, unreal.MaterialInstanceConstant):
            continue
        checked += 1
        parent = instance.get_editor_property("parent")
        shift = (
            unreal.MaterialEditingLibrary
            .get_material_instance_scalar_parameter_value(
                instance, "FaceShiftMillimeters"
            )
        )
        if parent != expected_parent or abs(float(shift)) > 0.0001:
            invalid.append(
                f"{instance_path}: parent={parent.get_path_name() if parent else 'None'} "
                f"shift={shift}"
            )

    if checked < 27 or invalid:
        raise RuntimeError(
            f"Face shift reset validation failed: checked={checked} "
            f"invalid={invalid}"
        )

    referencers = unreal.EditorAssetLibrary.find_package_referencers_for_asset(
        SHIFTED_MATERIAL_PATH, load_assets_to_confirm=True
    )
    if referencers:
        raise RuntimeError(
            f"Obsolete shifted material is still referenced: {referencers}"
        )

    unreal.log(
        "[Mahjong50FaceShift] FACE_SHIFT_RESET_VALIDATED "
        f"instances={checked} shifted_referencers=0"
    )


if __name__ == "__main__":
    main()
