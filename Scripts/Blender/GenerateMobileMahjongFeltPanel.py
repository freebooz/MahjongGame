"""Generate the mobile production Mahjong felt panel in Blender 5.2.

The asset contains only a closed felt panel and a low-poly center controller.
The controller's labels and fan sectors are texture-baked for mobile rendering.
It replaces the previous framed table while preserving the Unreal asset name
expected by the project.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

import bpy
from mathutils import Quaternion, Vector


ASSET_NAME = "SM_StandardMahjongTable"
FELT_MATERIAL = "M_Table_Felt_Mobile"
FELT_EDGE_MATERIAL = "M_Table_Felt_Edge_Mobile"
GUNMETAL_MATERIAL = "M_Table_Controller_Gunmetal_Mobile"
DISPLAY_MATERIAL = "M_Table_Controller_Display_Mobile"
GLASS_MATERIAL = "M_Table_Controller_Glass_Mobile"
SIZE_M = 3.0
THICKNESS_M = 0.04
EDGE_BEVEL_M = 0.012
CONTROLLER_RADIUS_M = 0.170
TRIANGLE_BUDGET = 7000


def arguments() -> argparse.Namespace:
    argv = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project_root", type=Path)
    return parser.parse_args(argv)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def create_material(
    name: str,
    color: tuple[float, float, float, float],
    *,
    metallic: float,
    roughness: float,
) -> bpy.types.Material:
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    material.diffuse_color = color
    principled = next(
        node
        for node in material.node_tree.nodes
        if node.type == "BSDF_PRINCIPLED"
    )
    principled.inputs["Base Color"].default_value = color
    principled.inputs["Metallic"].default_value = metallic
    principled.inputs["Roughness"].default_value = roughness
    material["mobile_profile"] = "single_opaque_pbr_material"
    return material


def connect_texture_set(
    material: bpy.types.Material,
    texture_root: Path,
    set_name: str,
    suffix: str,
) -> None:
    nodes = material.node_tree.nodes
    links = material.node_tree.links
    principled = next(
        node for node in nodes if node.type == "BSDF_PRINCIPLED"
    )
    base_image = bpy.data.images.load(
        str(texture_root / f"T_{set_name}_BaseColor_{suffix}.png"),
        check_existing=True,
    )
    normal_image = bpy.data.images.load(
        str(texture_root / f"T_{set_name}_Normal_{suffix}.png"),
        check_existing=True,
    )
    orm_image = bpy.data.images.load(
        str(texture_root / f"T_{set_name}_ORM_{suffix}.png"),
        check_existing=True,
    )
    normal_image.colorspace_settings.name = "Non-Color"
    orm_image.colorspace_settings.name = "Non-Color"

    base = nodes.new("ShaderNodeTexImage")
    base.name = "MobileBaseColor"
    base.image = base_image
    base.interpolation = "Linear"
    base.location = (-680, 180)
    links.new(base.outputs["Color"], principled.inputs["Base Color"])

    normal = nodes.new("ShaderNodeTexImage")
    normal.name = "MobileNormal"
    normal.image = normal_image
    normal.interpolation = "Linear"
    normal.location = (-680, -40)
    normal_map = nodes.new("ShaderNodeNormalMap")
    normal_map.inputs["Strength"].default_value = 0.58
    normal_map.location = (-380, -40)
    links.new(normal.outputs["Color"], normal_map.inputs["Color"])
    links.new(normal_map.outputs["Normal"], principled.inputs["Normal"])

    orm = nodes.new("ShaderNodeTexImage")
    orm.name = "MobileORM"
    orm.image = orm_image
    orm.interpolation = "Linear"
    orm.location = (-680, -280)
    separate = nodes.new("ShaderNodeSeparateColor")
    separate.location = (-380, -280)
    links.new(orm.outputs["Color"], separate.inputs["Color"])
    links.new(separate.outputs["Green"], principled.inputs["Roughness"])
    links.new(separate.outputs["Blue"], principled.inputs["Metallic"])


def create_materials(
    texture_root: Path,
) -> dict[str, bpy.types.Material]:
    felt = create_material(
        FELT_MATERIAL,
        (0.006, 0.030, 0.018, 1.0),
        metallic=0.0,
        roughness=0.86,
    )
    principled = next(
        node
        for node in felt.node_tree.nodes
        if node.type == "BSDF_PRINCIPLED"
    )
    sheen = principled.inputs.get("Sheen Weight")
    if sheen:
        sheen.default_value = 0.05
    connect_texture_set(
        felt,
        texture_root,
        "TableFeltMobileDeepForest",
        "8K",
    )
    felt_edge = create_material(
        FELT_EDGE_MATERIAL,
        (0.004, 0.016, 0.010, 1.0),
        metallic=0.0,
        roughness=0.90,
    )

    gunmetal = create_material(
        GUNMETAL_MATERIAL,
        (0.008, 0.010, 0.012, 1.0),
        metallic=0.80,
        roughness=0.24,
    )
    display = create_material(
        DISPLAY_MATERIAL,
        (0.008, 0.006, 0.004, 1.0),
        metallic=0.28,
        roughness=0.20,
    )
    connect_texture_set(
        display,
        texture_root,
        "TableControllerDirectionDisplayMobile",
        "512",
    )
    glass = create_material(
        GLASS_MATERIAL,
        (0.18, 0.25, 0.28, 0.12),
        metallic=0.0,
        roughness=0.025,
    )
    glass_principled = next(
        node
        for node in glass.node_tree.nodes
        if node.type == "BSDF_PRINCIPLED"
    )
    transmission = glass_principled.inputs.get("Transmission Weight")
    if transmission:
        transmission.default_value = 0.96
    alpha = glass_principled.inputs.get("Alpha")
    if alpha:
        alpha.default_value = 0.12
    ior = glass_principled.inputs.get("IOR")
    if ior:
        ior.default_value = 1.46
    try:
        glass.surface_render_method = "BLENDED"
    except (AttributeError, TypeError):
        pass
    return {
        FELT_MATERIAL: felt,
        FELT_EDGE_MATERIAL: felt_edge,
        GUNMETAL_MATERIAL: gunmetal,
        DISPLAY_MATERIAL: display,
        GLASS_MATERIAL: glass,
    }


def triangulate_object(obj: bpy.types.Object) -> None:
    bpy.context.view_layer.objects.active = obj
    triangulate = obj.modifiers.new("ProductionTriangulate", "TRIANGULATE")
    triangulate.keep_custom_normals = True
    bpy.ops.object.modifier_apply(modifier=triangulate.name)


def create_panel(material: bpy.types.Material) -> bpy.types.Object:
    bpy.ops.mesh.primitive_cube_add(
        size=1.0,
        location=(0.0, 0.0, -THICKNESS_M * 0.5),
    )
    panel = bpy.context.object
    panel.name = ASSET_NAME
    panel.dimensions = (SIZE_M, SIZE_M, THICKNESS_M)
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    panel.data.name = f"{ASSET_NAME}_Mesh"
    panel.data.materials.append(material)
    panel.data.materials.append(bpy.data.materials[FELT_EDGE_MATERIAL])

    bevel = panel.modifiers.new("MobileEdgeBevel", "BEVEL")
    bevel.width = EDGE_BEVEL_M
    bevel.segments = 2
    bevel.limit_method = "ANGLE"
    bpy.context.view_layer.objects.active = panel
    bpy.ops.object.modifier_apply(modifier=bevel.name)

    for polygon in panel.data.polygons:
        polygon.use_smooth = True

    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.normals_make_consistent(inside=False)
    bpy.ops.object.mode_set(mode="OBJECT")

    triangulate_object(panel)
    uv_layer = panel.data.uv_layers.get("UVMap")
    if uv_layer is None:
        uv_layer = panel.data.uv_layers.new(name="UVMap")
    for loop in panel.data.loops:
        vertex = panel.data.vertices[loop.vertex_index].co
        uv_layer.data[loop.index].uv = (
            vertex.x / SIZE_M + 0.5,
            vertex.y / SIZE_M + 0.5,
        )
    for polygon in panel.data.polygons:
        polygon.material_index = 0 if polygon.normal.z > 0.98 else 1

    panel["asset_role"] = "mobile_mahjong_felt_panel_only"
    panel["outer_dimensions_mm"] = "3000x3000"
    panel["frame_removed"] = True
    panel["controller_retained"] = True
    panel["textured_surfaces"] = "top_flat_surface_only"
    panel["side_bottom_material"] = FELT_EDGE_MATERIAL
    panel["mobile_triangle_budget"] = TRIANGLE_BUDGET
    return panel


def create_controller_part(
    name: str,
    radius: float,
    depth: float,
    z_center: float,
    material: bpy.types.Material,
    *,
    vertices: int = 48,
    bevel_width: float = 0.0,
) -> bpy.types.Object:
    bpy.ops.mesh.primitive_cylinder_add(
        vertices=vertices,
        radius=radius,
        depth=depth,
        end_fill_type="NGON",
        calc_uvs=True,
        location=(0.0, 0.0, z_center),
    )
    obj = bpy.context.object
    obj.name = name
    obj.data.name = f"{name}_Mesh"
    obj.data.materials.append(material)
    if bevel_width > 0.0:
        bevel = obj.modifiers.new("MobileEdgeBevel", "BEVEL")
        bevel.width = bevel_width
        bevel.segments = 1
        bevel.limit_method = "ANGLE"
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.modifier_apply(modifier=bevel.name)
    for polygon in obj.data.polygons:
        polygon.use_smooth = True
    triangulate_object(obj)
    uv_layer = obj.data.uv_layers.get("UVMap")
    if uv_layer is None:
        uv_layer = obj.data.uv_layers.new(name="UVMap")
    for loop in obj.data.loops:
        vertex = obj.data.vertices[loop.vertex_index].co
        uv_layer.data[loop.index].uv = (
            vertex.x / (2.0 * radius) + 0.5,
            vertex.y / (2.0 * radius) + 0.5,
        )
    obj["asset_role"] = "mobile_center_controller"
    return obj


def create_controller(
    materials: dict[str, bpy.types.Material],
) -> list[bpy.types.Object]:
    base = create_controller_part(
        "Controller_GunmetalBase",
        CONTROLLER_RADIUS_M,
        0.012,
        0.006,
        materials[GUNMETAL_MATERIAL],
        bevel_width=0.0015,
    )
    display = create_controller_part(
        "Controller_DirectionDisplay",
        0.158,
        0.004,
        0.014,
        materials[DISPLAY_MATERIAL],
        bevel_width=0.0008,
    )
    glass = create_controller_part(
        "Controller_GlassCover",
        0.162,
        0.002,
        0.018,
        materials[GLASS_MATERIAL],
        bevel_width=0.0006,
    )
    base["direction_labels"] = "北东南西"
    base["display_content"] = "directions_only_no_numbers"
    base["sector_layout"] = "four_direction_radial_fan_baked"
    base["glass_cover"] = "small_mobile_translucent_surface"
    return [base, display, glass]


def validate(objects: list[bpy.types.Object]) -> int:
    panel = next(obj for obj in objects if obj.name == ASSET_NAME)
    if abs(panel.dimensions.x - SIZE_M) > 0.0001:
        raise RuntimeError(f"Unexpected X dimension: {panel.dimensions.x}")
    if abs(panel.dimensions.y - SIZE_M) > 0.0001:
        raise RuntimeError(f"Unexpected Y dimension: {panel.dimensions.y}")
    if len(panel.data.uv_layers) != 1:
        raise RuntimeError(
            f"Expected one panel UV map, found {len(panel.data.uv_layers)}"
        )
    if [material.name for material in panel.data.materials] != [
        FELT_MATERIAL,
        FELT_EDGE_MATERIAL,
    ]:
        raise RuntimeError("Unexpected felt panel material-slot order")
    textured_polygons = [
        polygon
        for polygon in panel.data.polygons
        if polygon.material_index == 0
    ]
    untextured_polygons = [
        polygon
        for polygon in panel.data.polygons
        if polygon.material_index == 1
    ]
    if not textured_polygons or not untextured_polygons:
        raise RuntimeError("Felt surface material split is incomplete")
    if any(polygon.normal.z <= 0.98 for polygon in textured_polygons):
        raise RuntimeError("8K felt texture leaked onto a side or bottom face")
    if any(polygon.normal.z > 0.98 for polygon in untextured_polygons):
        raise RuntimeError("Top felt face is using the untextured edge material")
    if any(len(obj.data.uv_layers) < 1 for obj in objects):
        raise RuntimeError("One or more export meshes have no UV map")
    triangles = sum(len(obj.data.polygons) for obj in objects)
    if triangles <= 0 or triangles > TRIANGLE_BUDGET:
        raise RuntimeError(
            f"Triangle budget exceeded: {triangles} > {TRIANGLE_BUDGET}"
        )
    return triangles


def configure_review_viewport(objects: list[bpy.types.Object]) -> int:
    configured = 0
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type != "VIEW_3D":
                continue
            space = area.spaces.active
            space.shading.type = "MATERIAL"
            space.shading.light = "STUDIO"
            space.shading.studiolight_rotate_z = 0.35
            space.shading.studiolight_background_alpha = 0.25
            space.overlay.show_floor = False
            space.overlay.show_axis_x = False
            space.overlay.show_axis_y = False
            space.overlay.show_axis_z = False
            region = space.region_3d
            region.view_location = Vector((0.0, 0.0, 0.0))
            region.view_rotation = Quaternion((1.0, 0.0, 0.0, 0.0))
            region.view_distance = 0.46
            region.view_perspective = "ORTHO"
            configured += 1
    if configured == 0:
        raise RuntimeError("No Blender 3D viewport was available to configure")
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(obj.name.startswith("Controller_"))
    bpy.context.view_layer.objects.active = bpy.data.objects.get(
        "Controller_DirectionDisplay"
    )
    bpy.context.scene["review_viewport"] = (
        "material_preview_controller_top_closeup"
    )
    return configured


def export(
    output_dir: Path,
    objects: list[bpy.types.Object],
) -> list[Path]:
    blend_path = output_dir / f"{ASSET_NAME}.blend"
    fbx_path = output_dir / f"{ASSET_NAME}.fbx"
    glb_path = output_dir / f"{ASSET_NAME}.glb"

    bpy.ops.wm.save_as_mainfile(
        filepath=str(blend_path),
        check_existing=False,
    )
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.export_scene.fbx(
        filepath=str(fbx_path),
        use_selection=True,
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        object_types={"MESH"},
        use_mesh_modifiers=True,
        mesh_smooth_type="FACE",
        add_leaf_bones=False,
        bake_anim=False,
        axis_forward="-Z",
        axis_up="Y",
        path_mode="AUTO",
    )
    bpy.ops.export_scene.gltf(
        filepath=str(glb_path),
        use_selection=True,
        export_format="GLB",
        export_apply=True,
        export_materials="EXPORT",
        export_yup=True,
    )
    return [blend_path, fbx_path, glb_path]


def main() -> None:
    args = arguments()
    project_root = args.project_root.resolve()
    output_dir = (
        project_root / "SourceArt" / "3D" / "MahjongTableMobileProduction"
    )
    output_dir.mkdir(parents=True, exist_ok=True)

    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for datablocks in (
        bpy.data.meshes,
        bpy.data.curves,
        bpy.data.materials,
        bpy.data.cameras,
        bpy.data.lights,
    ):
        for datablock in list(datablocks):
            if datablock.users == 0:
                datablocks.remove(datablock)

    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0
    scene["production_target"] = "mobile"
    scene["triangle_budget_lod0"] = TRIANGLE_BUDGET

    texture_root = output_dir / "Textures"
    materials = create_materials(texture_root)
    panel = create_panel(materials[FELT_MATERIAL])
    controller = create_controller(materials)
    objects = [panel, *controller]
    triangles = validate(objects)
    configured_viewports = configure_review_viewport(objects)
    files = export(output_dir, objects)

    manifest = {
        "status": "approved_for_unreal_import",
        "generator": Path(__file__).name,
        "blender_version": bpy.app.version_string,
        "design": "felt_panel_and_low_poly_center_controller_no_frame",
        "outer_size_mm": [3000.0, 3000.0],
        "height_mm": 59.0,
        "mesh_object_count": len(objects),
        "configured_review_viewports": configured_viewports,
        "triangle_budget": TRIANGLE_BUDGET,
        "triangle_count": triangles,
        "materials": list(materials),
        "uvless_objects": [
            obj.name for obj in objects if not obj.data.uv_layers
        ],
        "mobile_profile": {
            "nanite": False,
            "material_slots": len(materials),
            "felt_texture_resolution": 8192,
            "controller_texture_resolution": 512,
            "baked_direction_labels": True,
        },
        "files": [
            {
                "name": path.name,
                "bytes": path.stat().st_size,
                "sha256": sha256(path),
            }
            for path in files
        ],
    }
    manifest_path = output_dir / "MahjongTableMobileProductionManifest.json"
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    print(
        "MOBILE_MAHJONG_FELT_PANEL_OK",
        f"blender={bpy.app.version_string}",
        "size_mm=(3000.000,3000.000,40.000)",
        f"triangles={triangles}",
        f"materials={len(materials)}",
        f"objects={len(objects)}",
        f"manifest={manifest_path}",
    )


if __name__ == "__main__":
    main()
