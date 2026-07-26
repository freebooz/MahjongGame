"""Display two Mahjong50 orientation-check tiles in Blender's material viewport."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import bpy


def parse_args() -> argparse.Namespace:
    argv = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--tile-a", default="Characters_5")
    parser.add_argument("--tile-b", default="Red_Dragon")
    return parser.parse_args(argv)


args = parse_args()
asset = bpy.data.objects.get("SM_Mahjong50")
face_material = bpy.data.materials.get("M_Mahjong50_TileUnified")
if asset is None or face_material is None or not face_material.use_nodes:
    raise RuntimeError("The opened file is not a valid Mahjong50 master model")

asset_root = Path(bpy.data.filepath).resolve().parent.parent
index_path = asset_root / "Textures" / "Mahjong50_FaceAtlas_Index.json"
index = json.loads(index_path.read_text(encoding="utf-8-sig"))
def find_tile(name: str) -> dict:
    tile = next((entry for entry in index["tiles"] if entry["name"] == name), None)
    if tile is None or tile.get("reserved", False):
        raise RuntimeError(f"Unknown or reserved Mahjong50 tile: {name}")
    return tile


def make_tile_material(tile: dict) -> bpy.types.Material:
    material = face_material.copy()
    material.name = f"M_Verify_{tile['name']}"
    mapping = material.node_tree.nodes.get("AtlasUV_Mapping")
    if mapping is None:
        raise RuntimeError("Unified material has no AtlasUV_Mapping node")
    offset_u = (160.0 + float(tile["column"]) * 896.0) / 8192.0
    offset_v = float(tile["row_from_bottom"]) * 1024.0 / 4096.0
    mapping.inputs["Location"].default_value = (offset_u, offset_v, 0.0)
    material["DefaultTile"] = tile["name"]
    material["DefaultTileColumn"] = int(tile["column"])
    material["DefaultTileRowFromBottom"] = int(tile["row_from_bottom"])
    return material


tile_a = find_tile(args.tile_a)
tile_b = find_tile(args.tile_b)

asset.name = f"Verify_{tile_a['name']}"
asset.location.x = -0.022
asset.data.materials[0] = make_tile_material(tile_a)

second_asset = asset.copy()
second_asset.data = asset.data.copy()
second_asset.name = f"Verify_{tile_b['name']}"
second_asset.location.x = 0.022
second_asset.data.materials[0] = make_tile_material(tile_b)
bpy.context.collection.objects.link(second_asset)

bpy.context.scene["InteractiveDisplayTiles"] = f"{tile_a['name']}, {tile_b['name']}"

bpy.ops.object.select_all(action="DESELECT")
asset.hide_set(False)
asset.select_set(True)
second_asset.hide_set(False)
second_asset.select_set(True)
bpy.context.view_layer.objects.active = second_asset

screen = bpy.context.screen
if screen is not None:
    for area in screen.areas:
        if area.type != "VIEW_3D":
            continue
        space = area.spaces.active
        space.shading.type = "MATERIAL"
        space.shading.use_scene_world = False
        space.overlay.show_outline_selected = True
        window_region = next(
            (region for region in area.regions if region.type == "WINDOW"),
            None,
        )
        if window_region is None:
            continue
        with bpy.context.temp_override(
            window=bpy.context.window,
            area=area,
            region=window_region,
            space_data=space,
        ):
            # The rebuilt master uses Blender's conventional -Y front and
            # source-unmirrored UVs. FRONT therefore shows readable glyphs
            # without any viewport-only compensation.
            bpy.ops.view3d.view_axis(type="FRONT", align_active=False)
            bpy.ops.view3d.view_selected(use_all_regions=False)
        # Deterministic two-tile framing; do not inherit a previously saved
        # viewport zoom or perspective state from the master .blend.
        space.region_3d.view_perspective = "ORTHO"
        space.region_3d.view_location = (0.0, 0.0, 0.025)
        space.region_3d.view_distance = 0.105

print(f"[Mahjong50Open] Displaying {tile_a['name']} + {tile_b['name']}")
