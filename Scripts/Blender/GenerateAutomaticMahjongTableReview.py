"""Create a non-production 1.5 m automatic Mahjong table review model.

The script is intentionally run against the current production Blender source
and writes every result to SourceArt/3D/MahjongTableReview. It never overwrites
the production blend, FBX, GLB, textures, or Unreal assets.

Usage:
    blender --background SourceArt/3D/MahjongTable/SM_StandardMahjongTable.blend \
      --python Scripts/Blender/GenerateAutomaticMahjongTableReview.py -- \
      H:/MahjongGame
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector


SCRIPT_VERSION = "2.0.0-radial-fan-sector-controller"
SOURCE_FRAME = "SM_Mahjong_Table_Frame_MiterJoint"
SOURCE_FELT = "Mahjong_Felt_Surface"
SOURCE_ROOT = "SM_StandardMahjongTable"
REVIEW_ROOT = "SM_AutomaticMahjongTable_Review"

OUTER_SIZE_M = 1.500
VISIBLE_FRAME_WIDTH_M = 0.090
PLAYING_OPENING_M = OUTER_SIZE_M - 2.0 * VISIBLE_FRAME_WIDTH_M
FELT_HIDDEN_OVERLAP_M = 0.0075
FELT_SIZE_M = PLAYING_OPENING_M + 2.0 * FELT_HIDDEN_OVERLAP_M
CENTER_DISC_DIAMETER_M = 0.340
FRAME_OUTER_CORNER_RADIUS_M = 0.055
FRAME_INNER_CORNER_RADIUS_M = 0.016
FRAME_GOLD_INLAY_OFFSET_M = 0.019
FRAME_GOLD_INLAY_WIDTH_M = 0.006


def arguments() -> argparse.Namespace:
    argv = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project_root", type=Path)
    parser.add_argument("--output-dir", type=Path)
    return parser.parse_args(argv)


def set_principled(material: bpy.types.Material, **values) -> None:
    shader = next(
        node
        for node in material.node_tree.nodes
        if node.type == "BSDF_PRINCIPLED"
    )
    for name, value in values.items():
        socket = shader.inputs.get(name)
        if socket is not None:
            socket.default_value = value


def solid_material(
    name: str,
    color: tuple[float, float, float, float],
    *,
    metallic: float,
    roughness: float,
    coat: float = 0.0,
    emission: tuple[float, float, float, float] | None = None,
    emission_strength: float = 0.0,
) -> bpy.types.Material:
    material = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    material.use_nodes = True
    material.diffuse_color = color
    set_principled(
        material,
        **{
            "Base Color": color,
            "Metallic": metallic,
            "Roughness": roughness,
            "Coat Weight": coat,
            "Coat Roughness": 0.18,
        },
    )
    if emission is not None:
        set_principled(
            material,
            **{
                "Emission Color": emission,
                "Emission Strength": emission_strength,
            },
        )
    material["review_material"] = True
    return material


def node_by_type(
    material: bpy.types.Material, node_type: str
) -> bpy.types.Node | None:
    return next(
        (node for node in material.node_tree.nodes if node.type == node_type),
        None,
    )


def enhance_existing_table_materials() -> None:
    wood = bpy.data.materials.get("M_Table_Walnut_PBR")
    felt = bpy.data.materials.get("M_Table_Felt_Green_PBR")
    if wood is None or felt is None:
        raise RuntimeError("Missing source wood or felt material")

    wood_shader = node_by_type(wood, "BSDF_PRINCIPLED")
    wood_ramp = node_by_type(wood, "VALTORGB")
    wood_noise = node_by_type(wood, "TEX_NOISE")
    wood_bump = node_by_type(wood, "BUMP")
    if None in (wood_shader, wood_ramp, wood_noise, wood_bump):
        raise RuntimeError("Unexpected source walnut material graph")
    set_principled(
        wood,
        **{
            "Metallic": 0.78,
            "Roughness": 0.19,
            "Coat Weight": 0.34,
            "Coat Roughness": 0.14,
            "Anisotropic IOR Level": 0.44,
            "Anisotropic Rotation": 0.12,
        },
    )
    wood_noise.inputs["Scale"].default_value = 210.0
    wood_noise.inputs["Detail"].default_value = 2.6
    wood_noise.inputs["Roughness"].default_value = 0.38
    wood_bump.inputs["Strength"].default_value = 0.018
    wood_bump.inputs["Distance"].default_value = 0.00006
    wood_colors = (
        (0.0006, 0.0004, 0.00025, 1.0),
        (0.0025, 0.0016, 0.0009, 1.0),
        (0.0120, 0.0070, 0.0035, 1.0),
    )
    for element, color in zip(wood_ramp.color_ramp.elements, wood_colors):
        element.color = color
    wood.name = "M_Table_Frame_BlackAlloy"
    wood["review_finish"] = "glossy_black_bronze_alloy_with_subtle_brushed_texture"

    felt_shader = node_by_type(felt, "BSDF_PRINCIPLED")
    felt_ramp = node_by_type(felt, "VALTORGB")
    felt_noise = node_by_type(felt, "TEX_NOISE")
    felt_bump = node_by_type(felt, "BUMP")
    felt_map = node_by_type(felt, "MAP_RANGE")
    if None in (felt_shader, felt_ramp, felt_noise, felt_bump, felt_map):
        raise RuntimeError("Unexpected source felt material graph")
    set_principled(
        felt,
        **{
            "Sheen Weight": 0.032,
            "IOR": 1.42,
        },
    )
    felt_noise.inputs["Scale"].default_value = 285.0
    felt_noise.inputs["Detail"].default_value = 3.8
    felt_noise.inputs["Roughness"].default_value = 0.72
    felt_bump.inputs["Strength"].default_value = 0.105
    felt_bump.inputs["Distance"].default_value = 0.00028
    felt_map.inputs["To Min"].default_value = 0.84
    felt_map.inputs["To Max"].default_value = 0.96
    felt_ramp.color_ramp.elements[0].color = (0.0006, 0.0035, 0.0020, 1.0)
    felt_ramp.color_ramp.elements[-1].color = (0.0024, 0.0155, 0.0085, 1.0)
    felt["review_finish"] = "reference_deep_cool_forest_green_microfiber"


def add_micro_surface(
    material: bpy.types.Material,
    *,
    scale: float,
    detail: float,
    roughness_min: float,
    roughness_max: float,
    bump_strength: float,
    bump_distance: float,
) -> None:
    nodes = material.node_tree.nodes
    links = material.node_tree.links
    shader = node_by_type(material, "BSDF_PRINCIPLED")
    if shader is None:
        raise RuntimeError(f"Missing Principled shader in {material.name}")

    texcoord = nodes.new("ShaderNodeTexCoord")
    texcoord.name = "ReviewMicroSurfaceCoordinates"
    texcoord.location = (-700.0, -120.0)
    noise = nodes.new("ShaderNodeTexNoise")
    noise.name = "ReviewMicroSurfaceNoise"
    noise.location = (-480.0, -120.0)
    noise.inputs["Scale"].default_value = scale
    noise.inputs["Detail"].default_value = detail
    noise.inputs["Roughness"].default_value = 0.62
    mapping = nodes.new("ShaderNodeMapRange")
    mapping.name = "ReviewMicroRoughness"
    mapping.location = (-230.0, 70.0)
    mapping.inputs["To Min"].default_value = roughness_min
    mapping.inputs["To Max"].default_value = roughness_max
    mapping.clamp = True
    bump = nodes.new("ShaderNodeBump")
    bump.name = "ReviewMicroBump"
    bump.location = (-210.0, -150.0)
    bump.inputs["Strength"].default_value = bump_strength
    bump.inputs["Distance"].default_value = bump_distance
    links.new(texcoord.outputs["Generated"], noise.inputs["Vector"])
    links.new(noise.outputs["Fac"], mapping.inputs["Value"])
    links.new(mapping.outputs["Result"], shader.inputs["Roughness"])
    links.new(noise.outputs["Fac"], bump.inputs["Height"])
    links.new(bump.outputs["Normal"], shader.inputs["Normal"])


def configure_review_lighting() -> None:
    energy_by_name = {
        "Key_Softbox": 330.0,
        "Fill_Softbox": 95.0,
        "Rim_Softbox": 135.0,
    }
    for name, energy in energy_by_name.items():
        light = bpy.data.objects.get(name)
        if light is not None and light.type == "LIGHT":
            light.data.energy = energy

    world = bpy.context.scene.world
    if world is not None:
        world.use_nodes = True
        background = node_by_type(world, "BACKGROUND")
        if background is not None:
            background.inputs["Color"].default_value = (
                0.006,
                0.009,
                0.007,
                1.0,
            )
            background.inputs["Strength"].default_value = 0.16


def move_to_collection(
    obj: bpy.types.Object, collection: bpy.types.Collection
) -> None:
    for owner in list(obj.users_collection):
        owner.objects.unlink(obj)
    collection.objects.link(obj)


def recalculate_normals(obj: bpy.types.Object) -> None:
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    try:
        bpy.ops.mesh.normals_make_consistent(inside=False)
    except AttributeError:
        pass
    bpy.ops.object.mode_set(mode="OBJECT")
    obj.select_set(False)


def smooth_beveled(obj: bpy.types.Object, width: float, segments: int = 3) -> None:
    bevel = obj.modifiers.new("ReviewEdgeSoftening", "BEVEL")
    bevel.width = width
    bevel.segments = segments
    bevel.limit_method = "ANGLE"
    bevel.angle_limit = math.radians(18.0)
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.modifier_apply(modifier=bevel.name)
    for polygon in obj.data.polygons:
        polygon.use_smooth = True
    obj.select_set(False)


def remap_coordinate(
    value: float,
    old_inner: float,
    old_outer: float,
    new_inner: float,
    new_outer: float,
) -> float:
    sign = -1.0 if value < 0.0 else 1.0
    magnitude = abs(value)
    if magnitude <= old_inner:
        mapped = magnitude * new_inner / old_inner
    else:
        alpha = min(1.0, (magnitude - old_inner) / (old_outer - old_inner))
        mapped = new_inner + alpha * (new_outer - new_inner)
    return sign * mapped


def resize_table(frame: bpy.types.Object, felt: bpy.types.Object) -> None:
    old_outer = max(frame.dimensions.x, frame.dimensions.y) * 0.5
    old_inner = min(
        max(abs(vertex.co.x), abs(vertex.co.y))
        for vertex in frame.data.vertices
    )
    new_outer = OUTER_SIZE_M * 0.5
    new_inner = PLAYING_OPENING_M * 0.5
    if not 0.40 < old_inner < old_outer < 0.70:
        raise RuntimeError(
            f"Unexpected source frame radii: inner={old_inner} outer={old_outer}"
        )

    for vertex in frame.data.vertices:
        vertex.co.x = remap_coordinate(
            vertex.co.x, old_inner, old_outer, new_inner, new_outer
        )
        vertex.co.y = remap_coordinate(
            vertex.co.y, old_inner, old_outer, new_inner, new_outer
        )
    frame.data.update()
    recalculate_normals(frame)

    felt_scale_x = FELT_SIZE_M / felt.dimensions.x
    felt_scale_y = FELT_SIZE_M / felt.dimensions.y
    for vertex in felt.data.vertices:
        vertex.co.x *= felt_scale_x
        vertex.co.y *= felt_scale_y
    felt.data.update()
    recalculate_normals(felt)

    frame["outer_dimensions_mm"] = "1500x1500"
    frame["visible_frame_width_mm"] = round(VISIBLE_FRAME_WIDTH_M * 1000.0, 3)
    frame["review_variant"] = "automatic_machine_narrow_frame"
    felt["finished_size_mm"] = f"{FELT_SIZE_M * 1000.0:.1f}x{FELT_SIZE_M * 1000.0:.1f}"
    felt["visible_opening_mm"] = (
        f"{PLAYING_OPENING_M * 1000.0:.1f}x"
        f"{PLAYING_OPENING_M * 1000.0:.1f}"
    )
    felt["hidden_overlap_each_side_mm"] = FELT_HIDDEN_OVERLAP_M * 1000.0


def rounded_rectangle_points(
    half_size: float,
    radius: float,
    z: float,
    *,
    corner_segments: int = 16,
) -> list[tuple[float, float, float]]:
    if radius <= 0.0 or radius >= half_size:
        raise ValueError(f"Invalid rounded rectangle radius {radius}")
    points: list[tuple[float, float, float]] = []
    corners = (
        (half_size - radius, half_size - radius, 0.0),
        (-half_size + radius, half_size - radius, 90.0),
        (-half_size + radius, -half_size + radius, 180.0),
        (half_size - radius, -half_size + radius, 270.0),
    )
    for center_x, center_y, start_degrees in corners:
        for step in range(corner_segments):
            angle = math.radians(start_degrees + 90.0 * step / corner_segments)
            points.append(
                (
                    center_x + radius * math.cos(angle),
                    center_y + radius * math.sin(angle),
                    z,
                )
            )
    return points


def closed_surface_between_loops(
    loop_count: int,
    points_per_loop: int,
) -> list[tuple[int, int, int, int]]:
    faces: list[tuple[int, int, int, int]] = []
    for loop_index in range(loop_count):
        next_loop = (loop_index + 1) % loop_count
        for index in range(points_per_loop):
            following = (index + 1) % points_per_loop
            faces.append(
                (
                    loop_index * points_per_loop + index,
                    loop_index * points_per_loop + following,
                    next_loop * points_per_loop + following,
                    next_loop * points_per_loop + index,
                )
            )
    return faces


def replace_frame_with_rounded_geometry(frame: bpy.types.Object) -> None:
    outer_half = OUTER_SIZE_M * 0.5
    inner_half = PLAYING_OPENING_M * 0.5
    loop_specs = (
        (outer_half, FRAME_OUTER_CORNER_RADIUS_M, -0.060),
        (outer_half, FRAME_OUTER_CORNER_RADIUS_M, -0.012),
        (outer_half - 0.006, FRAME_OUTER_CORNER_RADIUS_M - 0.005, 0.002),
        (outer_half - 0.028, 0.035, 0.010),
        (inner_half + 0.011, 0.022, 0.010),
        (inner_half, FRAME_INNER_CORNER_RADIUS_M, 0.002),
        (inner_half, FRAME_INNER_CORNER_RADIUS_M, -0.012),
    )
    loops = [
        rounded_rectangle_points(half_size, radius, z)
        for half_size, radius, z in loop_specs
    ]
    vertices = [point for loop in loops for point in loop]
    faces = closed_surface_between_loops(len(loops), len(loops[0]))
    mesh = bpy.data.meshes.new("SM_Mahjong_Table_Frame_RoundedAlloy_Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()

    old_mesh = frame.data
    old_materials = list(old_mesh.materials)
    frame.data = mesh
    for material in old_materials:
        frame.data.materials.append(material)
    frame.name = "SM_Mahjong_Table_Frame_RoundedAlloy"
    frame["corner_style"] = "rounded_reference_smaller_radius"
    frame["outer_corner_radius_mm"] = FRAME_OUTER_CORNER_RADIUS_M * 1000.0
    frame["inner_corner_radius_mm"] = FRAME_INNER_CORNER_RADIUS_M * 1000.0
    recalculate_normals(frame)
    smooth_beveled(frame, 0.0014, 3)
    if old_mesh.users == 0:
        bpy.data.meshes.remove(old_mesh)


def add_cylinder(
    name: str,
    radius: float,
    depth: float,
    z_center: float,
    material: bpy.types.Material,
    collection: bpy.types.Collection,
    *,
    vertices: int = 96,
    bevel: float = 0.0015,
) -> bpy.types.Object:
    bpy.ops.mesh.primitive_cylinder_add(
        vertices=vertices,
        radius=radius,
        depth=depth,
        location=(0.0, 0.0, z_center),
    )
    obj = bpy.context.object
    obj.name = name
    move_to_collection(obj, collection)
    obj.data.materials.append(material)
    smooth_beveled(obj, bevel, 3)
    return obj


def add_box(
    name: str,
    location: tuple[float, float, float],
    dimensions: tuple[float, float, float],
    rotation_z: float,
    material: bpy.types.Material,
    collection: bpy.types.Collection,
    bevel: float,
) -> bpy.types.Object:
    bpy.ops.mesh.primitive_cube_add(location=location)
    obj = bpy.context.object
    obj.name = name
    obj.dimensions = dimensions
    obj.rotation_euler.z = rotation_z
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    move_to_collection(obj, collection)
    obj.data.materials.append(material)
    smooth_beveled(obj, bevel, 3)
    return obj


def create_frame_gold_inlay(
    collection: bpy.types.Collection,
    root: bpy.types.Object,
) -> list[bpy.types.Object]:
    gold = solid_material(
        "M_Table_Frame_GoldInlay",
        (0.78, 0.31, 0.035, 1.0),
        metallic=0.94,
        roughness=0.16,
        coat=0.22,
    )
    set_principled(
        gold,
        **{
            "Anisotropic IOR Level": 0.42,
            "Anisotropic Rotation": 0.08,
        },
    )

    inner_half = PLAYING_OPENING_M * 0.5
    center = inner_half + FRAME_GOLD_INLAY_OFFSET_M
    center_radius = FRAME_INNER_CORNER_RADIUS_M + FRAME_GOLD_INLAY_OFFSET_M
    half_width = FRAME_GOLD_INLAY_WIDTH_M * 0.5
    loops = (
        rounded_rectangle_points(
            center + half_width,
            center_radius + half_width,
            0.0097,
        ),
        rounded_rectangle_points(
            center + half_width,
            center_radius + half_width,
            0.0110,
        ),
        rounded_rectangle_points(
            center - half_width,
            center_radius - half_width,
            0.0110,
        ),
        rounded_rectangle_points(
            center - half_width,
            center_radius - half_width,
            0.0097,
        ),
    )
    vertices = [point for loop in loops for point in loop]
    faces = closed_surface_between_loops(len(loops), len(loops[0]))
    mesh = bpy.data.meshes.new("Frame_GoldInlay_RoundedContinuous_Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new("Frame_GoldInlay_RoundedContinuous", mesh)
    collection.objects.link(obj)
    obj.data.materials.append(gold)
    obj.parent = root
    obj["asset_role"] = "frame_gold_inlay"
    obj["inlay_width_mm"] = FRAME_GOLD_INLAY_WIDTH_M * 1000.0
    obj["corner_style"] = "continuous_rounded_inlay"
    recalculate_normals(obj)
    smooth_beveled(obj, 0.00045, 3)
    return [obj]


def add_direction_text(
    character: str,
    name: str,
    location: tuple[float, float, float],
    rotation_z: float,
    font: bpy.types.VectorFont,
    material: bpy.types.Material,
    collection: bpy.types.Collection,
    *,
    size: float = 0.050,
    extrusion: float = 0.0010,
    bevel: float = 0.00045,
) -> bpy.types.Object:
    bpy.ops.object.text_add(location=location, rotation=(0.0, 0.0, rotation_z))
    obj = bpy.context.object
    obj.name = name
    obj.data.body = character
    obj.data.align_x = "CENTER"
    obj.data.align_y = "CENTER"
    obj.data.size = size
    obj.data.extrude = extrusion
    obj.data.bevel_depth = bevel
    obj.data.bevel_resolution = 3
    obj.data.fill_mode = "BOTH"
    obj.data.font = font
    obj.data.materials.append(material)
    move_to_collection(obj, collection)
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.convert(target="MESH")
    obj = bpy.context.object
    recalculate_normals(obj)
    return obj


def add_seven_segment_digit(
    digit: str,
    center_x: float,
    center_y: float,
    z: float,
    material: bpy.types.Material,
    collection: bpy.types.Collection,
    *,
    width: float = 0.024,
    height: float = 0.042,
    thickness: float = 0.0042,
) -> list[bpy.types.Object]:
    enabled_by_digit = {
        "0": {"a", "b", "c", "d", "e", "f"},
        "8": {"a", "b", "c", "d", "e", "f", "g"},
    }
    enabled = enabled_by_digit[digit]
    segment_depth = 0.0018
    horizontal = {
        "a": (center_x, center_y + height * 0.5),
        "g": (center_x, center_y),
        "d": (center_x, center_y - height * 0.5),
    }
    vertical = {
        "f": (center_x - width * 0.5, center_y + height * 0.25),
        "b": (center_x + width * 0.5, center_y + height * 0.25),
        "e": (center_x - width * 0.5, center_y - height * 0.25),
        "c": (center_x + width * 0.5, center_y - height * 0.25),
    }
    objects: list[bpy.types.Object] = []
    for segment, location in horizontal.items():
        if segment in enabled:
            objects.append(
                add_box(
                    f"CenterDisplay_{digit}_{center_x:+.3f}_{segment}",
                    (location[0], location[1], z),
                    (width, thickness, segment_depth),
                    0.0,
                    material,
                    collection,
                    thickness * 0.35,
                )
            )
    for segment, location in vertical.items():
        if segment in enabled:
            objects.append(
                add_box(
                    f"CenterDisplay_{digit}_{center_x:+.3f}_{segment}",
                    (location[0], location[1], z),
                    (thickness, height * 0.46, segment_depth),
                    0.0,
                    material,
                    collection,
                    thickness * 0.35,
                )
            )
    return objects


def add_indicator_triangle(
    name: str,
    center: tuple[float, float],
    size: float,
    z: float,
    material: bpy.types.Material,
    collection: bpy.types.Collection,
) -> bpy.types.Object:
    cx, cy = center
    vertices = (
        (cx, cy - size * 0.62, z),
        (cx - size * 0.55, cy + size * 0.38, z),
        (cx + size * 0.55, cy + size * 0.38, z),
    )
    mesh = bpy.data.meshes.new(f"{name}_Mesh")
    mesh.from_pydata(vertices, [], [(0, 1, 2)])
    mesh.materials.append(material)
    mesh.update(calc_edges=True)
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    return obj


def create_center_controller(
    collection: bpy.types.Collection,
    root: bpy.types.Object,
) -> list[bpy.types.Object]:
    brushed_metal = solid_material(
        "M_Table_CenterDisc_BrushedMetal",
        (0.22, 0.25, 0.25, 1.0),
        metallic=0.82,
        roughness=0.22,
        coat=0.20,
    )
    dark_panel = solid_material(
        "M_Table_CenterDisc_DarkGreenPanel",
        (0.008, 0.045, 0.025, 1.0),
        metallic=0.08,
        roughness=0.26,
        coat=0.16,
    )
    ivory = solid_material(
        "M_Table_DirectionLabel_Ivory",
        (0.90, 0.82, 0.56, 1.0),
        metallic=0.18,
        roughness=0.25,
        coat=0.12,
    )
    gold = solid_material(
        "M_Table_CenterButton_Gold",
        (0.45, 0.19, 0.035, 1.0),
        metallic=0.75,
        roughness=0.21,
        coat=0.15,
    )
    set_principled(
        brushed_metal,
        **{
            "Anisotropic IOR Level": 0.55,
            "Anisotropic Rotation": 0.18,
        },
    )
    add_micro_surface(
        brushed_metal,
        scale=420.0,
        detail=2.2,
        roughness_min=0.17,
        roughness_max=0.32,
        bump_strength=0.11,
        bump_distance=0.00010,
    )
    add_micro_surface(
        dark_panel,
        scale=135.0,
        detail=4.0,
        roughness_min=0.27,
        roughness_max=0.44,
        bump_strength=0.055,
        bump_distance=0.00014,
    )
    add_micro_surface(
        gold,
        scale=260.0,
        detail=2.0,
        roughness_min=0.16,
        roughness_max=0.28,
        bump_strength=0.045,
        bump_distance=0.00008,
    )
    led_colors = (
        ("North", (0.08, 0.45, 1.0, 1.0)),
        ("East", (0.95, 0.16, 0.06, 1.0)),
        ("South", (0.14, 0.95, 0.30, 1.0)),
        ("West", (1.0, 0.58, 0.05, 1.0)),
    )

    objects: list[bpy.types.Object] = []
    objects.append(
        add_cylinder(
            "CenterDisc_MetalRing",
            CENTER_DISC_DIAMETER_M * 0.5,
            0.010,
            0.0052,
            brushed_metal,
            collection,
        )
    )
    objects.append(
        add_cylinder(
            "CenterDisc_DarkPanel",
            0.145,
            0.006,
            0.0108,
            dark_panel,
            collection,
            bevel=0.0010,
        )
    )
    objects.append(
        add_cylinder(
            "CenterDisc_CenterButton",
            0.035,
            0.008,
            0.0160,
            gold,
            collection,
            vertices=64,
            bevel=0.0025,
        )
    )

    # Four subtle radial separators make the controller read like an automatic
    # Mahjong machine rather than a decorative medallion.
    for index in range(4):
        angle = math.radians(45.0 + index * 90.0)
        radius = 0.150
        objects.append(
            add_box(
                f"CenterDisc_Separator_{index + 1}",
                (
                    math.cos(angle) * radius,
                    math.sin(angle) * radius,
                    0.0113,
                ),
                (0.006, 0.030, 0.0022),
                angle,
                dark_panel,
                collection,
                0.001,
            )
        )

    font_path = Path("C:/Windows/Fonts/msyhbd.ttc")
    if not font_path.is_file():
        raise FileNotFoundError(f"Missing Chinese review font: {font_path}")
    font = bpy.data.fonts.load(str(font_path))
    directions = (
        ("北", "North", (0.0, 0.094, 0.0142), 0.0),
        ("东", "East", (0.094, 0.0, 0.0142), -math.pi * 0.5),
        ("南", "South", (0.0, -0.094, 0.0142), math.pi),
        ("西", "West", (-0.094, 0.0, 0.0142), math.pi * 0.5),
    )
    for character, english, location, rotation in directions:
        objects.append(
            add_direction_text(
                character,
                f"CenterDisc_Label_{english}",
                location,
                rotation,
                font,
                ivory,
                collection,
            )
        )

    for index, (english, color) in enumerate(led_colors):
        angle = math.radians(45.0 + index * 90.0)
        material = solid_material(
            f"M_Table_Indicator_{english}",
            color,
            metallic=0.05,
            roughness=0.20,
            emission=color,
            emission_strength=2.2,
        )
        bpy.ops.mesh.primitive_uv_sphere_add(
            segments=32,
            ring_count=16,
            location=(
                math.cos(angle) * 0.121,
                math.sin(angle) * 0.121,
                0.0150,
            ),
            scale=(0.0075, 0.0075, 0.0035),
        )
        led = bpy.context.object
        led.name = f"CenterDisc_LED_{english}"
        move_to_collection(led, collection)
        led.data.materials.append(material)
        for polygon in led.data.polygons:
            polygon.use_smooth = True
        objects.append(led)

    for obj in objects:
        obj.parent = root
        obj["asset_role"] = "automatic_mahjong_center_controller"
    objects[0]["diameter_mm"] = CENTER_DISC_DIAMETER_M * 1000.0
    objects[0]["direction_labels"] = "北,东,南,西"
    return objects


def create_reference_center_controller(
    collection: bpy.types.Collection,
    root: bpy.types.Object,
) -> list[bpy.types.Object]:
    gunmetal = solid_material(
        "M_Table_Controller_Gunmetal",
        (0.006, 0.007, 0.008, 1.0),
        metallic=0.78,
        roughness=0.24,
        coat=0.24,
    )
    black_panel = solid_material(
        "M_Table_Controller_BlackPanel",
        (0.002, 0.003, 0.0035, 1.0),
        metallic=0.28,
        roughness=0.18,
        coat=0.28,
    )
    gold = solid_material(
        "M_Table_Controller_GoldLabel",
        (0.72, 0.33, 0.055, 1.0),
        metallic=0.62,
        roughness=0.24,
        coat=0.18,
    )
    direction_gold = solid_material(
        "M_Table_Controller_DirectionGold",
        (0.95, 0.50, 0.075, 1.0),
        metallic=0.66,
        roughness=0.20,
        coat=0.22,
        emission=(0.62, 0.22, 0.015, 1.0),
        emission_strength=0.72,
    )
    divider_gold = solid_material(
        "M_Table_Controller_SectorDividerGold",
        (0.32, 0.12, 0.012, 1.0),
        metallic=0.82,
        roughness=0.22,
        coat=0.18,
    )
    glass = solid_material(
        "M_Table_Controller_TransparentGlass",
        (0.82, 0.92, 0.96, 0.08),
        metallic=0.0,
        roughness=0.012,
        coat=0.24,
    )
    set_principled(
        glass,
        **{
            "Transmission Weight": 0.985,
            "IOR": 1.46,
            "Alpha": 0.08,
            "Coat Weight": 0.26,
            "Coat Roughness": 0.018,
        },
    )
    try:
        glass.surface_render_method = "BLENDED"
    except (AttributeError, TypeError):
        pass

    set_principled(
        gunmetal,
        **{
            "Anisotropic IOR Level": 0.62,
            "Anisotropic Rotation": 0.12,
        },
    )
    add_micro_surface(
        gunmetal,
        scale=460.0,
        detail=2.0,
        roughness_min=0.16,
        roughness_max=0.25,
        bump_strength=0.020,
        bump_distance=0.000025,
    )
    objects: list[bpy.types.Object] = []
    outer_ring = add_cylinder(
        "Controller_OuterGunmetalRing",
        CENTER_DISC_DIAMETER_M * 0.5,
        0.010,
        0.0052,
        gunmetal,
        collection,
        bevel=0.0020,
    )
    objects.append(outer_ring)
    objects.append(
        add_cylinder(
            "Controller_GoldEdgeTrim",
            0.165,
            0.0025,
            0.0110,
            gold,
            collection,
            bevel=0.0012,
        )
    )
    objects.append(
        add_cylinder(
            "Controller_FourSectorPanel",
            0.158,
            0.0040,
            0.0135,
            black_panel,
            collection,
            bevel=0.0010,
        )
    )

    # Four diagonal radial dividers run from the central black bezel to the
    # outer panel edge. These boundaries place 北、东、南、西 inside four
    # distinct fan-shaped sectors, matching the supplied controller reference.
    divider_start_radius = 0.0615
    divider_end_radius = 0.1545
    divider_center_radius = (divider_start_radius + divider_end_radius) * 0.5
    divider_length = divider_end_radius - divider_start_radius
    divider_specs = (
        ("NE", 45.0),
        ("NW", 135.0),
        ("SW", 225.0),
        ("SE", 315.0),
    )
    for sector_name, angle_degrees in divider_specs:
        angle = math.radians(angle_degrees)
        objects.append(
            add_box(
                f"Controller_SectorDivider_{sector_name}",
                (
                    math.cos(angle) * divider_center_radius,
                    math.sin(angle) * divider_center_radius,
                    0.0160,
                ),
                (divider_length, 0.0015, 0.0013),
                angle,
                divider_gold,
                collection,
                0.00050,
            )
        )

    objects.append(
        add_cylinder(
            "Controller_DisplayBlackBezel",
            0.059,
            0.0040,
            0.0175,
            gunmetal,
            collection,
            vertices=72,
            bevel=0.0018,
        )
    )
    objects.append(
        add_cylinder(
            "Controller_DisplayBlackGlass",
            0.051,
            0.0030,
            0.0203,
            black_panel,
            collection,
            vertices=72,
            bevel=0.0012,
        )
    )

    font_path = Path("C:/Windows/Fonts/msyhbd.ttc")
    if not font_path.is_file():
        raise FileNotFoundError(f"Missing Chinese review font: {font_path}")
    font = bpy.data.fonts.load(str(font_path))
    labels = (
        ("北", "North", (0.0, 0.119, 0.0165), 0.0),
        ("东", "East", (0.120, 0.005, 0.0165), 0.0),
        ("南", "South", (0.0, -0.111, 0.0165), 0.0),
        ("西", "West", (-0.120, 0.005, 0.0165), 0.0),
    )
    for character, english, location, rotation in labels:
        objects.append(
            add_direction_text(
                character,
                f"Controller_Label_{english}",
                location,
                rotation,
                font,
                direction_gold,
                collection,
                size=0.034,
                extrusion=0.00075,
                bevel=0.00030,
            )
        )

    objects.append(
        add_indicator_triangle(
            "Controller_SouthIndicator",
            (0.0, -0.159),
            0.012,
            0.0175,
            gold,
            collection,
        )
    )

    # The cover is modeled as real thin glass above every display element,
    # with transmission, IOR and micro-surface roughness rather than a flat
    # alpha decal.
    objects.append(
        add_cylinder(
            "Controller_TransparentGlassCover",
            0.166,
            0.0030,
            0.0260,
            glass,
            collection,
            vertices=128,
            bevel=0.0013,
        )
    )

    for obj in objects:
        obj.parent = root
        obj["asset_role"] = "automatic_mahjong_reference_controller"
    outer_ring["diameter_mm"] = CENTER_DISC_DIAMETER_M * 1000.0
    outer_ring["direction_labels"] = "北,东,南,西"
    outer_ring["display"] = "none"
    outer_ring["visible_text"] = "directions_only"
    outer_ring["sector_layout"] = "four_direction_radial_fan"
    outer_ring["glass_cover"] = "smooth_transparent_ior_1.46"
    return objects


def point_at(obj: bpy.types.Object, target: Vector) -> None:
    obj.rotation_euler = (target - obj.location).to_track_quat("-Z", "Y").to_euler()


def configure_render() -> None:
    scene = bpy.context.scene
    try:
        scene.render.engine = "BLENDER_EEVEE_NEXT"
    except TypeError:
        scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.image_settings.color_depth = "8"
    scene.render.film_transparent = False
    scene.view_settings.view_transform = "AgX"
    scene.view_settings.look = "AgX - Medium High Contrast"
    scene.view_settings.exposure = 0.16


def render_previews(output_dir: Path) -> list[Path]:
    scene = bpy.context.scene
    configure_render()
    camera = bpy.data.objects.get("Preview_Camera")
    if camera is None or camera.type != "CAMERA":
        raise RuntimeError("Missing source Preview_Camera")

    camera.location = (2.55, -2.72, 1.16)
    camera.data.lens = 58.0
    point_at(camera, Vector((0.0, 0.0, -0.005)))
    scene.camera = camera
    scene.render.resolution_x = 1600
    scene.render.resolution_y = 1000
    three_quarter = output_dir / "AutomaticMahjongTable_Review_ThreeQuarter.png"
    scene.render.filepath = str(three_quarter)
    bpy.ops.render.render(write_still=True)

    top_data = bpy.data.cameras.new("Review_Top_Camera")
    top_camera = bpy.data.objects.new("Review_Top_Camera", top_data)
    scene.collection.objects.link(top_camera)
    top_camera.location = (0.0, 0.0, 2.65)
    top_data.lens = 52.0
    point_at(top_camera, Vector((0.0, 0.0, 0.0)))
    scene.camera = top_camera
    scene.render.resolution_x = 1400
    scene.render.resolution_y = 1400
    top = output_dir / "AutomaticMahjongTable_Review_Top.png"
    scene.render.filepath = str(top)
    bpy.ops.render.render(write_still=True)
    scene.camera = camera
    return [three_quarter, top]


def model_meshes(root: bpy.types.Object) -> list[bpy.types.Object]:
    return sorted(
        [
            obj
            for obj in bpy.data.objects
            if obj.type == "MESH" and (obj.parent == root or obj.parent == root.parent)
        ],
        key=lambda obj: obj.name,
    )


def select_meshes(objects: list[bpy.types.Object]) -> None:
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]


def export_review(output_dir: Path, objects: list[bpy.types.Object]) -> list[Path]:
    fbx = output_dir / f"{REVIEW_ROOT}.fbx"
    glb = output_dir / f"{REVIEW_ROOT}.glb"
    select_meshes(objects)
    bpy.ops.export_scene.fbx(
        filepath=str(fbx),
        use_selection=True,
        object_types={"MESH"},
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        use_space_transform=True,
        bake_space_transform=False,
        axis_forward="-Z",
        axis_up="Y",
        mesh_smooth_type="FACE",
        use_mesh_modifiers=True,
        use_tspace=True,
        add_leaf_bones=False,
        path_mode="AUTO",
        embed_textures=False,
    )
    select_meshes(objects)
    bpy.ops.export_scene.gltf(
        filepath=str(glb),
        use_selection=True,
        export_format="GLB",
        export_apply=True,
        export_materials="EXPORT",
        export_yup=True,
    )
    return [fbx, glb]


def bounds(objects: list[bpy.types.Object]) -> tuple[Vector, Vector]:
    points = [
        obj.matrix_world @ vertex.co
        for obj in objects
        for vertex in obj.data.vertices
    ]
    minimum = Vector(
        tuple(min(point[index] for point in points) for index in range(3))
    )
    maximum = Vector(
        tuple(max(point[index] for point in points) for index in range(3))
    )
    return minimum, maximum


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_manifest(
    output_dir: Path,
    objects: list[bpy.types.Object],
    files: list[Path],
) -> Path:
    minimum, maximum = bounds(objects)
    size = maximum - minimum
    manifest_path = output_dir / "AutomaticMahjongTable_ReviewManifest.json"
    manifest = {
        "status": "awaiting_human_review_not_for_unreal_import",
        "generator": Path(__file__).name,
        "generator_version": SCRIPT_VERSION,
        "blender_version": bpy.app.version_string,
        "source_blend": "SourceArt/3D/MahjongTable/SM_StandardMahjongTable.blend",
        "design": {
            "style": "premium_automatic_mahjong_machine",
            "outer_size_mm": [1500.0, 1500.0],
            "visible_frame_width_mm": VISIBLE_FRAME_WIDTH_M * 1000.0,
            "frame_outer_corner_radius_mm": FRAME_OUTER_CORNER_RADIUS_M * 1000.0,
            "frame_inner_corner_radius_mm": FRAME_INNER_CORNER_RADIUS_M * 1000.0,
            "frame_gold_inlay_width_mm": FRAME_GOLD_INLAY_WIDTH_M * 1000.0,
            "playing_opening_mm": [
                PLAYING_OPENING_M * 1000.0,
                PLAYING_OPENING_M * 1000.0,
            ],
            "felt_hidden_overlap_each_side_mm": FELT_HIDDEN_OVERLAP_M * 1000.0,
            "center_disc_diameter_mm": CENTER_DISC_DIAMETER_M * 1000.0,
            "direction_labels": ["北", "东", "南", "西"],
            "material_review": {
                "frame": (
                    "reference-matched glossy black bronze alloy with restrained "
                    "brushing, smaller rounded corners and a continuous rounded "
                    "polished gold inlay"
                ),
                "felt": (
                    "reference-matched near-black cool forest green microfiber "
                    "with restrained sheen and procedural fiber relief"
                ),
                "center_disc": (
                    "black gunmetal automatic machine controller with four "
                    "dark-gold radial dividers forming direction-aligned fan "
                    "sectors, bright gold direction labels only and an unmarked "
                    "black center display"
                ),
                "glass_cover": (
                    "modeled smooth transparent glass, IOR 1.46, without "
                    "procedural roughness or bump texture"
                ),
            },
        },
        "measured_bounds_mm": {
            "minimum": [round(value * 1000.0, 4) for value in minimum],
            "maximum": [round(value * 1000.0, 4) for value in maximum],
            "size": [round(value * 1000.0, 4) for value in size],
        },
        "geometry": {
            obj.name: {
                "vertices": len(obj.data.vertices),
                "polygons": len(obj.data.polygons),
                "materials": [slot.name for slot in obj.data.materials],
            }
            for obj in objects
        },
        "files": [
            {
                "name": path.name,
                "bytes": path.stat().st_size,
                "sha256": sha256(path),
            }
            for path in files
            if path.is_file()
        ],
    }
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    return manifest_path


def main() -> None:
    args = arguments()
    project_root = args.project_root.resolve()
    output_dir = (
        args.output_dir.resolve()
        if args.output_dir
        else project_root / "SourceArt" / "3D" / "MahjongTableReview"
    )
    output_dir.mkdir(parents=True, exist_ok=True)

    frame = bpy.data.objects.get(SOURCE_FRAME)
    felt = bpy.data.objects.get(SOURCE_FELT)
    root = bpy.data.objects.get(SOURCE_ROOT)
    if frame is None or frame.type != "MESH":
        raise RuntimeError(f"Missing source frame {SOURCE_FRAME}")
    if felt is None or felt.type != "MESH":
        raise RuntimeError(f"Missing source felt {SOURCE_FELT}")
    if root is None:
        raise RuntimeError(f"Missing source root {SOURCE_ROOT}")

    root.name = REVIEW_ROOT
    root["review_status"] = "awaiting_human_approval"
    root["outer_dimensions_mm"] = "1500x1500"
    root["design_style"] = "automatic_mahjong_machine"
    collection = frame.users_collection[0]

    resize_table(frame, felt)
    replace_frame_with_rounded_geometry(frame)
    enhance_existing_table_materials()
    configure_review_lighting()
    frame_inlay = create_frame_gold_inlay(collection, root)
    controller = create_reference_center_controller(collection, root)
    objects = [frame, felt, *frame_inlay, *controller]

    minimum, maximum = bounds(objects)
    measured = maximum - minimum
    if abs(measured.x - OUTER_SIZE_M) > 0.0005:
        raise RuntimeError(f"Unexpected X size: {measured.x}")
    if abs(measured.y - OUTER_SIZE_M) > 0.0005:
        raise RuntimeError(f"Unexpected Y size: {measured.y}")
    if felt.dimensions.x + 0.0005 < PLAYING_OPENING_M:
        raise RuntimeError("Felt no longer overlaps the wood frame")

    preview_files = render_previews(output_dir)
    blend_path = output_dir / f"{REVIEW_ROOT}.blend"
    bpy.ops.wm.save_as_mainfile(filepath=str(blend_path), check_existing=False)
    export_files = export_review(output_dir, objects)
    generated = [blend_path, *export_files, *preview_files]
    manifest_path = write_manifest(output_dir, objects, generated)

    print(
        "AUTOMATIC_MAHJONG_TABLE_REVIEW_OK",
        f"blender={bpy.app.version_string}",
        f"size_mm=({measured.x * 1000.0:.3f},{measured.y * 1000.0:.3f})",
        f"frame_width_mm={VISIBLE_FRAME_WIDTH_M * 1000.0:.3f}",
        f"felt_size_mm={felt.dimensions.x * 1000.0:.3f}",
        f"controller_diameter_mm={CENTER_DISC_DIAMETER_M * 1000.0:.3f}",
        f"objects={len(objects)}",
        f"manifest={manifest_path}",
    )


if __name__ == "__main__":
    main()
