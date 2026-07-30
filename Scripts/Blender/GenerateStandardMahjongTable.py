"""生成 115 × 115 cm 的高保真 PBR 麻将桌台面源模型。

脚本只生成圆角胡桃木围框、细木工接缝和下沉式绿色绒布，不包含桌腿、麻将牌、
中控器或机械结构。可编辑产品相机和四点棚拍灯光保存在独立集合中，不参与模型导出。
打牌平面固定为 Z=0，以保持 Unreal 运行时牌面放置契约。

命令行：

    blender --background --python Scripts/Blender/GenerateStandardMahjongTable.py \
        -- <project-root> --render --export-fbx --export-glb
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


SCRIPT_VERSION = "4.0.0"
MODEL_COLLECTION = "MG_MahjongTable_Model"
PRESENTATION_COLLECTION = "MG_MahjongTable_Studio"
ASSET_NAME = "SM_StandardMahjongTable"
WOOD_OBJECT_NAME = "SM_MahjongTable_RoundedWalnutFrame"
FELT_OBJECT_NAME = "SM_MahjongTable_RecessedFelt"
JOINT_OBJECT_NAME = "SM_MahjongTable_JoinerySeams"
WOOD_MATERIAL_NAME = "M_Table_Walnut_PBR"
FELT_MATERIAL_NAME = "M_Table_Felt_Green_PBR"
JOINT_MATERIAL_NAME = "M_Table_WoodJoint_PBR"


@dataclass(frozen=True)
class TabletopDimensions:
    """桌面生产尺寸，统一使用米；实例不可变，确保建模、验证和 manifest 口径一致。"""

    # 外框、打牌区与总高度决定 Unreal 碰撞和摆牌边界。
    size: float = 1.150
    playing_size: float = 0.920
    total_height: float = 0.125
    # 下围裙和上扶手分别描述家具截面，不允许跨过 Z=0 打牌平面。
    apron_bottom: float = -0.095
    apron_top: float = -0.006
    apron_corner_radius: float = 0.052
    apron_bevel: float = 0.010
    apron_bevel_segments: int = 5
    rail_bottom: float = -0.018
    rail_top: float = 0.030
    rail_outer_corner_radius: float = 0.062
    rail_inner_corner_radius: float = 0.028
    rail_corner_segments: int = 12
    felt_bottom: float = -0.010
    felt_top: float = 0.0
    felt_corner_radius: float = 0.028
    felt_bevel: float = 0.003
    felt_bevel_segments: int = 4
    decorative_band_z: float = -0.028
    # 接缝是可见的浅层家具细节，宽度单位为米，不代表真实结构裂缝。
    seam_distance_from_corner: float = 0.155
    seam_width: float = 0.0012


def arguments() -> argparse.Namespace:
    """解析 Blender `--` 之后的参数；所有导出和渲染动作默认关闭，避免误写资产。"""

    argv = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project_root", nargs="?", help="Mahjong project root")
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--render", action="store_true")
    parser.add_argument("--export-fbx", action="store_true")
    parser.add_argument("--export-glb", action="store_true")
    parser.add_argument("--no-save", action="store_true")
    return parser.parse_args(argv)


def project_root_from_script() -> Path:
    """从 `Scripts/Blender` 位置推导仓库根目录，供未显式传参的本地执行使用。"""

    return Path(__file__).resolve().parents[2]


def clean_scene() -> None:
    """清空当前 Blender 场景和无引用数据块；调用后现有未保存场景内容不可恢复。"""

    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for collection in list(bpy.data.collections):
        bpy.data.collections.remove(collection)
    for datablocks in (
        bpy.data.meshes,
        bpy.data.materials,
        bpy.data.images,
        bpy.data.cameras,
        bpy.data.lights,
        bpy.data.worlds,
    ):
        for datablock in list(datablocks):
            if datablock.users == 0:
                datablocks.remove(datablock)


def configure_scene() -> None:
    """配置米制、渲染器、色彩管理和棚拍世界；不创建生产模型对象。"""

    scene = bpy.context.scene
    bpy.context.preferences.filepaths.save_version = 0
    scene.name = "MahjongTable_PBR_AssetScene"
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.length_unit = "METERS"
    scene.unit_settings.scale_length = 1.0
    scene.render.resolution_x = 1920
    scene.render.resolution_y = 1080
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.film_transparent = False
    scene.render.image_settings.color_depth = "8"
    try:
        scene.render.engine = "BLENDER_EEVEE_NEXT"
    except TypeError:
        scene.render.engine = "BLENDER_EEVEE"
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"
    scene.view_settings.exposure = 0.0

    world = bpy.data.worlds.new("MahjongTable_BlackStudioWorld")
    world.use_nodes = True
    scene.world = world
    background = next(
        (node for node in world.node_tree.nodes if node.type == "BACKGROUND"),
        None,
    )
    if background:
        background.inputs["Color"].default_value = (0.001, 0.001, 0.001, 1.0)
        background.inputs["Strength"].default_value = 0.025


def create_collection(name: str) -> bpy.types.Collection:
    """创建并挂接场景集合，返回值由调用方持有并决定模型或展示用途。"""

    collection = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(collection)
    return collection


def set_input(node: bpy.types.Node, name: str, value) -> None:
    """在不同 Blender 小版本间安全设置可选节点输入；不存在的插槽保持默认值。"""

    socket = node.inputs.get(name)
    if socket is not None:
        socket.default_value = value


def load_texture(texture_dir: Path, filename: str, non_color: bool) -> bpy.types.Image:
    """加载并打包 PBR 纹理；缺失文件立即失败，防止生成仅靠回退色伪装成功的资产。"""

    path = texture_dir / filename
    if not path.is_file():
        raise FileNotFoundError(f"Missing PBR texture: {path}")
    image = bpy.data.images.load(str(path), check_existing=True)
    image.name = Path(filename).stem
    image.colorspace_settings.name = "Non-Color" if non_color else "sRGB"
    image.pack()
    return image


def create_pbr_material(
    name: str,
    texture_dir: Path,
    stem: str,
    *,
    fallback_color: tuple[float, float, float, float],
    coat_weight: float,
    coat_roughness: float,
    sheen_weight: float,
) -> bpy.types.Material:
    """创建金属度-粗糙度 PBR 材质并连接 BaseColor、Roughness 和 DirectX Normal。"""

    material = bpy.data.materials.new(name)
    material.use_nodes = True
    material.diffuse_color = fallback_color
    nodes = material.node_tree.nodes
    links = material.node_tree.links
    nodes.clear()

    output = nodes.new("ShaderNodeOutputMaterial")
    output.location = (680.0, 0.0)
    shader = nodes.new("ShaderNodeBsdfPrincipled")
    shader.location = (380.0, 0.0)
    set_input(shader, "Metallic", 0.0)
    set_input(shader, "IOR", 1.47)
    set_input(shader, "Coat Weight", coat_weight)
    set_input(shader, "Coat Roughness", coat_roughness)
    set_input(shader, "Sheen Weight", sheen_weight)
    set_input(shader, "Specular IOR Level", 0.28 if stem == "Wood" else 0.08)

    base = nodes.new("ShaderNodeTexImage")
    base.name = f"{stem}_BaseColor"
    base.label = "sRGB Base Color"
    base.location = (-650.0, 260.0)
    base.image = load_texture(texture_dir, f"T_{stem}_BaseColor_2K.png", False)

    roughness = nodes.new("ShaderNodeTexImage")
    roughness.name = f"{stem}_Roughness"
    roughness.label = "Linear Roughness"
    roughness.location = (-650.0, 20.0)
    roughness.image = load_texture(texture_dir, f"T_{stem}_Roughness_2K.png", True)

    normal = nodes.new("ShaderNodeTexImage")
    normal.name = f"{stem}_Normal"
    normal.label = "DirectX Tangent Normal"
    normal.location = (-650.0, -230.0)
    normal.image = load_texture(texture_dir, f"T_{stem}_Normal_2K.png", True)

    # 源法线按 Unreal/DirectX 制作，只在 Blender 节点中翻转绿色通道，
    # 保证打包的原始纹理仍可直接导入 Unreal。
    separate = nodes.new("ShaderNodeSeparateColor")
    separate.location = (-390.0, -230.0)
    invert_green = nodes.new("ShaderNodeMath")
    invert_green.operation = "SUBTRACT"
    invert_green.inputs[0].default_value = 1.0
    invert_green.location = (-170.0, -270.0)
    combine = nodes.new("ShaderNodeCombineColor")
    combine.location = (30.0, -230.0)
    normal_map = nodes.new("ShaderNodeNormalMap")
    normal_map.location = (190.0, -190.0)
    normal_map.inputs["Strength"].default_value = 0.25 if stem == "Wood" else 0.16

    links.new(base.outputs["Color"], shader.inputs["Base Color"])
    links.new(roughness.outputs["Color"], shader.inputs["Roughness"])
    links.new(normal.outputs["Color"], separate.inputs["Color"])
    links.new(separate.outputs["Red"], combine.inputs["Red"])
    links.new(separate.outputs["Green"], invert_green.inputs[1])
    links.new(invert_green.outputs[0], combine.inputs["Green"])
    links.new(separate.outputs["Blue"], combine.inputs["Blue"])
    links.new(combine.outputs["Color"], normal_map.inputs["Color"])
    links.new(normal_map.outputs["Normal"], shader.inputs["Normal"])
    links.new(shader.outputs["BSDF"], output.inputs["Surface"])

    material["generator"] = Path(__file__).name
    material["pbr_workflow"] = "metallic_roughness"
    material["metallic"] = 0.0
    material["texture_set"] = stem
    return material


def create_joint_material() -> bpy.types.Material:
    """创建低反差木工接缝材质；接缝用于结构可读性，不能表现成黑色油漆条。"""

    material = bpy.data.materials.new(JOINT_MATERIAL_NAME)
    material.use_nodes = True
    material.diffuse_color = (0.012, 0.0035, 0.0012, 1.0)
    shader = next(
        node for node in material.node_tree.nodes if node.type == "BSDF_PRINCIPLED"
    )
    set_input(shader, "Base Color", (0.012, 0.0035, 0.0012, 1.0))
    set_input(shader, "Metallic", 0.0)
    set_input(shader, "Roughness", 0.48)
    set_input(shader, "Coat Weight", 0.08)
    set_input(shader, "Coat Roughness", 0.35)
    material["surface"] = "subtle_recessed_wood_joinery"
    material["pbr_workflow"] = "metallic_roughness"
    return material


def rounded_perimeter(
    width: float,
    depth: float,
    radius: float,
    z: float,
    corner_segments: int,
) -> list[tuple[float, float, float]]:
    """按顺时针生成圆角矩形周界点；半径会收敛到几何可行范围。"""

    half_x = width * 0.5
    half_y = depth * 0.5
    radius = min(radius, half_x - 0.001, half_y - 0.001)
    corners = (
        ((half_x - radius, -half_y + radius), -math.pi * 0.5),
        ((half_x - radius, half_y - radius), 0.0),
        ((-half_x + radius, half_y - radius), math.pi * 0.5),
        ((-half_x + radius, -half_y + radius), math.pi),
    )
    points: list[tuple[float, float, float]] = []
    for (center_x, center_y), start_angle in corners:
        for index in range(corner_segments):
            angle = start_angle + math.pi * 0.5 * index / corner_segments
            points.append(
                (
                    center_x + math.cos(angle) * radius,
                    center_y + math.sin(angle) * radius,
                    z,
                )
            )
    return points


def recalculate_normals(obj: bpy.types.Object) -> None:
    """向外重算法线；旧 Blender 缺少对应操作时保持现有法线并继续。"""

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


def add_weighted_smoothing(obj: bpy.types.Object) -> None:
    """启用平滑并按 50 度设置锐边，减少家具大曲面的分面高光。"""

    for polygon in obj.data.polygons:
        polygon.use_smooth = True
    try:
        obj.data.set_sharp_from_angle(angle=math.radians(50.0))
    except (AttributeError, TypeError):
        pass


def create_rounded_prism(
    name: str,
    width: float,
    depth: float,
    radius: float,
    z_bottom: float,
    z_top: float,
    bevel_width: float,
    bevel_segments: int,
    material: bpy.types.Material,
    collection: bpy.types.Collection,
    corner_segments: int = 14,
) -> bpy.types.Object:
    """创建封闭圆角棱柱、应用倒角和平滑，并生成环向木纹 UV。"""

    bottom = rounded_perimeter(width, depth, radius, z_bottom, corner_segments)
    top = rounded_perimeter(width, depth, radius, z_top, corner_segments)
    count = len(bottom)
    vertices = bottom + top
    faces: list[tuple[int, ...]] = [
        tuple(reversed(range(count))),
        tuple(range(count, count * 2)),
    ]
    for index in range(count):
        following = (index + 1) % count
        faces.append((index, following, count + following, count + index))

    mesh = bpy.data.meshes.new(f"{name}_Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.materials.append(material)
    mesh.validate()
    mesh.update(calc_edges=True)
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    recalculate_normals(obj)

    bevel = obj.modifiers.new("SoftFurnitureRoundover", "BEVEL")
    bevel.width = bevel_width
    bevel.segments = bevel_segments
    bevel.limit_method = "ANGLE"
    bevel.angle_limit = math.radians(12.0)
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.modifier_apply(modifier=bevel.name)
    obj.select_set(False)
    add_weighted_smoothing(obj)
    cylindrical_wood_uv(obj)
    return obj


def cylindrical_wood_uv(obj: bpy.types.Object) -> None:
    """生成沿圆角围框连续行进的胡桃木纹 UV，并在角度接缝处修正跨区插值。"""

    mesh = obj.data
    uv_layer = mesh.uv_layers.get("UVMap") or mesh.uv_layers.new(name="UVMap")
    z_values = [vertex.co.z for vertex in mesh.vertices]
    min_z, max_z = min(z_values), max(z_values)

    def perimeter_coordinate(point: Vector) -> float:
        """把 XY 极角映射到四个 UV 周界单元，保持四边纹理密度一致。"""

        angle = math.atan2(point.y, point.x)
        return (angle + math.pi) / (math.tau) * 4.0

    for polygon in mesh.polygons:
        coords = [mesh.vertices[mesh.loops[i].vertex_index].co for i in polygon.loop_indices]
        values = [perimeter_coordinate(co) for co in coords]
        seam = max(values) - min(values) > 2.0
        for loop_index, value in zip(polygon.loop_indices, values):
            co = mesh.vertices[mesh.loops[loop_index].vertex_index].co
            if seam and value < 1.0:
                value += 4.0
            cross = (co.z - min_z) / max(max_z - min_z, 1e-6)
            uv_layer.data[loop_index].uv = (cross, value)


def rail_profile_contours(
    dimensions: TabletopDimensions,
) -> tuple[tuple[float, float, float], ...]:
    """返回从外向内的家具截面控制点，顺序同时决定扶手环面的连接方向。"""

    return (
        (1.132, 0.054, dimensions.rail_bottom),
        (1.150, dimensions.rail_outer_corner_radius, -0.006),
        (1.148, 0.061, 0.008),
        (1.137, 0.058, 0.019),
        (1.112, 0.052, 0.028),
        (1.020, 0.040, dimensions.rail_top),
        (0.968, 0.032, 0.026),
        (0.935, 0.029, 0.016),
        (dimensions.playing_size, dimensions.rail_inner_corner_radius, 0.007),
        (
            dimensions.playing_size,
            dimensions.rail_inner_corner_radius,
            dimensions.rail_bottom,
        ),
    )


def create_profiled_rail(
    dimensions: TabletopDimensions,
    material: bpy.types.Material,
    collection: bpy.types.Collection,
) -> bpy.types.Object:
    """沿圆角周界扫掠家具截面，创建连续扶手环及稳定的截面/周界 UV。"""

    # 截面按外到内有序排列，最后一个轮廓从底面闭合到第一个轮廓，
    # 从而避免导出后出现开放边或碰撞漏面。
    contours = rail_profile_contours(dimensions)
    loops = [
        rounded_perimeter(
            size,
            size,
            radius,
            z,
            dimensions.rail_corner_segments,
        )
        for size, radius, z in contours
    ]
    point_count = len(loops[0])
    vertices = [point for loop in loops for point in loop]
    faces: list[tuple[int, int, int, int]] = []
    for profile_index in range(len(loops)):
        following_profile = (profile_index + 1) % len(loops)
        for point_index in range(point_count):
            following_point = (point_index + 1) % point_count
            faces.append(
                (
                    profile_index * point_count + point_index,
                    profile_index * point_count + following_point,
                    following_profile * point_count + following_point,
                    following_profile * point_count + point_index,
                )
            )

    mesh = bpy.data.meshes.new(f"{WOOD_OBJECT_NAME}_RailMesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.materials.append(material)
    mesh.validate()
    mesh.update(calc_edges=True)
    obj = bpy.data.objects.new("RoundedWalnut_UpperRail", mesh)
    collection.objects.link(obj)
    recalculate_normals(obj)
    add_weighted_smoothing(obj)

    uv_layer = mesh.uv_layers.new(name="UVMap")
    profile_denominator = max(len(loops) - 1, 1)
    for polygon in mesh.polygons:
        point_indices = [
            mesh.loops[loop_index].vertex_index % point_count
            for loop_index in polygon.loop_indices
        ]
        seam = 0 in point_indices and point_count - 1 in point_indices
        for loop_index in polygon.loop_indices:
            vertex_index = mesh.loops[loop_index].vertex_index
            profile_index = vertex_index // point_count
            point_index = vertex_index % point_count
            perimeter = point_index / point_count * 4.0
            if seam and point_index == 0:
                perimeter = 4.0
            uv_layer.data[loop_index].uv = (
                profile_index / profile_denominator,
                perimeter,
            )
    obj["construction"] = "rounded_profiled_rails_with_visible_corner_joinery"
    obj["outer_size_mm"] = 1150.0
    obj["inner_opening_mm"] = dimensions.playing_size * 1000.0
    return obj


def create_joinery_seams(
    dimensions: TabletopDimensions,
    material: bpy.types.Material,
    collection: bpy.types.Collection,
) -> bpy.types.Object:
    """在四个圆角附近生成细窄木工接缝；接缝独立成物体以保留材质控制。"""

    contours = rail_profile_contours(dimensions)
    half_width = dimensions.seam_width * 0.5
    seam_offset = dimensions.size * 0.5 - dimensions.seam_distance_from_corner
    positions = (-seam_offset, seam_offset)
    vertices: list[tuple[float, float, float]] = []
    faces: list[tuple[int, int, int, int]] = []

    def add_quad(points: tuple[tuple[float, float, float], ...]) -> None:
        """写入双面四边形，使极窄接缝在不同剔除设置下都可审查。"""

        start = len(vertices)
        vertices.extend(points)
        face = (start, start + 1, start + 2, start + 3)
        faces.append(face)
        faces.append(tuple(reversed(face)))

    # 窄带严格贴合扶手截面，只抬高 0.12 mm，目标是浅木工缝而非黑色装饰条。
    surface_bias = 0.00012
    for side in (-1.0, 1.0):
        for position in positions:
            for first, second in zip(contours[1:-1], contours[2:]):
                first_size, _first_radius, first_z = first
                second_size, _second_radius, second_z = second
                add_quad(
                    (
                        (
                            position - half_width,
                            side * first_size * 0.5,
                            first_z + surface_bias,
                        ),
                        (
                            position + half_width,
                            side * first_size * 0.5,
                            first_z + surface_bias,
                        ),
                        (
                            position + half_width,
                            side * second_size * 0.5,
                            second_z + surface_bias,
                        ),
                        (
                            position - half_width,
                            side * second_size * 0.5,
                            second_z + surface_bias,
                        ),
                    )
                )
                add_quad(
                    (
                        (
                            side * first_size * 0.5,
                            position - half_width,
                            first_z + surface_bias,
                        ),
                        (
                            side * second_size * 0.5,
                            position - half_width,
                            second_z + surface_bias,
                        ),
                        (
                            side * second_size * 0.5,
                            position + half_width,
                            second_z + surface_bias,
                        ),
                        (
                            side * first_size * 0.5,
                            position + half_width,
                            first_z + surface_bias,
                        ),
                    )
                )

    # 同一接缝向下延续到围裙侧面，避免扶手和围裙的结构语言断裂。
    apron_outer = dimensions.size * 0.5 - 0.00008
    z_bottom = dimensions.apron_bottom + dimensions.apron_bevel
    z_top = dimensions.apron_top - 0.001
    for side in (-1.0, 1.0):
        for position in positions:
            add_quad(
                (
                    (position - half_width, side * apron_outer, z_bottom),
                    (position + half_width, side * apron_outer, z_bottom),
                    (position + half_width, side * apron_outer, z_top),
                    (position - half_width, side * apron_outer, z_top),
                )
            )
            add_quad(
                (
                    (side * apron_outer, position - half_width, z_bottom),
                    (side * apron_outer, position - half_width, z_top),
                    (side * apron_outer, position + half_width, z_top),
                    (side * apron_outer, position + half_width, z_bottom),
                )
            )

    mesh = bpy.data.meshes.new(f"{JOINT_OBJECT_NAME}_Mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.materials.append(material)
    mesh.validate()
    mesh.update(calc_edges=True)
    obj = bpy.data.objects.new(JOINT_OBJECT_NAME, mesh)
    collection.objects.link(obj)
    uv_layer = mesh.uv_layers.new(name="UVMap")
    for uv in uv_layer.data:
        uv.uv = (0.5, 0.5)
    obj["joinery"] = "eight_rail_and_apron_corner_block_seams"
    obj["seam_width_mm"] = dimensions.seam_width * 1000.0
    return obj


def create_felt(
    dimensions: TabletopDimensions,
    material: bpy.types.Material,
    collection: bpy.types.Collection,
) -> bpy.types.Object:
    """创建 Z=0 的下沉绒布并改用平面 UV；返回网格供导出和边界验证。"""

    obj = create_rounded_prism(
        FELT_OBJECT_NAME,
        dimensions.playing_size,
        dimensions.playing_size,
        dimensions.felt_corner_radius,
        dimensions.felt_bottom,
        dimensions.felt_top,
        dimensions.felt_bevel,
        dimensions.felt_bevel_segments,
        material,
        collection,
        corner_segments=dimensions.rail_corner_segments,
    )
    # 绒布使用稳定平面投影，不能继承木框的环向 UV。
    mesh = obj.data
    uv_layer = mesh.uv_layers.get("UVMap") or mesh.uv_layers.new(name="UVMap")
    half = dimensions.playing_size * 0.5
    for polygon in mesh.polygons:
        for loop_index in polygon.loop_indices:
            co = mesh.vertices[mesh.loops[loop_index].vertex_index].co
            uv_layer.data[loop_index].uv = (
                (co.x + half) / dimensions.playing_size * 4.0,
                (co.y + half) / dimensions.playing_size * 4.0,
            )
    obj["surface_z_m"] = 0.0
    obj["playing_area_mm"] = "920x920"
    return obj


def join_wood_parts(parts: list[bpy.types.Object]) -> bpy.types.Object:
    """合并围裙、扶手和装饰带；调用会修改输入对象并返回唯一木框对象。"""

    bpy.ops.object.select_all(action="DESELECT")
    for part in parts:
        part.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    wood = bpy.context.active_object
    wood.name = WOOD_OBJECT_NAME
    wood.data.name = f"{WOOD_OBJECT_NAME}_Mesh"
    wood["surface"] = "polished_warm_walnut"
    wood["outer_dimensions_mm"] = "1150x1150"
    return wood


def triangulate_for_export(objects: list[bpy.types.Object]) -> None:
    """应用确定性三角化，确保 FBX/GLB 与 manifest 的三角形口径一致。"""

    for obj in objects:
        bpy.context.view_layer.objects.active = obj
        obj.select_set(True)
        modifier = obj.modifiers.new("GameReadyTriangulation", "TRIANGULATE")
        modifier.quad_method = "BEAUTY"
        modifier.ngon_method = "BEAUTY"
        bpy.ops.object.modifier_apply(modifier=modifier.name)
        obj.select_set(False)


def point_camera(camera: bpy.types.Object, target: Vector) -> None:
    """令相机或面光源的本地 -Z 轴指向目标，副作用是覆盖对象旋转。"""

    camera.rotation_euler = (target - camera.location).to_track_quat("-Z", "Y").to_euler()


def add_area_light(
    collection: bpy.types.Collection,
    name: str,
    location: tuple[float, float, float],
    energy: float,
    size: float,
    color: tuple[float, float, float],
    target: Vector,
) -> bpy.types.Object:
    """创建朝向目标的圆盘面光源，返回对象供棚拍集合持有。"""

    data = bpy.data.lights.new(name, "AREA")
    data.energy = energy
    data.shape = "DISK"
    data.size = size
    data.color = color
    obj = bpy.data.objects.new(name, data)
    collection.objects.link(obj)
    obj.location = location
    point_camera(obj, target)
    return obj


def create_studio(dimensions: TabletopDimensions, collection: bpy.types.Collection) -> None:
    """创建产品审查相机和四点灯光；展示集合不会包含在模型导出选择中。"""

    target = Vector((0.0, 0.0, -0.018))
    camera_data = bpy.data.cameras.new("MahjongTable_ProductCamera")
    camera = bpy.data.objects.new("MahjongTable_ProductCamera", camera_data)
    collection.objects.link(camera)
    camera.location = (1.90, -1.90, 0.82)
    camera_data.lens = 58.0
    camera_data.sensor_width = 36.0
    point_camera(camera, target)
    bpy.context.scene.camera = camera

    add_area_light(
        collection,
        "Studio_Key_Warm",
        (-0.70, -0.85, 1.65),
        72.0,
        1.15,
        (1.0, 0.84, 0.72),
        target,
    )
    add_area_light(
        collection,
        "Studio_Fill_Soft",
        (1.20, -0.15, 0.95),
        28.0,
        1.20,
        (0.50, 0.66, 1.0),
        target,
    )
    add_area_light(
        collection,
        "Studio_Rim_Warm",
        (0.20, 1.15, 1.25),
        68.0,
        0.85,
        (1.0, 0.68, 0.50),
        target,
    )
    add_area_light(
        collection,
        "Studio_Top_Felt",
        (0.0, 0.0, 2.1),
        18.0,
        1.0,
        (0.68, 0.87, 0.70),
        target,
    )
    camera["artist_note"] = "Editable product camera; matches supplied three-quarter reference."
    collection["artist_editable"] = True
    collection["lighting_setup"] = "four_point_product_studio"
    collection["table_dimensions_cm"] = "115x115"


def mesh_triangle_count(objects: list[bpy.types.Object]) -> int:
    """按多边形拓扑计算导出前三角形总数，只统计 MESH 对象。"""

    return sum(
        sum(max(0, len(polygon.vertices) - 2) for polygon in obj.data.polygons)
        for obj in objects
        if obj.type == "MESH"
    )


def mesh_bounds(objects: list[bpy.types.Object]) -> tuple[Vector, Vector]:
    """返回所有模型顶点在世界空间的最小/最大边界；空输入属于调用错误。"""

    points = [
        obj.matrix_world @ vertex.co
        for obj in objects
        if obj.type == "MESH"
        for vertex in obj.data.vertices
    ]
    return (
        Vector(tuple(min(point[index] for point in points) for index in range(3))),
        Vector(tuple(max(point[index] for point in points) for index in range(3))),
    )


def sha256(path: Path) -> str:
    """以 1 MiB 分块计算文件 SHA-256，避免大导出文件一次性进入内存。"""

    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def select_model(objects: list[bpy.types.Object]) -> None:
    """只选择给定模型并设置活动对象，防止相机和灯光混入导出文件。"""

    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]


def export_fbx(path: Path, objects: list[bpy.types.Object]) -> None:
    """按 Unreal 坐标和单位约定导出所选网格；目标文件会被 Blender 覆盖。"""

    select_model(objects)
    bpy.ops.export_scene.fbx(
        filepath=str(path),
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


def export_glb(path: Path, objects: list[bpy.types.Object]) -> None:
    """导出自包含 GLB 审查文件；仅包括模型集合中的所选网格。"""

    select_model(objects)
    bpy.ops.export_scene.gltf(
        filepath=str(path),
        use_selection=True,
        export_format="GLB",
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
    """写入尺寸、拓扑、PBR、棚拍和文件校验和 manifest，供导入与审计复核。"""

    minimum, maximum = mesh_bounds(objects)
    bounds = maximum - minimum
    manifest = {
        "generator": "Blender 5.1 GenerateStandardMahjongTable.py",
        "generator_version": SCRIPT_VERSION,
        "blender_version": bpy.app.version_string,
        "asset_root": ASSET_NAME,
        "asset_scope": "tabletop_only",
        "reference_style": "premium_rounded_walnut_frame_with_recessed_green_felt",
        "frame_construction": "segmented_rail_with_geometric_joinery_seams",
        "nominal_dimensions_mm": [
            dimensions.size * 1000.0,
            dimensions.size * 1000.0,
            dimensions.total_height * 1000.0,
        ],
        "measured_bounds_mm": {
            "minimum": [round(value * 1000.0, 4) for value in minimum],
            "maximum": [round(value * 1000.0, 4) for value in maximum],
            "size": [round(value * 1000.0, 4) for value in bounds],
        },
        "playing_surface_mm": [
            dimensions.playing_size * 1000.0,
            dimensions.playing_size * 1000.0,
        ],
        "playing_surface_z_mm": 0.0,
        "pivot": "playing_surface_center",
        "mesh_object_count": len(objects),
        "geometry": {
            obj.name: {
                "vertices": len(obj.data.vertices),
                "polygons": len(obj.data.polygons),
                "triangles": sum(
                    max(0, len(polygon.vertices) - 2)
                    for polygon in obj.data.polygons
                ),
                "material_slots": [slot.name for slot in obj.data.materials],
            }
            for obj in objects
        },
        "triangle_count": mesh_triangle_count(objects),
        "mobile_triangle_budget": 5000,
        "materials": [
            WOOD_MATERIAL_NAME,
            JOINT_MATERIAL_NAME,
            FELT_MATERIAL_NAME,
        ],
        "material_workflow": "PBR metallic-roughness",
        "pbr_channels": ["BaseColor", "Roughness", "Normal", "AO"],
        "dimensions_m": asdict(dimensions),
        "studio": {
            "camera": "MahjongTable_ProductCamera",
            "lights": [
                "Studio_Key_Warm",
                "Studio_Fill_Soft",
                "Studio_Rim_Warm",
                "Studio_Top_Felt",
            ],
            "artist_editable": True,
        },
        "files": [
            {
                "path": str(path.relative_to(output_dir.parents[2])).replace("\\", "/"),
                "bytes": path.stat().st_size,
                "sha256": sha256(path),
            }
            for path in generated_files
            if path.is_file()
        ],
    }
    (output_dir / "MahjongTableAssetManifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )


def main() -> None:
    """执行生成流程；只有显式开关才保存、导出或渲染，并在写入前校验边界和预算。"""

    args = arguments()
    project_root = (
        Path(args.project_root).resolve()
        if args.project_root
        else project_root_from_script()
    )
    output_dir = (
        args.output_dir.resolve()
        if args.output_dir
        else project_root / "SourceArt" / "3D" / "MahjongTable"
    )
    output_dir.mkdir(parents=True, exist_ok=True)
    texture_dir = output_dir / "Textures"

    clean_scene()
    configure_scene()
    dimensions = TabletopDimensions()
    model_collection = create_collection(MODEL_COLLECTION)
    studio_collection = create_collection(PRESENTATION_COLLECTION)
    wood_material = create_pbr_material(
        WOOD_MATERIAL_NAME,
        texture_dir,
        "Wood",
        fallback_color=(0.20, 0.045, 0.012, 1.0),
        coat_weight=0.10,
        coat_roughness=0.28,
        sheen_weight=0.0,
    )
    felt_material = create_pbr_material(
        FELT_MATERIAL_NAME,
        texture_dir,
        "Felt",
        fallback_color=(0.014, 0.17, 0.035, 1.0),
        coat_weight=0.0,
        coat_roughness=0.0,
        sheen_weight=0.03,
    )
    joint_material = create_joint_material()

    apron = create_rounded_prism(
        "RoundedWalnut_Apron",
        dimensions.size,
        dimensions.size,
        dimensions.apron_corner_radius,
        dimensions.apron_bottom,
        dimensions.apron_top,
        dimensions.apron_bevel,
        dimensions.apron_bevel_segments,
        wood_material,
        model_collection,
        corner_segments=dimensions.rail_corner_segments,
    )
    rail = create_profiled_rail(dimensions, wood_material, model_collection)
    decorative_band = create_rounded_prism(
        "RoundedWalnut_LowerReveal",
        1.136,
        1.136,
        0.050,
        dimensions.decorative_band_z - 0.005,
        dimensions.decorative_band_z + 0.005,
        0.0025,
        3,
        wood_material,
        model_collection,
        corner_segments=dimensions.rail_corner_segments,
    )
    wood = join_wood_parts([apron, rail, decorative_band])
    felt = create_felt(dimensions, felt_material, model_collection)
    joints = create_joinery_seams(dimensions, joint_material, model_collection)
    objects = [wood, joints, felt]
    triangulate_for_export(objects)
    create_studio(dimensions, studio_collection)

    root = bpy.data.objects.new(ASSET_NAME, None)
    model_collection.objects.link(root)
    root["dimensions_cm"] = "115x115"
    root["playing_surface_z_cm"] = 0.0
    root["construction"] = "segmented_rail_with_geometric_joinery_seams"
    for obj in objects:
        obj.parent = root

    minimum, maximum = mesh_bounds(objects)
    measured = maximum - minimum
    expected = Vector(
        (dimensions.size, dimensions.size, dimensions.total_height)
    )
    if any(abs(measured[index] - expected[index]) > 0.001 for index in range(3)):
        raise RuntimeError(
            "Generated bounds do not match authored dimensions: "
            f"measured={tuple(measured)} expected={tuple(expected)}"
        )
    if mesh_triangle_count(objects) >= 5000:
        raise RuntimeError("Generated tabletop exceeds the 5000 triangle budget")

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
    print(
        "MAHJONG_TABLETOP_GENERATED "
        f"blender={bpy.app.version_string} "
        f"dimensions_cm=({measured.x * 100.0:.3f},"
        f"{measured.y * 100.0:.3f},{measured.z * 100.0:.3f}) "
        f"triangles={mesh_triangle_count(objects)} objects={len(objects)}"
    )


if __name__ == "__main__":
    main()
