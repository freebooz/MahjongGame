"""Acquire and prepare CC0 photogrammetry PBR maps for the Mahjong table.

Sources:
* Poly Haven "Wood Table 001" for stained varnished furniture wood.
* Poly Haven "Stretch Poplin" for the fine green woven playing surface.

The original 2K JPEG files are retained under ``PolyHavenSource`` and the
Unreal-ready PNG derivatives keep the stable T_Wood/T_Felt asset names.
"""

from __future__ import annotations

import hashlib
import json
import urllib.request
from pathlib import Path

import numpy as np
from PIL import Image, ImageEnhance


PROJECT_ROOT = Path(__file__).resolve().parents[1]
TEXTURE_ROOT = PROJECT_ROOT / "SourceArt" / "3D" / "MahjongTable" / "Textures"
SOURCE_ROOT = TEXTURE_ROOT / "PolyHavenSource"
SIZE = 2048

ASSETS = {
    "Wood": {
        "id": "wood_table_001",
        "title": "Wood Table 001",
        "page": "https://polyhaven.com/a/wood_table_001",
        "channels": {
            "BaseColor": "https://dl.polyhaven.org/file/ph-assets/Textures/jpg/2k/wood_table_001/wood_table_001_diff_2k.jpg",
            "Normal": "https://dl.polyhaven.org/file/ph-assets/Textures/jpg/2k/wood_table_001/wood_table_001_nor_dx_2k.jpg",
            "Roughness": "https://dl.polyhaven.org/file/ph-assets/Textures/jpg/2k/wood_table_001/wood_table_001_rough_2k.jpg",
            "AO": "https://dl.polyhaven.org/file/ph-assets/Textures/jpg/2k/wood_table_001/wood_table_001_ao_2k.jpg",
        },
    },
    "Felt": {
        "id": "stretch_poplin",
        "title": "Stretch Poplin",
        "page": "https://polyhaven.com/a/stretch_poplin",
        "channels": {
            "BaseColor": "https://dl.polyhaven.org/file/ph-assets/Textures/jpg/2k/stretch_poplin/stretch_poplin_diff_2k.jpg",
            "Normal": "https://dl.polyhaven.org/file/ph-assets/Textures/jpg/2k/stretch_poplin/stretch_poplin_nor_dx_2k.jpg",
            "Roughness": "https://dl.polyhaven.org/file/ph-assets/Textures/jpg/2k/stretch_poplin/stretch_poplin_rough_2k.jpg",
            "AO": "https://dl.polyhaven.org/file/ph-assets/Textures/jpg/2k/stretch_poplin/stretch_poplin_ao_2k.jpg",
        },
    },
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def acquire(url: str, destination: Path) -> None:
    if destination.is_file() and destination.stat().st_size > 0:
        return
    destination.parent.mkdir(parents=True, exist_ok=True)
    request = urllib.request.Request(url, headers={"User-Agent": "GuiyangMahjongAssetBuild/1.0"})
    with urllib.request.urlopen(request, timeout=60) as response:
        destination.write_bytes(response.read())


def rgb(image: Image.Image) -> Image.Image:
    return image.convert("RGB").resize((SIZE, SIZE), Image.Resampling.LANCZOS)


def prepare_wood(channel: str, source: Image.Image) -> Image.Image:
    image = rgb(source)
    if channel == "BaseColor":
        image = ImageEnhance.Color(image).enhance(0.92)
        image = ImageEnhance.Contrast(image).enhance(1.08)
        image = ImageEnhance.Brightness(image).enhance(0.98)
    elif channel == "Roughness":
        values = np.asarray(image.convert("L"), dtype=np.float32) / 255.0
        # A polished furniture lacquer: preserve scanned variation while
        # compressing it into a satin 0.20-0.39 roughness range.
        values = np.clip(values * 0.28 + 0.19, 0.28, 0.45)
        mono = (values * 255.0).astype(np.uint8)
        image = Image.fromarray(np.repeat(mono[:, :, None], 3, axis=2), "RGB")
    return image


def prepare_felt(channel: str, source: Image.Image) -> Image.Image:
    image = rgb(source)
    if channel == "BaseColor":
        values = np.asarray(image, dtype=np.float32)
        luminance = (
            values[:, :, 0] * 0.2126
            + values[:, :, 1] * 0.7152
            + values[:, :, 2] * 0.0722
        )
        average = max(float(luminance.mean()), 1.0)
        shade = np.power(np.clip(luminance / average, 0.55, 1.55), 0.72)
        target = np.asarray((12.0, 56.0, 27.0), dtype=np.float32)
        output = np.clip(shade[:, :, None] * target[None, None, :], 0.0, 255.0)
        image = Image.fromarray(output.astype(np.uint8), "RGB")
    elif channel == "Roughness":
        values = np.asarray(image.convert("L"), dtype=np.float32) / 255.0
        values = np.clip(values * 0.22 + 0.69, 0.72, 0.91)
        mono = (values * 255.0).astype(np.uint8)
        image = Image.fromarray(np.repeat(mono[:, :, None], 3, axis=2), "RGB")
    return image


def main() -> None:
    TEXTURE_ROOT.mkdir(parents=True, exist_ok=True)
    SOURCE_ROOT.mkdir(parents=True, exist_ok=True)
    output_files: list[Path] = []
    source_files: list[dict[str, object]] = []

    for material, spec in ASSETS.items():
        asset_source = SOURCE_ROOT / spec["id"]
        for channel, url in spec["channels"].items():
            source_path = asset_source / Path(url).name
            acquire(url, source_path)
            source_files.append(
                {
                    "material": material,
                    "channel": channel,
                    "path": str(source_path.relative_to(PROJECT_ROOT)).replace("\\", "/"),
                    "url": url,
                    "bytes": source_path.stat().st_size,
                    "sha256": sha256(source_path),
                }
            )
            with Image.open(source_path) as source_image:
                prepared = (
                    prepare_wood(channel, source_image)
                    if material == "Wood"
                    else prepare_felt(channel, source_image)
                )
                output_path = TEXTURE_ROOT / f"T_{material}_{channel}_2K.png"
                prepared.save(output_path, "PNG", optimize=True, compress_level=7)
                output_files.append(output_path)

    material_specs = [
        {
            "material_slot": "M_Table_Walnut_PBR",
            "source": ASSETS["Wood"]["page"],
            "textures": {
                channel: f"T_Wood_{channel}_2K.png"
                for channel in ("BaseColor", "Normal", "Roughness", "AO")
            },
            "roughness": "texture",
            "metallic": 0.0,
        },
        {
            "material_slot": "M_Table_Felt_Green_PBR",
            "source": ASSETS["Felt"]["page"],
            "textures": {
                channel: f"T_Felt_{channel}_2K.png"
                for channel in ("BaseColor", "Normal", "Roughness", "AO")
            },
            "roughness": "texture",
            "metallic": 0.0,
        },
    ]
    manifest = {
        "workflow": "Unreal metallic-roughness PBR",
        "resolution": [SIZE, SIZE],
        "resolution_label": "2K",
        "license": "CC0 1.0",
        "provider": "Poly Haven",
        "provider_url": "https://polyhaven.com/",
        "channels": ["BaseColor", "Normal", "Roughness", "AO"],
        "materials": material_specs,
        "source_files": source_files,
        "files": [
            {
                "name": path.name,
                "bytes": path.stat().st_size,
                "sha256": sha256(path),
            }
            for path in sorted(output_files)
        ],
    }
    (TEXTURE_ROOT / "MahjongTableTextureManifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    (SOURCE_ROOT / "LICENSE_AND_ATTRIBUTION.md").write_text(
        "# Mahjong table PBR sources\n\n"
        "Both source texture sets are dedicated to the public domain under "
        "[CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/).\n\n"
        "- [Wood Table 001](https://polyhaven.com/a/wood_table_001), "
        "Dimitrios Savva and Rico Cilliers, Poly Haven.\n"
        "- [Stretch Poplin](https://polyhaven.com/a/stretch_poplin), "
        "Rico Cilliers and colormass, Poly Haven.\n",
        encoding="utf-8",
    )
    print(f"PBR_TEXTURE_SET={TEXTURE_ROOT}")
    print("PROVIDER=Poly Haven")
    print("LICENSE=CC0 1.0")
    print(f"TEXTURE_COUNT={len(output_files)}")
    print(f"TEXTURE_RESOLUTION={SIZE}x{SIZE}")


if __name__ == "__main__":
    main()
