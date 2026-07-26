"""Generate mobile PBR textures for the felt panel and center controller."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont, ImageOps, ImageStat


PROJECT_ROOT = Path(__file__).resolve().parents[1]
OUTPUT_DIR = (
    PROJECT_ROOT
    / "SourceArt"
    / "3D"
    / "MahjongTableMobileProduction"
    / "Textures"
)
FELT_SIZE = 8192
SEED = 2026072602
FELT_SET = "TableFeltMobileDeepForest"
DISPLAY_SET = "TableControllerDirectionDisplayMobile"
AI_FELT_SOURCE = OUTPUT_DIR / "Source" / "AI_RealFelt_DeepForest_Source.png"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def save_rgb(path: Path, array: np.ndarray) -> None:
    Image.fromarray(array, mode="RGB").save(
        path,
        format="PNG",
        compress_level=7,
        optimize=False,
    )


def generate_felt() -> dict[str, Path]:
    if not AI_FELT_SOURCE.is_file():
        raise FileNotFoundError(f"Missing generated felt source: {AI_FELT_SOURCE}")
    source = Image.open(AI_FELT_SOURCE).convert("RGB")
    crop_size = min(source.size)
    left = (source.width - crop_size) // 2
    top = (source.height - crop_size) // 2
    source = source.crop((left, top, left + crop_size, top + crop_size))
    source = source.resize(
        (FELT_SIZE // 2, FELT_SIZE // 2),
        Image.Resampling.LANCZOS,
    )

    # Mirrored 2x2 construction guarantees identical opposing borders while
    # preserving the irregular AI-generated short-fiber microstructure.
    top_row = Image.new("RGB", (FELT_SIZE, FELT_SIZE // 2))
    top_row.paste(source, (0, 0))
    top_row.paste(ImageOps.mirror(source), (FELT_SIZE // 2, 0))
    tile = Image.new("RGB", (FELT_SIZE, FELT_SIZE))
    tile.paste(top_row, (0, 0))
    tile.paste(ImageOps.flip(top_row), (0, FELT_SIZE // 2))

    target_mean = (10.6, 32.2, 19.5)
    current_mean = ImageStat.Stat(tile).mean
    calibrated_channels = []
    for channel, current, target in zip(
        tile.split(),
        current_mean,
        target_mean,
    ):
        lut = [
            max(0, min(255, round((value - current) * 0.62 + target)))
            for value in range(256)
        ]
        calibrated_channels.append(channel.point(lut))
    base_image = Image.merge("RGB", calibrated_channels)
    del tile, calibrated_channels

    grayscale = base_image.convert("L")
    fine_blur = grayscale.filter(ImageFilter.GaussianBlur(radius=2.2))
    gray = np.asarray(grayscale, dtype=np.uint8)
    fine = np.asarray(fine_blur, dtype=np.uint8)
    normal = np.empty((FELT_SIZE, FELT_SIZE, 3), dtype=np.uint8)
    orm = np.empty((FELT_SIZE, FELT_SIZE, 3), dtype=np.uint8)
    strip_height = 256
    fine_mean = float(np.mean(fine))
    for y0 in range(0, FELT_SIZE, strip_height):
        y1 = min(FELT_SIZE, y0 + strip_height)
        rows = np.arange(y0, y1)
        previous_rows = (rows - 1) % FELT_SIZE
        next_rows = (rows + 1) % FELT_SIZE
        height = (
            gray[rows].astype(np.float32)
            - fine[rows].astype(np.float32)
        )
        previous = (
            gray[previous_rows].astype(np.float32)
            - fine[previous_rows].astype(np.float32)
        )
        following = (
            gray[next_rows].astype(np.float32)
            - fine[next_rows].astype(np.float32)
        )
        dx = (
            np.roll(height, -1, axis=1)
            - np.roll(height, 1, axis=1)
        ) * 0.23
        dy = (following - previous) * 0.23
        nx = -dx
        ny = -dy
        nz = np.ones_like(nx)
        length = np.sqrt(nx * nx + ny * ny + nz * nz)
        normal[y0:y1, :, 0] = np.clip(
            (nx / length * 0.5 + 0.5) * 255.0,
            0,
            255,
        ).astype(np.uint8)
        normal[y0:y1, :, 1] = np.clip(
            (ny / length * 0.5 + 0.5) * 255.0,
            0,
            255,
        ).astype(np.uint8)
        normal[y0:y1, :, 2] = np.clip(
            (nz / length * 0.5 + 0.5) * 255.0,
            0,
            255,
        ).astype(np.uint8)

        nap = fine[rows].astype(np.float32) - fine_mean
        orm[y0:y1, :, 0] = np.clip(
            250.0 + height * 0.35,
            242,
            255,
        ).astype(np.uint8)
        orm[y0:y1, :, 1] = np.clip(
            224.0 + nap * 0.55 - height * 0.45,
            198,
            244,
        ).astype(np.uint8)
        orm[y0:y1, :, 2] = 0
    del gray, fine
    vertical_edge = (
        normal[:, 0, :].astype(np.uint16)
        + normal[:, -1, :].astype(np.uint16)
    ) // 2
    normal[:, 0, :] = vertical_edge.astype(np.uint8)
    normal[:, -1, :] = vertical_edge.astype(np.uint8)
    horizontal_edge = (
        normal[0, :, :].astype(np.uint16)
        + normal[-1, :, :].astype(np.uint16)
    ) // 2
    normal[0, :, :] = horizontal_edge.astype(np.uint8)
    normal[-1, :, :] = horizontal_edge.astype(np.uint8)

    files = {
        "BaseColor": OUTPUT_DIR / f"T_{FELT_SET}_BaseColor_8K.png",
        "Normal": OUTPUT_DIR / f"T_{FELT_SET}_Normal_8K.png",
        "ORM": OUTPUT_DIR / f"T_{FELT_SET}_ORM_8K.png",
    }
    base_image.save(
        files["BaseColor"],
        format="PNG",
        compress_level=7,
        optimize=False,
    )
    save_rgb(files["Normal"], normal)
    del normal
    save_rgb(files["ORM"], orm)
    del orm
    return files


def generate_controller_display() -> dict[str, Path]:
    supersample = 1024
    output_size = 512
    image = Image.new("RGB", (supersample, supersample), (4, 4, 4))
    draw = ImageDraw.Draw(image)
    center = supersample // 2
    outer = 476
    gold = (224, 152, 40)
    dark_gold = (111, 70, 20)
    panel = (8, 8, 8)
    draw.ellipse(
        (center - outer, center - outer, center + outer, center + outer),
        fill=panel,
        outline=gold,
        width=16,
    )
    draw.ellipse((188, 188, 836, 836), outline=dark_gold, width=7)
    inner_radius = 150
    draw.ellipse(
        (
            center - inner_radius,
            center - inner_radius,
            center + inner_radius,
            center + inner_radius,
        ),
        fill=(2, 5, 6),
        outline=gold,
        width=10,
    )
    for angle in (45.0, 135.0, 225.0, 315.0):
        radians = np.deg2rad(angle)
        start = (
            center + int(np.cos(radians) * inner_radius),
            center + int(np.sin(radians) * inner_radius),
        )
        end = (
            center + int(np.cos(radians) * (outer - 16)),
            center + int(np.sin(radians) * (outer - 16)),
        )
        draw.line((start, end), fill=dark_gold, width=8)

    font_path = Path(r"C:\Windows\Fonts\msyhbd.ttc")
    if not font_path.is_file():
        raise FileNotFoundError(f"Missing Chinese font: {font_path}")
    font = ImageFont.truetype(str(font_path), 112)
    labels = (
        ("北", (center, 236)),
        ("东", (788, center)),
        ("南", (center, 788)),
        ("西", (236, center)),
    )
    for text, position in labels:
        box = draw.textbbox((0, 0), text, font=font)
        width = box[2] - box[0]
        height = box[3] - box[1]
        draw.text(
            (position[0] - width / 2, position[1] - height / 2 - box[1]),
            text,
            font=font,
            fill=gold,
        )

    image = image.resize(
        (output_size, output_size),
        Image.Resampling.LANCZOS,
    )
    base_path = OUTPUT_DIR / f"T_{DISPLAY_SET}_BaseColor_512.png"
    image.save(base_path, format="PNG", compress_level=7)

    normal = np.zeros((output_size, output_size, 3), dtype=np.uint8)
    normal[:, :, 0] = 128
    normal[:, :, 1] = 128
    normal[:, :, 2] = 255
    normal_path = OUTPUT_DIR / f"T_{DISPLAY_SET}_Normal_512.png"
    save_rgb(normal_path, normal)

    orm = np.zeros((output_size, output_size, 3), dtype=np.uint8)
    orm[:, :, 0] = 255
    orm[:, :, 1] = 56
    orm[:, :, 2] = 72
    orm_path = OUTPUT_DIR / f"T_{DISPLAY_SET}_ORM_512.png"
    save_rgb(orm_path, orm)
    return {
        "BaseColor": base_path,
        "Normal": normal_path,
        "ORM": orm_path,
    }


def main() -> None:
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    for suffix in ("1K", "4K", "8K"):
        for obsolete in OUTPUT_DIR.glob(f"T_{FELT_SET}_*_{suffix}.png"):
            obsolete.unlink()
    felt_files = generate_felt()
    display_files = generate_controller_display()
    all_files = {
        **{f"felt_{key}": value for key, value in felt_files.items()},
        **{f"display_{key}": value for key, value in display_files.items()},
    }

    manifest = {
        "status": "ok",
        "generator": Path(__file__).name,
        "mobile_profile": True,
        "materials": {
            "M_Table_Felt_Mobile": {
                "asset": "M_Table_Felt_DeepForest_Mobile",
                "texture_set": FELT_SET,
                "resolution_suffix": "8K",
                "blend": "opaque",
            },
            "M_Table_Felt_Edge_Mobile": {
                "asset": "M_Table_Felt_Edge_Mobile",
                "texture_set": None,
                "blend": "opaque_constant",
                "base_color": [0.004, 0.016, 0.010],
                "metallic": 0.0,
                "roughness": 0.90,
            },
            "M_Table_Controller_Gunmetal_Mobile": {
                "asset": "M_Table_Controller_Gunmetal_Mobile",
                "texture_set": None,
                "blend": "opaque_constant",
                "base_color": [0.008, 0.010, 0.012],
                "metallic": 0.80,
                "roughness": 0.24,
            },
            "M_Table_Controller_Display_Mobile": {
                "asset": "M_Table_Controller_Display_Mobile",
                "texture_set": DISPLAY_SET,
                "resolution_suffix": "512",
                "blend": "opaque",
            },
            "M_Table_Controller_Glass_Mobile": {
                "asset": "M_Table_Controller_Glass_Mobile",
                "texture_set": None,
                "blend": "translucent",
                "base_color": [0.18, 0.25, 0.28],
                "roughness": 0.025,
                "opacity": 0.14,
                "refraction": 1.46,
            },
        },
        "files": [
            {
                "channel": channel.split("_", 1)[1],
                "name": path.name,
                "bytes": path.stat().st_size,
                "sha256": sha256(path),
            }
            for channel, path in all_files.items()
        ],
    }
    manifest_path = OUTPUT_DIR / "MahjongTableMobileTextureManifest.json"
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    print(
        "MOBILE_MAHJONG_FELT_TEXTURES_OK",
        "felt_resolution=8192x8192",
        "controller_resolution=512x512",
        f"textures={len(all_files)}",
        f"manifest={manifest_path}",
    )


if __name__ == "__main__":
    main()
