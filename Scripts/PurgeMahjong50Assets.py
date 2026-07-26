"""Delete only the generated Mahjong50 UE asset set in a dedicated process."""

from __future__ import annotations

import unreal


DEST_ROOT = "/Game/Art/Mahjong/Mahjong50"


def log(message: str) -> None:
    unreal.log(f"[Mahjong50Purge] {message}")


def main() -> None:
    if not unreal.EditorAssetLibrary.does_directory_exist(DEST_ROOT):
        log("target directory does not exist; nothing to delete")
        return

    assets = unreal.EditorAssetLibrary.list_assets(
        DEST_ROOT, recursive=True, include_folder=False
    )
    log(f"deleting {len(assets)} assets under {DEST_ROOT}")
    failed = []
    for asset_path in sorted(assets, reverse=True):
        if not unreal.EditorAssetLibrary.delete_asset(asset_path):
            failed.append(asset_path)
    if failed:
        raise RuntimeError("Failed to delete target assets: " + ", ".join(failed))

    unreal.EditorAssetLibrary.delete_directory(DEST_ROOT)
    remaining = unreal.EditorAssetLibrary.list_assets(
        DEST_ROOT, recursive=True, include_folder=False
    )
    if remaining:
        raise RuntimeError("Assets remain after purge: " + ", ".join(remaining))

    unreal.EditorLoadingAndSavingUtils.save_dirty_packages(
        save_map_packages=False,
        save_content_packages=True,
    )
    log(f"PURGE_OK deleted={len(assets)}")


if __name__ == "__main__":
    main()
