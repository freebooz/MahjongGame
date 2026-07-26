"""Generate deterministic 2K PBR textures for the approved Mahjong table."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image


PROJECT_ROOT = Path(__file__).resolve().parents[1]
OUTPUT_DIR = (
    PROJECT_ROOT / "SourceArt" / "3D" / "MahjongTableProduction" / "Textures"
)
SIZE = 2048
SEED = 20260726

SETS = {
    "TableFrameBlackBronze": {
        "base": (6, 4, 3),
        "variation": (8, 5, 2),
        "roughness": 49,
        "metallic": 224,
        "normal_strength": 0.52,
        "pattern": "brushed",
    },
    "TableFeltDeepForest": {
        "base": (7, 22, 17),
        "variation": (3, 10, 7),
        "roughness": 232,
        "metallic": 0,
        "normal_strength": 1.45,
        "pattern": "woven",
    },
    "TableGold": {
        "base": (178, 79, 10),
        "variation": (42, 24, 7),
        "roughness": 43,
        "metallic": 242,
        "normal_strength": 0.30,
        "pattern": "brushed",
    },
    "TableControllerGunmetal": {
        "base": (5, 7, 9),
        "variation": (9, 11, 14),
        "roughness": 61,
        "metallic": 212,
        "normal_strength": 0.38,
        "pattern": "brushed",
    },
    "TableControllerBlack": {
        "base": (2, 4, 4),
        "variation": (4, 7, 7),
        "roughness": 48,
        "metallic": 72,
        "normal_strength": 0.18,
        "pattern": "matte",
    },
}

MATERIALS = {
    "M_Table_Frame_BlackAlloy": {
        "asset": "M_Table_Frame_BlackBronze_PBR",
        "texture_set": "TableFrameBlackBronze",
        "blend": "opaque",
    },
    "M_Table_Felt_Green_PBR": {
        "asset": "M_Table_Felt_DeepForest_PBR",
        "texture_set": "TableFeltDeepForest",
        "blend": "opaque",
    },
    "M_Table_Frame_GoldInlay": {
        "asset": "M_Table_Frame_GoldInlay_PBR",
        "texture_set": "TableGold",
        "blend": "opaque",
    },
    "M_Table_Controller_Gunmetal": {
        "asset": "M_Table_Controller_Gunmetal_PBR",
        "texture_set": "TableControllerGunmetal",
        "blend": "opaque",
    },
    "M_Table_Controller_GoldLabel": {
        "asset": "M_Table_Controller_Gold_PBR",
        "texture_set": "TableGold",
        "blend": "opaque",
    },
    "M_Table_Controller_BlackPanel": {
        "asset": "M_Table_Controller_BlackPanel_PBR",
        "texture_set": "TableControllerBlack",
        "blend": "opaque",
    },
    "M_Table_Controller_SectorDividerGold": {
        "asset": "M_Table_Controller_DividerGold_PBR",
        "texture_set": "TableGold",
        "blend": "opaque",
    },
    "M_Table_Controller_DirectionGold": {
        "asset": "M_Table_Controller_DirectionGold_PBR",
        "texture_set": "TableGold",
        "blend": "opaque",
        "emissive_scale": 0.55,
    },
    "M_Table_Controller_TransparentGlass": {
        "asset": "M_Table_Controller_SmoothGlass",
        "texture_set": None,
        "blend": "translucent",
        "base_color": [0.20, 0.28, 0.32],
        "roughness": 0.012,
        "opacity": 0.16,
        "refraction": 1.46,
    },
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def make_height(pattern: str, rng: np.random.Generator) -> np.ndarray:
    y, x = np.mgrid[0:SIZE, 0:SIZE].astype(np.float32)
    noise = rng.normal(0.0, 1.0, (SIZE, SIZE)).astype(np.float32)
    if pattern == "woven":
        warp = np.sin(x * np.pi / 2.15) * 0.48
        weft = np.sin(y * np.pi / 2.55) * 0.38
        diagonal = np.sin((x + y) * np.pi / 7.0) * 0.10
        return warp + weft + diagonal + noise * 0.20
    if pattern == "brushed":
        bands = np.sin(y * np.pi / 3.6) * 0.24
        streaks = rng.normal(0.0, 0.42, (SIZE, 1)).astype(np.float32)
        return bands + streaks + noise * 0.10
    return noise * 0.24


def normal_from_height(height: np.ndarray, strength: float) -> np.ndarray:
    dy, dx = np.gradient(height)
    nx = -dx * strength
    ny = -dy * strength
    nz = np.ones_like(nx)
    length = np.sqrt(nx * nx + ny * ny + nz * nz)
    normal = np.stack((nx / length, ny / length, nz / length), axis=-1)
    return np.clip((normal * 0.5 + 0.5) * 255.0, 0, 255).astype(np.uint8)


def base_color(
    base: tuple[int, int, int],
    variation: tuple[int, int, int],
    height: np.ndarray,
) -> np.ndarray:
    normalized = height / max(float(np.max(np.abs(height))), 1.0)
    base_array = np.asarray(base, dtype=np.float32)[None, None, :]
    variation_array = np.asarray(variation, dtype=np.float32)[None, None, :]
    color = base_array + normalized[:, :, None] * variation_array
    return np.clip(color, 0, 255).astype(np.uint8)


def orm_texture(
    roughness: int,
    metallic: int,
    height: np.ndarray,
) -> np.ndarray:
    normalized = height / max(float(np.max(np.abs(height))), 1.0)
    ao = np.clip(248.0 + normalized * 7.0, 0, 255)
    rough = np.clip(float(roughness) + normalized * 12.0, 0, 255)
    metal = np.full_like(rough, float(metallic))
    return np.stack((ao, rough, metal), axis=-1).astype(np.uint8)


def save_rgb(path: Path, array: np.ndarray) -> None:
    Image.fromarray(array, mode="RGB").save(
        path,
        format="PNG",
        compress_level=7,
        optimize=False,
    )


def main() -> None:
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    generated: list[Path] = []
    for index, (name, spec) in enumerate(SETS.items()):
        rng = np.random.default_rng(SEED + index * 101)
        height = make_height(spec["pattern"], rng)
        textures = {
            "BaseColor": base_color(spec["base"], spec["variation"], height),
            "Normal": normal_from_height(height, spec["normal_strength"]),
            "ORM": orm_texture(spec["roughness"], spec["metallic"], height),
        }
        for channel, array in textures.items():
            path = OUTPUT_DIR / f"T_{name}_{channel}_2K.png"
            save_rgb(path, array)
            generated.append(path)

    manifest = {
        "status": "production",
        "generator": Path(__file__).name,
        "resolution": [SIZE, SIZE],
        "packing": {
            "ORM": {
                "R": "AmbientOcclusion",
                "G": "Roughness",
                "B": "Metallic",
            }
        },
        "materials": MATERIALS,
        "files": [
            {
                "name": path.name,
                "bytes": path.stat().st_size,
                "sha256": sha256(path),
            }
            for path in sorted(generated)
        ],
    }
    manifest_path = OUTPUT_DIR / "AutomaticMahjongTableTextureManifest.json"
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    print(
        "AUTOMATIC_MAHJONG_TABLE_TEXTURES_OK",
        f"sets={len(SETS)}",
        f"textures={len(generated)}",
        f"resolution={SIZE}x{SIZE}",
        f"manifest={manifest_path}",
    )


if __name__ == "__main__":
    main()
