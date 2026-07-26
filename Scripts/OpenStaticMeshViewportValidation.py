"""打开一个代表性麻将牌静态网格体，用于验证资产编辑器三维视口布局。"""

import time
import unreal


ASSET_PATH = (
    "/Game/Art/Mahjong/Mahjong50/Tiles/"
    "SM_Mahjong50_Characters_1.SM_Mahjong50_Characters_1"
)

_started_at = time.monotonic()
_callback_handle = None


def _open_asset_after_editor_ready(delta_seconds):
    del delta_seconds
    global _callback_handle

    if time.monotonic() - _started_at < 3.0:
        return

    asset = unreal.load_asset(ASSET_PATH)
    if asset is None:
        unreal.log_error(f"STATIC_MESH_VIEWPORT_VALIDATE_LOAD_FAILED path={ASSET_PATH}")
    else:
        subsystem = unreal.get_editor_subsystem(unreal.AssetEditorSubsystem)
        opened = subsystem.open_editor_for_assets([asset])
        unreal.log(
            "STATIC_MESH_VIEWPORT_VALIDATE_OPENED "
            f"path={ASSET_PATH} result={opened}"
        )

    unreal.unregister_slate_post_tick_callback(_callback_handle)
    _callback_handle = None


_callback_handle = unreal.register_slate_post_tick_callback(
    _open_asset_after_editor_ready
)
