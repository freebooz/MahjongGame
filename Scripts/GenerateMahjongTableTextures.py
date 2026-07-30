# 生成移动端桌面麻将资产的 PBR 纹理集合，在视觉质量和显存预算之间保持约束。
# 全量重建前精确删除目标旧纹理；通道命名、色彩空间和尺寸必须符合 Unreal 导入规范。
"""Generate the mobile PBR texture set for the tabletop-only Mahjong asset.

The output uses the Unreal-friendly metallic/roughness workflow:

* BaseColor: sRGB RGB texture
* Normal: tangent-space DirectX normal texture
* Roughness: linear grayscale texture
* AO: linear grayscale texture

The generator uses NumPy and Pillow to keep the full 2K build deterministic and
fast enough for iterative art review.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import struct
import zlib
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

import numpy as np
from PIL import Image


PixelFunction = Callable[[int, int, int], tuple[int, int, int]]


@dataclass(frozen=True)
class MaterialSpec:
    slot: str
    stem: str
    base_color: tuple[int, int, int]
    roughness: float
    metallic: float
    pattern: str
    normal_strength: float = 1.0


MATERIALS = (
    MaterialSpec("M_Table_Walnut_PBR", "Wood", (92, 34, 10), 0.29, 0.0, "wood", 0.30),
    MaterialSpec("M_Table_Felt_Green_PBR", "Felt", (6, 52, 18), 0.84, 0.0, "felt", 0.52),
)


def clamp_byte(value: float) -> int:
    return max(0, min(255, round(value)))


def noise(x: int, y: int, seed: int = 0) -> float:
    value = (x * 374761393 + y * 668265263 + seed * 1442695041) & 0xFFFFFFFF
    value = ((value ^ (value >> 13)) * 1274126177) & 0xFFFFFFFF
    value ^= value >> 16
    return (value & 0xFFFF) / 65535.0


def height_value(spec: MaterialSpec, x: int, y: int, size: int) -> float:
    u = x / size
    v = y / size
    n = noise(x, y, len(spec.stem))
    if spec.pattern == "felt":
        # Interlaced warp/weft threads plus directional nap at three scales.
        warp = math.sin(x * math.tau / 3.7)
        weft = math.sin(y * math.tau / 4.3)
        interlace = warp * weft
        diagonal_fiber = math.sin((x + y * 1.17) * math.tau / 17.0)
        nap = math.sin((x * 0.23 + y) * math.tau / 61.0)
        coarse = noise(x // 7, y // 7, 37) - 0.5
        return (
            0.25 * warp
            + 0.22 * weft
            + 0.19 * interlace
            + 0.09 * diagonal_fiber
            + 0.07 * nap
            + 0.15 * (n - 0.5)
            + 0.10 * coarse
        )
    if spec.pattern == "wood":
        flowing = (
            u * 27.0
            + math.sin(v * math.tau * 4.6) * 0.82
            + math.sin((v * 11.0 + u * 2.7) * math.tau) * 0.22
        )
        grain = math.sin(flowing * math.tau)
        cathedral = math.sin(
            (u * 8.5 + math.sin(v * math.tau * 2.1) * 1.25) * math.tau
        )
        pores = math.sin((u * 123.0 + v * 3.2) * math.tau) * 0.16
        return grain * 0.39 + cathedral * 0.20 + pores + (n - 0.5) * 0.09
    if spec.pattern == "grille":
        cell = max(12, size // 24)
        px = (x % cell) / cell - 0.5
        py = (y % cell) / cell - 0.5
        hole = 1.0 if px * px + py * py < 0.075 else 0.0
        return -0.85 * hole + (n - 0.5) * 0.06
    if spec.pattern == "brushed":
        return math.sin(y * math.tau / 5.0) * 0.12 + (n - 0.5) * 0.08
    if spec.pattern == "powder":
        return (n - 0.5) * 0.55
    if spec.pattern == "rubber":
        bumps = math.sin(x * math.tau / 9.0) * math.sin(y * math.tau / 9.0)
        return bumps * 0.22 + (n - 0.5) * 0.16
    if spec.pattern == "composite":
        return math.sin((u * 8.0 + v * 5.0) * math.tau) * 0.08 + (n - 0.5) * 0.12
    if spec.pattern == "lens":
        return math.sin(u * math.tau) * math.sin(v * math.tau) * 0.025
    return (n - 0.5) * 0.04


def color_variation(spec: MaterialSpec, x: int, y: int, size: int) -> float:
    h = height_value(spec, x, y, size)
    if spec.pattern == "felt":
        directional_nap = math.sin((x * 0.18 + y) * math.tau / 113.0)
        return h * 0.045 + directional_nap * 0.010
    if spec.pattern == "wood":
        return h * 0.42
    if spec.pattern == "grille":
        return -0.58 if h < -0.5 else h * 0.12
    if spec.pattern == "powder":
        return h * 0.055
    if spec.pattern == "rubber":
        return h * 0.04
    if spec.pattern == "brushed":
        return h * 0.035
    return h * 0.018


def base_color_pixel(spec: MaterialSpec) -> PixelFunction:
    def pixel(x: int, y: int, size: int) -> tuple[int, int, int]:
        variation = color_variation(spec, x, y, size)
        multiplier = max(0.12, 1.0 + variation)
        return tuple(clamp_byte(channel * multiplier) for channel in spec.base_color)

    return pixel


def normal_pixel(spec: MaterialSpec) -> PixelFunction:
    def pixel(x: int, y: int, size: int) -> tuple[int, int, int]:
        left = height_value(spec, (x - 1) % size, y, size)
        right = height_value(spec, (x + 1) % size, y, size)
        down = height_value(spec, x, (y - 1) % size, size)
        up = height_value(spec, x, (y + 1) % size, size)
        nx = (left - right) * spec.normal_strength
        # DirectX tangent space uses a downward/negative green channel.
        ny = (up - down) * spec.normal_strength
        nz = 1.0
        length = math.sqrt(nx * nx + ny * ny + nz * nz)
        return (
            clamp_byte((nx / length * 0.5 + 0.5) * 255.0),
            clamp_byte((ny / length * 0.5 + 0.5) * 255.0),
            clamp_byte((nz / length * 0.5 + 0.5) * 255.0),
        )

    return pixel


def roughness_pixel(spec: MaterialSpec) -> PixelFunction:
    def pixel(x: int, y: int, size: int) -> tuple[int, int, int]:
        h = height_value(spec, x, y, size)
        rough = spec.roughness + h * (0.055 if spec.pattern in {"felt", "wood", "powder"} else 0.025)
        value = clamp_byte(max(0.03, min(1.0, rough)) * 255.0)
        return (value, value, value)

    return pixel


def ao_pixel(spec: MaterialSpec) -> PixelFunction:
    def pixel(x: int, y: int, size: int) -> tuple[int, int, int]:
        h = height_value(spec, x, y, size)
        ao = 0.94 + min(0.04, max(-0.08, h * 0.035))
        value = clamp_byte(ao * 255.0)
        return (value, value, value)

    return pixel


def png_chunk(kind: bytes, payload: bytes) -> bytes:
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)


def write_rgb_png(path: Path, size: int, pixel: PixelFunction) -> None:
    scanlines = bytearray()
    for y in range(size):
        scanlines.append(0)
        for x in range(size):
            scanlines.extend(pixel(x, y, size))
    header = struct.pack(">IIBBBBB", size, size, 8, 2, 0, 0, 0)
    data = b"\x89PNG\r\n\x1a\n"
    data += png_chunk(b"IHDR", header)
    data += png_chunk(b"IDAT", zlib.compress(bytes(scanlines), level=9))
    data += png_chunk(b"IEND", b"")
    path.write_bytes(data)


def vector_height(spec: MaterialSpec, size: int) -> np.ndarray:
    """Vectorized deterministic height field used by all PBR channels."""

    y_pixels, x_pixels = np.mgrid[0:size, 0:size]
    x = x_pixels.astype(np.float32)
    y = y_pixels.astype(np.float32)
    u = x / np.float32(size)
    v = y / np.float32(size)
    rng = np.random.default_rng(1701 if spec.pattern == "wood" else 5107)
    noise_field = rng.random((size, size), dtype=np.float32) - np.float32(0.5)
    tau = np.float32(math.tau)
    if spec.pattern == "wood":
        flowing = (
            u * np.float32(5.6)
            + np.sin(v * tau * np.float32(3.1)) * np.float32(0.52)
            + np.sin((v * np.float32(7.0) + u * np.float32(1.9)) * tau)
            * np.float32(0.18)
        )
        grain = np.sin(flowing * tau)
        cathedral = np.sin(
            (u * np.float32(2.2) + np.sin(v * tau * np.float32(1.7)) * np.float32(0.72))
            * tau
        )
        pores = (
            np.sin((u * np.float32(42.0) + v * np.float32(2.4)) * tau)
            * np.float32(0.10)
        )
        return (
            grain * np.float32(0.39)
            + cathedral * np.float32(0.20)
            + pores
            + noise_field * np.float32(0.09)
        )

    warp = np.sin(x * tau / np.float32(3.7))
    weft = np.sin(y * tau / np.float32(4.3))
    interlace = warp * weft
    diagonal = np.sin((x + y * np.float32(1.17)) * tau / np.float32(17.0))
    nap = np.sin((x * np.float32(0.23) + y) * tau / np.float32(61.0))
    coarse_size = max(1, math.ceil(size / 7))
    coarse = rng.random((coarse_size, coarse_size), dtype=np.float32)
    coarse = np.repeat(np.repeat(coarse, 7, axis=0), 7, axis=1)[:size, :size]
    coarse -= np.float32(0.5)
    return (
        warp * np.float32(0.25)
        + weft * np.float32(0.22)
        + interlace * np.float32(0.19)
        + diagonal * np.float32(0.09)
        + nap * np.float32(0.07)
        + noise_field * np.float32(0.15)
        + coarse * np.float32(0.10)
    )


def save_vectorized_pbr(spec: MaterialSpec, size: int, output_dir: Path) -> None:
    height = vector_height(spec, size)
    if spec.pattern == "wood":
        variation = height * np.float32(0.32)
    else:
        y_pixels, x_pixels = np.mgrid[0:size, 0:size]
        directional_nap = np.sin(
            (x_pixels.astype(np.float32) * np.float32(0.18) + y_pixels)
            * np.float32(math.tau / 113.0)
        )
        variation = height * np.float32(0.045) + directional_nap * np.float32(0.010)

    base = np.asarray(spec.base_color, dtype=np.float32)[None, None, :]
    multiplier = np.maximum(np.float32(0.12), np.float32(1.0) + variation)
    base_color = np.clip(base * multiplier[:, :, None], 0.0, 255.0).astype(np.uint8)

    left = np.roll(height, 1, axis=1)
    right = np.roll(height, -1, axis=1)
    down = np.roll(height, 1, axis=0)
    up = np.roll(height, -1, axis=0)
    nx = (left - right) * np.float32(spec.normal_strength)
    ny = (up - down) * np.float32(spec.normal_strength)
    nz = np.ones_like(nx)
    length = np.sqrt(nx * nx + ny * ny + nz * nz)
    normal = np.stack(
        (
            nx / length * np.float32(0.5) + np.float32(0.5),
            ny / length * np.float32(0.5) + np.float32(0.5),
            nz / length * np.float32(0.5) + np.float32(0.5),
        ),
        axis=2,
    )
    normal = np.clip(normal * np.float32(255.0), 0.0, 255.0).astype(np.uint8)

    roughness = np.clip(
        np.float32(spec.roughness) + height * np.float32(0.055),
        np.float32(0.03),
        np.float32(1.0),
    )
    roughness_rgb = np.repeat(
        (roughness * np.float32(255.0)).astype(np.uint8)[:, :, None], 3, axis=2
    )
    ao = np.float32(0.94) + np.clip(
        height * np.float32(0.035), np.float32(-0.08), np.float32(0.04)
    )
    ao_rgb = np.repeat(
        (np.clip(ao, 0.0, 1.0) * np.float32(255.0)).astype(np.uint8)[:, :, None],
        3,
        axis=2,
    )

    channels = {
        "BaseColor": base_color,
        "Normal": normal,
        "Roughness": roughness_rgb,
        "AO": ao_rgb,
    }
    for channel, pixels in channels.items():
        Image.fromarray(pixels, mode="RGB").save(
            output_dir / f"T_{spec.stem}_{channel}_2K.png",
            format="PNG",
            optimize=True,
            compress_level=7,
        )


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--size", type=int, default=512, help="Square texture resolution")
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument(
        "--only",
        choices=("All", "Wood", "Felt"),
        default="All",
        help="Regenerate one material while preserving and re-manifesting the complete set",
    )
    args = parser.parse_args()
    if args.size < 64 or args.size > 4096 or args.size & (args.size - 1):
        raise SystemExit("--size must be a power of two between 64 and 4096")

    project_root = Path(__file__).resolve().parents[1]
    output_dir = args.output_dir or project_root / "SourceArt" / "3D" / "MahjongTable" / "Textures"
    output_dir.mkdir(parents=True, exist_ok=True)
    selected_materials = (
        MATERIALS if args.only == "All" else tuple(spec for spec in MATERIALS if spec.stem == args.only)
    )
    for spec in selected_materials:
        save_vectorized_pbr(spec, args.size, output_dir)
    generated = []
    for spec in MATERIALS:
        generated.append(
            {
                "material_slot": spec.slot,
                "textures": {
                    channel: f"T_{spec.stem}_{channel}_2K.png"
                    for channel in ("BaseColor", "Normal", "Roughness", "AO")
                },
                "roughness": spec.roughness,
                "metallic": spec.metallic,
            }
        )

    files = sorted(output_dir.glob("T_*_2K.png"))
    manifest = {
        "workflow": "Unreal metallic-roughness PBR",
        "resolution": [args.size, args.size],
        "resolution_label": "2K",
        "channels": ["BaseColor", "Normal", "Roughness", "AO"],
        "materials": generated,
        "files": [{"name": path.name, "bytes": path.stat().st_size, "sha256": sha256(path)} for path in files],
    }
    manifest_path = output_dir / "MahjongTableTextureManifest.json"
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"PBR_TEXTURE_SET={output_dir}")
    print(f"MATERIAL_COUNT={len(MATERIALS)}")
    print(f"TEXTURE_COUNT={len(files)}")
    print(f"TEXTURE_RESOLUTION={args.size}x{args.size}")


if __name__ == "__main__":
    main()
