"""Import the two supplied transparent player-avatar placeholders as UI textures."""

import tempfile
from pathlib import Path

import unreal


DESTINATION = "/Game/UI/Textures/Avatars"
# 剪贴板附件由宿主写入当前系统临时目录，不能绑定某个 Windows 用户的短路径。
TEMP_ROOT = Path(tempfile.gettempdir())
SOURCES = {
    "T_PlayerAvatar_Placeholder_A": (
        TEMP_ROOT / "codex-clipboard-6e87b485-565f-424f-81d2-643973f6d5df.png"
    ),
    "T_PlayerAvatar_Placeholder_B": (
        TEMP_ROOT / "codex-clipboard-fa3a67a7-d3b0-4043-a185-28dd5d6f6732.png"
    ),
}


for asset_name, source in SOURCES.items():
    if not source.is_file():
        raise RuntimeError(f"Avatar source is missing: {source}")
    task = unreal.AssetImportTask()
    task.filename = str(source)
    task.destination_path = DESTINATION
    task.destination_name = asset_name
    task.automated = True
    task.replace_existing = True
    task.save = True
    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])

    texture = unreal.EditorAssetLibrary.load_asset(
        f"{DESTINATION}/{asset_name}"
    )
    if not texture:
        raise RuntimeError(f"Avatar import failed: {asset_name}")
    texture.set_editor_property("srgb", True)
    texture.set_editor_property(
        "lod_group", unreal.TextureGroup.TEXTUREGROUP_UI
    )
    texture.set_editor_property(
        "mip_gen_settings", unreal.TextureMipGenSettings.TMGS_NO_MIPMAPS
    )
    texture.set_editor_property(
        "compression_settings",
        unreal.TextureCompressionSettings.TC_DEFAULT,
    )
    texture.set_editor_property("filter", unreal.TextureFilter.TF_TRILINEAR)
    unreal.EditorAssetLibrary.save_loaded_asset(
        texture, only_if_is_dirty=False
    )
    unreal.log(
        "PLAYER_AVATAR_PLACEHOLDER_IMPORTED "
        f"name={asset_name} size={texture.blueprint_get_size_x()}x"
        f"{texture.blueprint_get_size_y()} ui_group=true alpha=true"
    )

unreal.EditorAssetLibrary.save_directory(
    DESTINATION, only_if_is_dirty=False, recursive=False
)
unreal.log("PLAYER_AVATAR_PLACEHOLDERS_IMPORT_OK count=2")
