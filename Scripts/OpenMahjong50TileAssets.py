"""Open the imported Mahjong50 verification meshes in Unreal Editor."""

from __future__ import annotations

import time

import unreal


ASSET_PATHS = (
    "/Game/Art/Mahjong/Mahjong50/Tiles/"
    "SM_Mahjong50_Characters_5.SM_Mahjong50_Characters_5",
    "/Game/Art/Mahjong/Mahjong50/Tiles/"
    "SM_Mahjong50_Red_Dragon.SM_Mahjong50_Red_Dragon",
)

_started_at = time.monotonic()
_callback_handle = None


def _open_assets_after_editor_ready(delta_seconds):
    del delta_seconds
    global _callback_handle

    if time.monotonic() - _started_at < 4.0:
        return

    assets = []
    for asset_path in ASSET_PATHS:
        asset = unreal.load_asset(asset_path)
        if asset is None or not isinstance(asset, unreal.StaticMesh):
            unreal.log_error(
                f"[Mahjong50Open] Missing StaticMesh asset: {asset_path}"
            )
            continue
        assets.append(asset)

    if len(assets) != len(ASSET_PATHS):
        unreal.log_error(
            "[Mahjong50Open] Could not load both verification tile meshes"
        )
    else:
        asset_editor = unreal.get_editor_subsystem(unreal.AssetEditorSubsystem)
        opened = asset_editor.open_editor_for_assets(assets)
        unreal.log(
            "[Mahjong50Open] MAHJONG50_TILE_EDITORS_OPENED "
            f"count={len(assets)} result={opened}"
        )

        try:
            unreal.EditorAssetLibrary.sync_browser_to_objects(
                list(ASSET_PATHS)
            )
        except Exception as exc:
            unreal.log_warning(
                f"[Mahjong50Open] Content Browser sync skipped: {exc}"
            )

    unreal.unregister_slate_post_tick_callback(_callback_handle)
    _callback_handle = None


_callback_handle = unreal.register_slate_post_tick_callback(
    _open_assets_after_editor_ready
)
