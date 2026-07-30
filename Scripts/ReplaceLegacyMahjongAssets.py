# 验证 Mahjong50 运行资产完整后删除退役三维麻将资源，并修复所有目标引用。
# 只有新资产校验全部通过才允许删除；范围限定到旧资产清单，无关资源一律保留。
"""Validate Mahjong50 runtime assets and remove the retired 3D Mahjong set."""

import unreal


OLD_ROOT = "/Game/Art/Mahjong/Tiles"
NEW_TILE_ROOT = "/Game/Art/Mahjong/Mahjong50/Tiles"

TILE_NAMES = [
    *[f"Characters_{rank}" for rank in range(1, 10)],
    *[f"Bamboo_{rank}" for rank in range(1, 10)],
    *[f"Dots_{rank}" for rank in range(1, 10)],
    "East", "South", "West", "North",
    "Red_Dragon", "Green_Dragon", "White_Dragon",
]


def log(message: str) -> None:
    unreal.log(f"[Mahjong50Runtime] {message}")


def validate_new_assets() -> None:
    missing = []
    for tile_name in TILE_NAMES:
        path = f"{NEW_TILE_ROOT}/SM_Mahjong50_{tile_name}"
        mesh = unreal.EditorAssetLibrary.load_asset(path)
        if not mesh:
            missing.append(path)
            continue
        if len(mesh.get_editor_property("static_materials")) != 1:
            raise RuntimeError(f"Expected one unified material slot on {path}")
    if missing:
        raise RuntimeError("Missing Mahjong50 tile meshes: " + ", ".join(missing))
    log("validated 34 Mahjong50 runtime tile meshes")


def delete_legacy_assets() -> None:
    if not unreal.EditorAssetLibrary.does_directory_exist(OLD_ROOT):
        log(f"legacy directory already absent: {OLD_ROOT}")
        return

    old_assets = unreal.EditorAssetLibrary.list_assets(OLD_ROOT, recursive=True, include_folder=False)
    external_referencers = {}
    for asset_path in old_assets:
        references = unreal.EditorAssetLibrary.find_package_referencers_for_asset(
            asset_path, load_assets_to_confirm=True
        )
        outside = [path for path in references if not path.startswith(OLD_ROOT)]
        if outside:
            external_referencers[asset_path] = outside
    if external_referencers:
        raise RuntimeError(f"Legacy assets still have external references: {external_referencers}")

    unreal.EditorAssetLibrary.delete_directory(OLD_ROOT)
    remaining = unreal.EditorAssetLibrary.list_assets(OLD_ROOT, recursive=True, include_folder=False)
    for asset_path in remaining:
        unreal.EditorAssetLibrary.delete_asset(asset_path)
    remaining = unreal.EditorAssetLibrary.list_assets(OLD_ROOT, recursive=True, include_folder=False)
    if remaining:
        raise RuntimeError(f"Legacy assets still exist after deletion: {remaining}")
    log(f"deleted {len(old_assets)} legacy assets from {OLD_ROOT}")


def main() -> None:
    validate_new_assets()
    delete_legacy_assets()
    log("MAHJONG50_RUNTIME_REPLACEMENT_OK")


if __name__ == "__main__":
    main()
