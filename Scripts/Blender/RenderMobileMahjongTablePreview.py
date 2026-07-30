# 渲染移动麻将桌全景和中心控制器审查图，不保存场景修改，输出用于人工视觉验证。
# 渲染失败时保留日志但不得发布不完整截图；颜色管理和相机参数必须保持确定性。
"""Render full-table and center-controller review images without saving scene."""

from __future__ import annotations

import argparse
import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector


def arguments() -> argparse.Namespace:
    argv = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output_dir", type=Path)
    return parser.parse_args(argv)


def point_at(obj: bpy.types.Object, target: Vector) -> None:
    obj.rotation_euler = (target - obj.location).to_track_quat("-Z", "Y").to_euler()


def add_area(
    name: str,
    location: tuple[float, float, float],
    energy: float,
    size: float,
    color: tuple[float, float, float],
) -> bpy.types.Object:
    data = bpy.data.lights.new(name, "AREA")
    data.energy = energy
    data.shape = "DISK"
    data.size = size
    data.color = color
    obj = bpy.data.objects.new(name, data)
    bpy.context.scene.collection.objects.link(obj)
    obj.location = location
    point_at(obj, Vector((0.0, 0.0, 0.0)))
    return obj


def render(
    camera: bpy.types.Object,
    location: tuple[float, float, float],
    target: tuple[float, float, float],
    lens: float,
    output: Path,
) -> None:
    camera.location = location
    camera.data.lens = lens
    point_at(camera, Vector(target))
    bpy.context.scene.render.filepath = str(output)
    bpy.ops.render.render(write_still=True)


def main() -> None:
    args = arguments()
    output_dir = args.output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    scene = bpy.context.scene
    try:
        scene.render.engine = "BLENDER_EEVEE_NEXT"
    except TypeError:
        scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x = 960
    scene.render.resolution_y = 720
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.film_transparent = False
    scene.render.image_settings.color_depth = "8"
    scene.world.color = (0.012, 0.012, 0.012)
    scene.view_settings.look = "AgX - Medium High Contrast"

    camera_data = bpy.data.cameras.new("ReviewCamera")
    camera = bpy.data.objects.new("ReviewCamera", camera_data)
    scene.collection.objects.link(camera)
    scene.camera = camera

    add_area(
        "ReviewKey",
        (-1.8, -2.0, 3.6),
        75.0,
        3.0,
        (1.0, 0.82, 0.58),
    )
    add_area(
        "ReviewFill",
        (2.4, -0.4, 2.2),
        38.0,
        2.2,
        (0.48, 0.68, 1.0),
    )
    add_area(
        "ReviewTop",
        (0.0, 0.0, 4.0),
        50.0,
        2.5,
        (1.0, 1.0, 1.0),
    )

    render(
        camera,
        (2.7, -2.7, 2.65),
        (0.0, 0.0, -0.01),
        54.0,
        output_dir / "MobileMahjongTable_Full.png",
    )
    render(
        camera,
        (0.0, -0.46, 0.48),
        (0.0, 0.0, 0.012),
        66.0,
        output_dir / "MobileMahjongTable_Controller.png",
    )
    print(
        "MOBILE_MAHJONG_TABLE_PREVIEW_OK",
        output_dir / "MobileMahjongTable_Full.png",
        output_dir / "MobileMahjongTable_Controller.png",
    )


if __name__ == "__main__":
    main()
