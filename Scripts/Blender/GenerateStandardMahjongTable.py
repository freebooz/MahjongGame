"""Blender 5.2 generator for the mobile-ready Mahjong tabletop.

The generated asset intentionally contains only:

* four independently machined walnut rails with 45-degree miter joints;
* one inset green felt mesh.

It contains no legs, center controller, tiles, or mechanical parts.  The playing
surface is authored at Z=0 so the Unreal runtime can place tiles directly on the
asset pivot.  The complete tabletop occupies 1150 x 1150 x 65 mm.

Command line:

    blender --background --python Scripts/Blender/GenerateStandardMahjongTable.py \
        -- H:/MahjongGame --render --export-fbx --export-glb
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import sys
from dataclasses import asdict, dataclass
from pathlib import Path

import bpy
from mathutils import Vector


SCRIPT_VERSION = "3.4.0"
MODEL_COLLECTION = "MG_MahjongTableTop"
PRESENTATION_COLLECTION = "MG_MahjongTableTop_Presentation"
ASSET_NAME = "SM_StandardMahjongTable"
FRAME_OBJECT_NAME = "SM_Mahjong_Table_Frame_MiterJoint"
FRAME_PART_NAMES = ("Frame_Front", "Frame_Back", "Frame_Left", "Frame_Right")
FELT_NAME = "Mahjong_Felt_Surface"


@dataclass(frozen=True)
class TabletopDimensions:
    """Production dimensions in meters."""

    size: float = 1.150
    thickness: float = 0.065
    frame_width: float = 0.090
    felt_thickness: float = 0.006
    frame_lip_above_felt: float = 0.005
    rail_edge_bevel: float = 0.012
    rail_bevel_segments: int = 6
    base_band_inset: float = 0.003
    base_band_bevel: float = 0.0035
    base_band_bevel_segments: int = 3
    base_band_top_z: float = -0.018
    bullnose_bottom_z: float = -0.030
    outer_corner_radius: float = 0.024
    outer_corner_arc_segments: int = 5
    # A furniture-tight joint: the previous 3 mm opening read as a structural
    # crack at gameplay distance. Keep a sub-millimetre seam so the four-rail
    # construction remains legible without looking incorrectly assembled.
    miter_joint_width: float = 0.0006
    felt_corner_radius: float = 0.012
    felt_corner_segments: int = 16

    @property
    def playing_size(self) -> float:
        return self.size - 2.0 * self.frame_width

    @property
    def felt_top(self) -> float:
        return 0.0

    @property
    def frame_top(self) -> float:
        return self.frame_lip_above_felt

    @property
    def frame_bottom(self) -> float:
        return self.frame_top - self.thickness


def arguments() -> argparse.Namespace:
    argv = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project_root", nargs="?", help="Mahjong project root")
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--render", action="store_true", help="Render the preview PNG")
    parser.add_argument("--export-fbx", action="store_true")
    parser.add_argument("--export-glb", action="store_true")
    parser.add_argument("--no-save", action="store_true")
    return parser.parse_args(argv)


def project_root_from_script() -> Path:
    return Path(__file__).resolve().parents[2]


def clean_scene() -> None:
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for collection in list(bpy.data.collections):
        bpy.data.collections.remove(collection)
    for datablocks in (
        bpy.data.meshes,
        bpy.data.materials,
        bpy.data.cameras,
        bpy.data.lights,
        bpy.data.worlds,
    ):
        for datablock in list(datablocks):
            if datablock.users == 0:
                datablocks.remove(datablock)


def configure_scene() -> None:
    scene = bpy.context.scene
    scene.name = "MahjongTableTop_AssetScene"
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.length_unit = "METERS"
    scene.unit_settings.scale_length = 1.0
    scene.render.resolution_x = 1600
    scene.render.resolution_y = 900
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.film_transparent = False
    for engine in ("BLENDER_EEVEE", "BLENDER_EEVEE_NEXT"):
        try:
            scene.render.engine = engine
            break
        except TypeError:
            continue
    scene.render.image_settings.color_mode = "RGBA"
    try:
        scene.view_settings.view_transform = "Standard"
        scene.view_settings.look = "None"
    except TypeError:
        scene.view_settings.look = "None"

    world = bpy.data.worlds.new("MahjongTableTop_World")
    world.use_nodes = True
    scene.world = world
    background = next(
        (node for node in world.node_tree.nodes if node.type == "BACKGROUND"),
        None,
    )
    if background:
        background.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
        background.inputs["Strength"].default_value = 0.0


def create_collection(name: str) -> bpy.types.Collection:
    collection = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(collection)
    return collection


def set_input(node: bpy.types.Node, name: str, value) -> None:
    socket = node.inputs.get(name)
    if socket is not None:
        socket.default_value = value


def principled_material(
    name: str,
    base_color: tuple[float, float, float, float],
    roughness: float,
) -> tuple[bpy.types.Material, bpy.types.Node, bpy.types.NodeTree]:
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    material.diffuse_color = base_color
    nodes = material.node_tree.nodes
    links = material.node_tree.links
    nodes.clear()

    output = nodes.new("ShaderNodeOutputMaterial")
    output.location = (620.0, 0.0)
    shader = nodes.new("ShaderNodeBsdfPrincipled")
    shader.location = (320.0, 0.0)
    set_input(shader, "Base Color", base_color)
    set_input(shader, "Metallic", 0.0)
    set_input(shader, "Roughness", roughness)
    set_input(shader, "IOR", 1.46)
    links.new(shader.outputs["BSDF"], output.inputs["Surface"])
    material["generator"] = Path(__file__).name
    material["pbr_workflow"] = "metallic_roughness"
    return material, shader, material.node_tree


def create_walnut_material() -> bpy.types.Material:
    material, shader, tree = principled_material(
        "M_Table_Walnut_PBR", (0.070, 0.015, 0.003, 1.0), 0.27
    )
    set_input(shader, "Coat Weight", 0.34)
    set_input(shader, "Coat Roughness", 0.19)
    nodes, links = tree.nodes, tree.links

    texcoord = nodes.new("ShaderNodeTexCoord")
    texcoord.location = (-900.0, 50.0)
    mapping = nodes.new("ShaderNodeMapping")
    mapping.location = (-710.0, 50.0)
    mapping.inputs["Scale"].default_value = (7.0, 0.65, 2.0)
    noise = nodes.new("ShaderNodeTexNoise")
    noise.location = (-500.0, 50.0)
    set_input(noise, "Scale", 4.6)
    set_input(noise, "Detail", 7.0)
    set_input(noise, "Roughness", 0.72)
    ramp = nodes.new("ShaderNodeValToRGB")
    ramp.location = (-250.0, 100.0)
    ramp.color_ramp.elements[0].color = (0.003, 0.0007, 0.0002, 1.0)
    ramp.color_ramp.elements[0].position = 0.18
    ramp.color_ramp.elements[1].color = (0.125, 0.027, 0.0045, 1.0)
    ramp.color_ramp.elements[1].position = 0.82
    middle = ramp.color_ramp.elements.new(0.50)
    middle.color = (0.028, 0.0055, 0.0012, 1.0)
    bump = nodes.new("ShaderNodeBump")
    bump.location = (70.0, -150.0)
    bump.inputs["Strength"].default_value = 0.18
    bump.inputs["Distance"].default_value = 0.00065

    links.new(texcoord.outputs["UV"], mapping.inputs["Vector"])
    links.new(mapping.outputs["Vector"], noise.inputs["Vector"])
    links.new(noise.outputs["Fac"], ramp.inputs["Fac"])
    links.new(ramp.outputs["Color"], shader.inputs["Base Color"])
    links.new(noise.outputs["Fac"], bump.inputs["Height"])
    links.new(bump.outputs["Normal"], shader.inputs["Normal"])
    material["surface"] = "polished_dark_walnut"
    return material


def create_felt_material() -> bpy.types.Material:
    material, shader, tree = principled_material(
        "M_Table_Felt_Green_PBR", (0.002, 0.075, 0.009, 1.0), 0.86
    )
    set_input(shader, "Sheen Weight", 0.015)
    set_input(shader, "Sheen Roughness", 0.92)
    set_input(shader, "Specular IOR Level", 0.010)
    nodes, links = tree.nodes, tree.links

    texcoord = nodes.new("ShaderNodeTexCoord")
    texcoord.location = (-980.0, 20.0)
    mapping = nodes.new("ShaderNodeMapping")
    mapping.location = (-810.0, 20.0)
    mapping.inputs["Scale"].default_value = (1.0, 1.0, 1.0)

    warp = nodes.new("ShaderNodeTexWave")
    warp.location = (-620.0, 150.0)
    warp.wave_type = "BANDS"
    warp.bands_direction = "X"
    set_input(warp, "Scale", 420.0)
    set_input(warp, "Distortion", 1.8)
    set_input(warp, "Detail", 3.0)
    set_input(warp, "Detail Scale", 2.0)

    weft = nodes.new("ShaderNodeTexWave")
    weft.location = (-620.0, -80.0)
    weft.wave_type = "BANDS"
    weft.bands_direction = "Y"
    set_input(weft, "Scale", 380.0)
    set_input(weft, "Distortion", 1.4)
    set_input(weft, "Detail", 3.0)

    weave = nodes.new("ShaderNodeMixRGB")
    weave.location = (-390.0, 90.0)
    weave.blend_type = "MULTIPLY"
    weave.inputs["Fac"].default_value = 1.0

    noise = nodes.new("ShaderNodeTexNoise")
    noise.location = (-610.0, -310.0)
    set_input(noise, "Scale", 220.0)
    set_input(noise, "Detail", 2.3)
    set_input(noise, "Roughness", 0.78)

    fiber = nodes.new("ShaderNodeMixRGB")
    fiber.location = (-170.0, 20.0)
    fiber.blend_type = "MULTIPLY"
    fiber.inputs["Fac"].default_value = 0.28

    ramp = nodes.new("ShaderNodeValToRGB")
    ramp.location = (20.0, 130.0)
    ramp.color_ramp.elements[0].color = (0.0002, 0.008, 0.0008, 1.0)
    ramp.color_ramp.elements[1].color = (0.0010, 0.035, 0.0030, 1.0)

    roughness = nodes.new("ShaderNodeMapRange")
    roughness.location = (20.0, -260.0)
    set_input(roughness, "From Min", 0.0)
    set_input(roughness, "From Max", 1.0)
    set_input(roughness, "To Min", 0.80)
    set_input(roughness, "To Max", 0.94)

    bump = nodes.new("ShaderNodeBump")
    bump.location = (220.0, -90.0)
    bump.inputs["Strength"].default_value = 0.055
    bump.inputs["Distance"].default_value = 0.00018

    links.new(texcoord.outputs["UV"], mapping.inputs["Vector"])
    links.new(mapping.outputs["Vector"], warp.inputs["Vector"])
    links.new(mapping.outputs["Vector"], weft.inputs["Vector"])
    links.new(mapping.outputs["Vector"], noise.inputs["Vector"])
    links.new(warp.outputs["Color"], weave.inputs[1])
    links.new(weft.outputs["Color"], weave.inputs[2])
    links.new(weave.outputs["Color"], fiber.inputs[1])
    links.new(noise.outputs["Fac"], fiber.inputs[2])
    links.new(fiber.outputs["Color"], ramp.inputs["Fac"])
    links.new(ramp.outputs["Color"], shader.inputs["Base Color"])
    links.new(fiber.outputs["Color"], bump.inputs["Height"])
    links.new(bump.outputs["Normal"], shader.inputs["Normal"])
    links.new(noise.outputs["Fac"], roughness.inputs["Value"])
    links.new(roughness.outputs["Result"], shader.inputs["Roughness"])
    material["surface"] = "premium_green_wool_felt"
    return material


def rounded_rectangle(
    width: float,
    depth: float,
    radius: float,
    z: float,
    corner_segments: int,
) -> list[tuple[float, float, float]]:
    half_x = width * 0.5
    half_y = depth * 0.5
    radius = min(radius, half_x - 0.001, half_y - 0.001)
    centers_and_starts = (
        ((half_x - radius, -half_y + radius), -math.pi * 0.5),
        ((half_x - radius, half_y - radius), 0.0),
        ((-half_x + radius, half_y - radius), math.pi * 0.5),
        ((-half_x + radius, -half_y + radius), math.pi),
    )
    points: list[tuple[float, float, float]] = []
    for (center_x, center_y), angle_start in centers_and_starts:
        for index in range(corner_segments):
            angle = angle_start + (math.pi * 0.5) * index / corner_segments
            points.append(
                (
                    center_x + math.cos(angle) * radius,
                    center_y + math.sin(angle) * radius,
                    z,
                )
            )
    return points


def create_beveled_prism(
    name: str,
    outline: list[tuple[float, float]],
    z_bottom: float,
    z_top: float,
    bevel_width: float,
    bevel_segments: int,
    material: bpy.types.Material,
    collection: bpy.types.Collection,
    long_edge_indices: tuple[int, int] | None = None,
) -> bpy.types.Object:
    point_count = len(outline)
    vertices = [(x, y, z_bottom) for x, y in outline]
    vertices.extend((x, y, z_top) for x, y in outline)
    faces: list[tuple[int, ...]] = [
        tuple(reversed(range(point_count))),
        tuple(range(point_count, point_count * 2)),
    ]
    for index in range(point_count):
        following = (index + 1) % point_count
        faces.append((index, following, point_count + following, point_count + index))

    mesh = bpy.data.meshes.new(f"{name}_Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.materials.append(material)
    mesh.validate()
    mesh.update(calc_edges=True)
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)

    bevel = obj.modifiers.new("MachinedEdgeRoundover", "BEVEL")
    bevel.width = bevel_width
    bevel.segments = bevel_segments
    if long_edge_indices:
        weights = mesh.attributes.new("bevel_weight_edge", "FLOAT", "EDGE")
        for edge in mesh.edges:
            first, second = edge.vertices
            if (first < point_count) != (second < point_count):
                continue
            local_pair = {first % point_count, second % point_count}
            if any(
                local_pair == {index, (index + 1) % point_count}
                for index in long_edge_indices
            ):
                weights.data[edge.index].value = 1.0
        bevel.limit_method = "WEIGHT"
    else:
        bevel.limit_method = "ANGLE"
        bevel.angle_limit = math.radians(20.0)
    bevel.harden_normals = True
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.modifier_apply(modifier=bevel.name)
    triangulate = obj.modifiers.new("GameReadyTriangulation", "TRIANGULATE")
    triangulate.quad_method = "BEAUTY"
    triangulate.ngon_method = "BEAUTY"
    bpy.ops.object.modifier_apply(modifier=triangulate.name)
    obj.select_set(False)
    return obj


def apply_directional_wood_uv(obj: bpy.types.Object, grain_axis: str) -> None:
    """Map V along the rail so the walnut grain follows the machined piece."""

    mesh = obj.data
    uv_layer = mesh.uv_layers.get("UVMap") or mesh.uv_layers.new(name="UVMap")
    x_values = [vertex.co.x for vertex in mesh.vertices]
    y_values = [vertex.co.y for vertex in mesh.vertices]
    z_values = [vertex.co.z for vertex in mesh.vertices]
    min_x, max_x = min(x_values), max(x_values)
    min_y, max_y = min(y_values), max(y_values)
    min_z, max_z = min(z_values), max(z_values)
    length_min, length_max = (min_x, max_x) if grain_axis == "X" else (min_y, max_y)
    cross_min, cross_max = (min_y, max_y) if grain_axis == "X" else (min_x, max_x)

    def normalized(value: float, minimum: float, maximum: float) -> float:
        return (value - minimum) / max(maximum - minimum, 1e-6)

    for polygon in mesh.polygons:
        horizontal = abs(polygon.normal.z) > 0.70
        for loop_index in polygon.loop_indices:
            vertex = mesh.vertices[mesh.loops[loop_index].vertex_index].co
            length_value = vertex.x if grain_axis == "X" else vertex.y
            cross_value = vertex.y if grain_axis == "X" else vertex.x
            v = normalized(length_value, length_min, length_max) * 4.0
            u = (
                normalized(cross_value, cross_min, cross_max)
                if horizontal
                else normalized(vertex.z, min_z, max_z)
            )
            uv_layer.data[loop_index].uv = (u, v)
    obj["wood_grain_axis"] = grain_axis
    obj["wood_grain_orientation"] = "longitudinal"
    obj["wood_uv_v_tiles"] = 4.0


def join_frame_segments(
    segment_parts: dict[str, list[bpy.types.Object]],
    joint_width_mm: float,
) -> bpy.types.Object:
    """Join four objects while preserving four disconnected geometry islands."""

    bpy.ops.object.select_all(action="DESELECT")
    active = None
    for segment_name, parts in segment_parts.items():
        for part in parts:
            group = part.vertex_groups.new(name=segment_name)
            group.add(range(len(part.data.vertices)), 1.0, "REPLACE")
            part.select_set(True)
            active = active or part
    bpy.context.view_layer.objects.active = active
    bpy.ops.object.join()
    frame = bpy.context.object
    frame.name = FRAME_OBJECT_NAME
    frame.data.name = f"{FRAME_OBJECT_NAME}_Mesh"
    frame["asset_role"] = "four_segment_mitered_wood_frame"
    frame["segment_names"] = ",".join(FRAME_PART_NAMES)
    frame["segment_count"] = 4
    frame["joint_angle_degrees"] = 45.0
    frame["joint_width_mm"] = joint_width_mm
    frame["joint_fill_geometry"] = False
    frame["shared_uv"] = True
    frame["mobile_game_ready"] = True
    frame.select_set(False)
    return frame


def create_mitered_frame(
    dimensions: TabletopDimensions,
    wood_material: bpy.types.Material,
    collection: bpy.types.Collection,
) -> bpy.types.Object:
    """Create four discrete rails meeting at exact 45-degree miter seams."""

    half = dimensions.size * 0.5
    inner = dimensions.playing_size * 0.5
    z_bottom = dimensions.frame_bottom
    z_top = dimensions.frame_top

    def front_outline(
        outer: float,
        opening: float,
        radius: float,
        segments: int,
    ) -> tuple[list[tuple[float, float]], tuple[int, ...]]:
        center_right = Vector((outer - radius, -outer + radius))
        center_left = Vector((-outer + radius, -outer + radius))
        points = [(-opening, -opening), (opening, -opening)]
        for index in range(segments + 1):
            angle = math.radians(-45.0 - 45.0 * index / segments)
            points.append(
                (
                    center_right.x + math.cos(angle) * radius,
                    center_right.y + math.sin(angle) * radius,
                )
            )
        points.append((-outer + radius, -outer))
        for index in range(1, segments + 1):
            angle = math.radians(-90.0 - 45.0 * index / segments)
            points.append(
                (
                    center_left.x + math.cos(angle) * radius,
                    center_left.y + math.sin(angle) * radius,
                )
            )
        # Edge 1 and the closing edge are the two miter cuts; every other outline
        # edge receives the same longitudinal bullnose treatment.
        trim = dimensions.miter_joint_width / math.sqrt(2.0)
        points[0] = (points[0][0] + trim, points[0][1])
        points[1] = (points[1][0] - trim, points[1][1])
        points[2] = (points[2][0] - trim, points[2][1])
        points[-1] = (points[-1][0] + trim, points[-1][1])
        bevel_edges = tuple(index for index in range(len(points)) if index not in {1, len(points) - 1})
        return points, bevel_edges

    def rotate_outline(
        outline: list[tuple[float, float]], quarter_turns: int
    ) -> list[tuple[float, float]]:
        result = []
        for x, y in outline:
            for _ in range(quarter_turns % 4):
                x, y = -y, x
            result.append((x, y))
        return result

    cap_front, cap_bevel_edges = front_outline(
        half,
        inner,
        dimensions.outer_corner_radius,
        dimensions.outer_corner_arc_segments,
    )
    base_outer = half - dimensions.base_band_inset
    base_inner = inner + dimensions.base_band_inset
    base_front, base_bevel_edges = front_outline(
        base_outer,
        base_inner,
        max(0.006, dimensions.outer_corner_radius - dimensions.base_band_inset),
        dimensions.outer_corner_arc_segments,
    )
    quarter_turns = (0, 2, 3, 1)
    cap_outlines = tuple(rotate_outline(cap_front, turns) for turns in quarter_turns)
    base_outlines = tuple(rotate_outline(base_front, turns) for turns in quarter_turns)
    grain_axes = ("X", "X", "Y", "Y")
    segment_parts: dict[str, list[bpy.types.Object]] = {}
    for name, cap_outline, base_outline, grain_axis in zip(
        FRAME_PART_NAMES, cap_outlines, base_outlines, grain_axes
    ):
        base = create_beveled_prism(
            f"{name}_BaseBand",
            base_outline,
            z_bottom,
            dimensions.base_band_top_z,
            dimensions.base_band_bevel,
            dimensions.base_band_bevel_segments,
            wood_material,
            collection,
            base_bevel_edges,
        )
        cap = create_beveled_prism(
            f"{name}_BullnoseCap",
            cap_outline,
            dimensions.bullnose_bottom_z,
            z_top,
            dimensions.rail_edge_bevel,
            dimensions.rail_bevel_segments,
            wood_material,
            collection,
            cap_bevel_edges,
        )
        for part in (base, cap):
            part["asset_role"] = "independent_mitered_wood_rail"
            part["joint_angle_degrees"] = 45.0
            part["mobile_game_ready"] = True
            apply_directional_wood_uv(part, grain_axis)
        segment_parts[name] = [base, cap]
    return join_frame_segments(
        segment_parts,
        dimensions.miter_joint_width * 1000.0,
    )


def create_felt_surface(
    dimensions: TabletopDimensions,
    material: bpy.types.Material,
    collection: bpy.types.Collection,
) -> bpy.types.Object:
    segments = dimensions.felt_corner_segments
    top = rounded_rectangle(
        dimensions.playing_size - 0.004,
        dimensions.playing_size - 0.004,
        dimensions.felt_corner_radius,
        dimensions.felt_top,
        segments,
    )
    bottom = rounded_rectangle(
        dimensions.playing_size - 0.006,
        dimensions.playing_size - 0.006,
        dimensions.felt_corner_radius - 0.001,
        dimensions.felt_top - dimensions.felt_thickness,
        segments,
    )
    vertices = top + bottom
    point_count = len(top)
    top_center = len(vertices)
    vertices.append((0.0, 0.0, dimensions.felt_top))
    bottom_center = len(vertices)
    vertices.append((0.0, 0.0, dimensions.felt_top - dimensions.felt_thickness))

    faces: list[tuple[int, ...]] = []
    for index in range(point_count):
        following = (index + 1) % point_count
        faces.append((top_center, index, following))
        faces.append((bottom_center, point_count + following, point_count + index))
        faces.append((index, point_count + index, point_count + following, following))

    mesh = bpy.data.meshes.new(f"{FELT_NAME}_Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.materials.append(material)
    mesh.update(calc_edges=True)
    obj = bpy.data.objects.new(FELT_NAME, mesh)
    collection.objects.link(obj)
    for polygon in mesh.polygons:
        polygon.use_smooth = polygon.loop_total == 4
    obj["asset_role"] = "inset_playing_surface"
    obj["mobile_game_ready"] = True
    return obj


def smart_uv(obj: bpy.types.Object) -> None:
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(
        angle_limit=math.radians(55.0),
        island_margin=0.025,
        area_weight=0.15,
        correct_aspect=True,
        scale_to_bounds=True,
    )
    bpy.ops.object.mode_set(mode="OBJECT")
    obj.select_set(False)


def mesh_triangle_count(objects: list[bpy.types.Object]) -> int:
    count = 0
    depsgraph = bpy.context.evaluated_depsgraph_get()
    for obj in objects:
        evaluated = obj.evaluated_get(depsgraph)
        mesh = evaluated.to_mesh()
        mesh.calc_loop_triangles()
        count += len(mesh.loop_triangles)
        evaluated.to_mesh_clear()
    return count


def mesh_bounds(objects: list[bpy.types.Object]) -> tuple[Vector, Vector]:
    points = [obj.matrix_world @ Vector(corner) for obj in objects for corner in obj.bound_box]
    minimum = Vector((min(p.x for p in points), min(p.y for p in points), min(p.z for p in points)))
    maximum = Vector((max(p.x for p in points), max(p.y for p in points), max(p.z for p in points)))
    return minimum, maximum


def create_preview(dimensions: TabletopDimensions, collection: bpy.types.Collection) -> None:
    for name, location, energy, size, color in (
        ("Key_Softbox", (-1.7, -1.8, 2.2), 420.0, 2.8, (1.0, 0.74, 0.52)),
        ("Fill_Softbox", (1.6, -0.2, 1.4), 45.0, 2.2, (0.78, 0.86, 1.0)),
        ("Rim_Softbox", (0.2, 1.7, 1.9), 110.0, 1.8, (0.70, 1.0, 0.82)),
    ):
        light_data = bpy.data.lights.new(name, "AREA")
        light_data.energy = energy
        light_data.shape = "DISK"
        light_data.size = size
        light_data.color = color
        light = bpy.data.objects.new(name, light_data)
        light.location = location
        collection.objects.link(light)

    camera_data = bpy.data.cameras.new("Preview_Camera")
    camera = bpy.data.objects.new("Preview_Camera", camera_data)
    camera.location = (1.92, -2.06, 0.78)
    camera.data.lens = 62.0
    camera.data.sensor_width = 36.0
    camera.data.shift_y = -0.09
    collection.objects.link(camera)
    target = Vector((0.0, 0.0, -0.010))
    camera.rotation_euler = (target - camera.location).to_track_quat("-Z", "Y").to_euler()
    bpy.context.scene.camera = camera


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def export_fbx(path: Path, objects: list[bpy.types.Object]) -> None:
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.export_scene.fbx(
        filepath=str(path),
        use_selection=True,
        object_types={"MESH"},
        use_mesh_modifiers=True,
        mesh_smooth_type="FACE",
        use_tspace=True,
        add_leaf_bones=False,
        bake_anim=False,
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        axis_forward="-Y",
        axis_up="Z",
    )


def export_glb(path: Path, objects: list[bpy.types.Object]) -> None:
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.export_scene.gltf(
        filepath=str(path),
        export_format="GLB",
        use_selection=True,
        export_apply=True,
        export_materials="EXPORT",
        export_yup=True,
    )


def write_manifest(
    output_dir: Path,
    dimensions: TabletopDimensions,
    objects: list[bpy.types.Object],
    generated_files: list[Path],
) -> None:
    minimum, maximum = mesh_bounds(objects)
    size = maximum - minimum
    per_object = {}
    depsgraph = bpy.context.evaluated_depsgraph_get()
    for obj in objects:
        evaluated = obj.evaluated_get(depsgraph)
        mesh = evaluated.to_mesh()
        mesh.calc_loop_triangles()
        per_object[obj.name] = {
            "vertices": len(mesh.vertices),
            "polygons": len(mesh.polygons),
            "triangles": len(mesh.loop_triangles),
            "material": obj.data.materials[0].name if obj.data.materials else None,
            "material_slots": [material.name for material in obj.data.materials],
        }
        evaluated.to_mesh_clear()

    manifest = {
        "generator": "Blender 5.2 GenerateStandardMahjongTable.py",
        "generator_version": SCRIPT_VERSION,
        "blender_version": bpy.app.version_string,
        "asset_root": ASSET_NAME,
        "asset_scope": "tabletop_only",
        "frame_construction": "four_independent_rails_with_45_degree_miter_joints",
        "frame_mesh_layout": {
            "object_count": 1,
            "segment_count": 4,
            "joint_fill_geometry": False,
            "segments": list(FRAME_PART_NAMES),
            "shared_uv": True,
            "grain_direction": {
                "Frame_Front": "longitudinal_X",
                "Frame_Back": "longitudinal_X",
                "Frame_Left": "longitudinal_Y",
                "Frame_Right": "longitudinal_Y",
            },
            "uv_scale_consistent": True,
            "uv_v_tiles_per_rail": 4.0,
        },
        "excluded_parts": ["legs", "center_controller", "mahjong_tiles", "mechanical_structure"],
        "nominal_dimensions_mm": [1150.0, 1150.0, 65.0],
        "measured_bounds_mm": {
            "minimum": [round(value * 1000.0, 4) for value in minimum],
            "maximum": [round(value * 1000.0, 4) for value in maximum],
            "size": [round(value * 1000.0, 4) for value in size],
        },
        "playing_surface_mm": [dimensions.playing_size * 1000.0] * 2,
        "playing_surface_z_mm": 0.0,
        "pivot": "playing_surface_center",
        "mesh_object_count": len(objects),
        "geometry": per_object,
        "triangle_count": mesh_triangle_count(objects),
        "mobile_triangle_budget": 5000,
        "materials": [
            "M_Table_Walnut_PBR",
            "M_Table_Felt_Green_PBR",
        ],
        "material_workflow": "PBR metallic-roughness",
        "dimensions_m": asdict(dimensions),
        "files": [
            {
                "path": path.relative_to(output_dir.parents[2]).as_posix(),
                "bytes": path.stat().st_size,
                "sha256": sha256(path),
            }
            for path in generated_files
            if path.is_file()
        ],
    }
    (output_dir / "MahjongTableAssetManifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )


def main() -> None:
    args = arguments()
    root = Path(args.project_root).resolve() if args.project_root else project_root_from_script()
    output_dir = (
        args.output_dir.resolve()
        if args.output_dir
        else root / "SourceArt" / "3D" / "MahjongTable"
    )
    output_dir.mkdir(parents=True, exist_ok=True)

    clean_scene()
    configure_scene()
    dimensions = TabletopDimensions()
    model_collection = create_collection(MODEL_COLLECTION)
    presentation_collection = create_collection(PRESENTATION_COLLECTION)
    walnut = create_walnut_material()
    felt_material = create_felt_material()
    frame = create_mitered_frame(dimensions, walnut, model_collection)
    felt = create_felt_surface(dimensions, felt_material, model_collection)
    objects = [frame, felt]
    smart_uv(felt)

    root_empty = bpy.data.objects.new(ASSET_NAME, None)
    model_collection.objects.link(root_empty)
    root_empty["asset_scope"] = "tabletop_only"
    root_empty["dimensions_mm"] = "1150x1150x65"
    for obj in objects:
        obj.parent = root_empty

    create_preview(dimensions, presentation_collection)
    generated_files: list[Path] = []

    blend_path = output_dir / f"{ASSET_NAME}.blend"
    if not args.no_save:
        bpy.ops.wm.save_as_mainfile(filepath=str(blend_path), check_existing=False)
        generated_files.append(blend_path)

    if args.export_fbx:
        fbx_path = output_dir / f"{ASSET_NAME}.fbx"
        export_fbx(fbx_path, objects)
        generated_files.append(fbx_path)

    if args.export_glb:
        glb_path = output_dir / f"{ASSET_NAME}.glb"
        export_glb(glb_path, objects)
        generated_files.append(glb_path)

    if args.render:
        preview_path = output_dir / "StandardMahjongTable_Preview.png"
        bpy.context.scene.render.filepath = str(preview_path)
        bpy.ops.render.render(write_still=True)
        generated_files.append(preview_path)

    write_manifest(output_dir, dimensions, objects, generated_files)
    minimum, maximum = mesh_bounds(objects)
    dimensions_cm = (maximum - minimum) * 100.0
    print(
        "MAHJONG_TABLETOP_GENERATED "
        f"blender={bpy.app.version_string} "
        f"dimensions_cm=({dimensions_cm.x:.3f},{dimensions_cm.y:.3f},{dimensions_cm.z:.3f}) "
        f"triangles={mesh_triangle_count(objects)} objects={len(objects)}"
    )


if __name__ == "__main__":
    main()
