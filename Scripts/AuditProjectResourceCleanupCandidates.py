"""Generate a read-only audit of project resource cleanup candidates."""

from __future__ import annotations

import json
from pathlib import Path

import unreal


PROJECT_ROOT = Path(__file__).resolve().parents[1]
REPORT_PATH = (
    PROJECT_ROOT / "Saved" / "Reports" / "ProjectResourceCleanupAudit.json"
)
TEXT_ROOTS = (
    PROJECT_ROOT / "Source",
    PROJECT_ROOT / "Scripts",
    PROJECT_ROOT / "Config",
)
TEXT_SUFFIXES = {
    ".cpp",
    ".h",
    ".hpp",
    ".ini",
    ".json",
    ".py",
    ".uproject",
}

# These roots contain entry points or assets loaded by generated runtime paths.
# A zero registry referencer under them is not enough evidence for deletion.
DYNAMIC_OR_ENTRY_PREFIXES = (
    "/Game/Maps/",
    "/Game/Client/",
    "/Game/Art/Mahjong/Mahjong50/",
)
ENTRY_CLASS_NAMES = {
    "AnimBlueprint",
    "Blueprint",
    "DataTable",
    "EditorUtilityBlueprint",
    "EditorUtilityWidgetBlueprint",
    "PrimaryAssetLabel",
    "StringTable",
    "WidgetBlueprint",
    "World",
}


def build_text_corpus() -> str:
    chunks = []
    for root in TEXT_ROOTS:
        if not root.is_dir():
            continue
        for path in root.rglob("*"):
            if not path.is_file() or path.suffix.lower() not in TEXT_SUFFIXES:
                continue
            try:
                chunks.append(path.read_text(encoding="utf-8", errors="ignore"))
            except OSError:
                continue
    return "\n".join(chunks)


def class_name_for_asset(asset_registry, object_path: str) -> str:
    try:
        asset_data = asset_registry.get_asset_by_object_path(object_path)
        class_path = asset_data.get_editor_property("asset_class_path")
        return str(class_path.get_editor_property("asset_name"))
    except Exception:
        asset = unreal.EditorAssetLibrary.load_asset(object_path)
        return asset.get_class().get_name() if asset is not None else "Unknown"


def main() -> None:
    asset_registry = unreal.AssetRegistryHelpers.get_asset_registry()
    asset_registry.wait_for_completion()
    text_corpus = build_text_corpus()
    asset_paths = sorted(
        str(path)
        for path in unreal.EditorAssetLibrary.list_assets(
            "/Game",
            recursive=True,
            include_folder=False,
        )
    )

    redirectors = []
    zero_reference_review = []
    zero_reference_excluded = []
    referenced_assets = 0

    for object_path in asset_paths:
        package_path = object_path.split(".", 1)[0]
        asset_name = package_path.rsplit("/", 1)[-1]
        class_name = class_name_for_asset(asset_registry, object_path)

        if class_name == "ObjectRedirector":
            redirectors.append(
                {
                    "asset": object_path,
                    "class": class_name,
                }
            )
            continue

        referencers = sorted(
            str(path)
            for path in unreal.EditorAssetLibrary.find_package_referencers_for_asset(
                package_path,
                load_assets_to_confirm=False,
            )
        )
        if referencers:
            referenced_assets += 1
            continue

        source_path_reference = package_path in text_corpus
        dynamic_prefix = next(
            (
                prefix
                for prefix in DYNAMIC_OR_ENTRY_PREFIXES
                if package_path.startswith(prefix)
            ),
            None,
        )
        entry_class = class_name in ENTRY_CLASS_NAMES
        record = {
            "asset": object_path,
            "package": package_path,
            "name": asset_name,
            "class": class_name,
            "source_path_reference": source_path_reference,
            "dynamic_or_entry_prefix": dynamic_prefix,
            "entry_class": entry_class,
        }
        if source_path_reference or dynamic_prefix or entry_class:
            zero_reference_excluded.append(record)
        else:
            zero_reference_review.append(record)

    report = {
        "status": "ok",
        "scope": "/Game",
        "read_only": True,
        "asset_count": len(asset_paths),
        "referenced_asset_count": referenced_assets,
        "redirector_count": len(redirectors),
        "zero_reference_review_count": len(zero_reference_review),
        "zero_reference_excluded_count": len(zero_reference_excluded),
        "notes": [
            "Zero Asset Registry referencers do not prove an asset is unused.",
            "Maps, entry-point classes, source soft paths, and generated Mahjong50 paths are excluded from deletion candidates.",
            "No project asset is modified or deleted by this audit.",
        ],
        "redirectors": redirectors,
        "zero_reference_review": zero_reference_review,
        "zero_reference_excluded": zero_reference_excluded,
    }
    REPORT_PATH.parent.mkdir(parents=True, exist_ok=True)
    REPORT_PATH.write_text(
        json.dumps(report, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    unreal.log(
        "[ProjectResourceAudit] PROJECT_RESOURCE_AUDIT_OK "
        f"assets={len(asset_paths)} redirectors={len(redirectors)} "
        f"zero_ref_review={len(zero_reference_review)} "
        f"zero_ref_excluded={len(zero_reference_excluded)} "
        f"report={REPORT_PATH}"
    )


if __name__ == "__main__":
    main()
