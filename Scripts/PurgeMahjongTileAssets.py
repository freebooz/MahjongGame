# 在干净导入前删除旧麻将牌资产根目录及明确废弃依赖，防止覆盖导入继承旧设置。
# 删除目标必须位于项目 Content 白名单；无法确认归属的引用保留并报告人工审查。
"""Delete only the old Mahjong tile asset roots before a clean import."""

from __future__ import annotations

import unreal


TARGET_ROOTS = (
    "/Game/Art/Mahjong/Tiles",
    "/Game/Art/Mahjong/Mahjong50",
)


def log(message: str) -> None:
    unreal.log(f"[MahjongTilePurge] {message}")


def purge_root(root: str) -> int:
    if not unreal.EditorAssetLibrary.does_directory_exist(root):
        log(f"target directory already absent: {root}")
        return 0

    assets = unreal.EditorAssetLibrary.list_assets(
        root,
        recursive=True,
        include_folder=False,
    )
    log(f"deleting {len(assets)} assets under {root}")

    failed = []
    for asset_path in sorted(assets, reverse=True):
        if not unreal.EditorAssetLibrary.delete_asset(asset_path):
            failed.append(asset_path)
    if failed:
        raise RuntimeError(
            f"Failed to delete assets under {root}: " + ", ".join(failed)
        )

    unreal.EditorAssetLibrary.delete_directory(root)
    remaining = unreal.EditorAssetLibrary.list_assets(
        root,
        recursive=True,
        include_folder=False,
    )
    if remaining:
        raise RuntimeError(
            f"Assets remain under {root}: " + ", ".join(remaining)
        )
    return len(assets)


def main() -> None:
    deleted = sum(purge_root(root) for root in TARGET_ROOTS)
    unreal.EditorLoadingAndSavingUtils.save_dirty_packages(
        save_map_packages=False,
        save_content_packages=True,
    )
    log(f"PURGE_ALL_TILE_ASSETS_OK deleted={deleted}")


if __name__ == "__main__":
    main()
