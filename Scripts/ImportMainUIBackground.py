"""Import the approved pavilion image across the main non-game UI screens."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

import unreal


PROJECT_ROOT = Path(unreal.Paths.project_dir())
SOURCE_ROOT = PROJECT_ROOT / "SourceArt" / "UI" / "Backgrounds"
DESTINATION = "/Game/UI/Textures/Backgrounds"
EXPECTED_SIZE = (1672, 941)
BACKGROUND_NAMES = (
    "T_BG_Login_Guiyang",
    "T_BG_Lobby_JiaxiuTower",
    "T_BG_CreatingRoom_GuiyangMoon",
)
REPORT_PATH = PROJECT_ROOT / "Saved" / "Reports" / "MainUIBackgroundImportReport.json"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def set_property(obj, name: str, value) -> None:
    try:
        obj.set_editor_property(name, value)
    except Exception as exc:
        unreal.log_warning(
            f"[MainUIBackgroundImport] Could not set {name} on "
            f"{obj.get_path_name()}: {exc}"
        )


def main() -> None:
    tasks: list[unreal.AssetImportTask] = []
    source_files: dict[str, Path] = {}
    for name in BACKGROUND_NAMES:
        source = SOURCE_ROOT / f"{name}.png"
        if not source.is_file():
            raise RuntimeError(f"Missing approved UI background source: {source}")
        source_files[name] = source
        task = unreal.AssetImportTask()
        task.filename = str(source)
        task.destination_path = DESTINATION
        task.destination_name = name
        task.automated = True
        task.replace_existing = True
        task.replace_existing_settings = True
        task.save = True
        tasks.append(task)

    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks(tasks)
    imported: list[dict[str, object]] = []
    for name in BACKGROUND_NAMES:
        asset_path = f"{DESTINATION}/{name}"
        texture = unreal.EditorAssetLibrary.load_asset(asset_path)
        if not texture:
            raise RuntimeError(f"Failed to import main UI background: {asset_path}")
        set_property(
            texture,
            "compression_settings",
            unreal.TextureCompressionSettings.TC_EDITOR_ICON,
        )
        set_property(texture, "lod_group", unreal.TextureGroup.TEXTUREGROUP_UI)
        set_property(texture, "srgb", True)
        set_property(texture, "never_stream", False)
        set_property(texture, "max_texture_size", 2048)
        post_edit_change = getattr(texture, "post_edit_change", None)
        if post_edit_change:
            post_edit_change()
        unreal.EditorAssetLibrary.save_loaded_asset(texture, only_if_is_dirty=False)

        size = (int(texture.blueprint_get_size_x()), int(texture.blueprint_get_size_y()))
        if size != EXPECTED_SIZE:
            raise RuntimeError(
                f"Unexpected imported size for {asset_path}: {size[0]}x{size[1]}"
            )
        referencers = unreal.EditorAssetLibrary.find_package_referencers_for_asset(
            asset_path, load_assets_to_confirm=True
        )
        imported.append(
            {
                "asset": asset_path,
                "source": str(source_files[name]),
                "source_sha256": sha256(source_files[name]),
                "dimensions": list(size),
                "referencers": sorted(str(path) for path in referencers),
            }
        )

    source_hashes = {entry["source_sha256"] for entry in imported}
    if len(source_hashes) != 1:
        raise RuntimeError("Main UI screens were not imported from the same approved image")

    report = {
        "status": "ok",
        "approved_source_dimensions": list(EXPECTED_SIZE),
        "shared_source_sha256": next(iter(source_hashes)),
        "screens": [
            "Login",
            "ConnectServer",
            "Lobby",
            "CreatingRoom",
        ],
        "imported": imported,
    }
    REPORT_PATH.parent.mkdir(parents=True, exist_ok=True)
    REPORT_PATH.write_text(
        json.dumps(report, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    unreal.log(
        "[MainUIBackgroundImport] MAIN_UI_BACKGROUND_IMPORT_OK "
        f"assets={len(imported)} dimensions={EXPECTED_SIZE[0]}x{EXPECTED_SIZE[1]}"
    )


if __name__ == "__main__":
    main()
