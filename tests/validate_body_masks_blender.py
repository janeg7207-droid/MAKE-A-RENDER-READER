"""Verify common body-hide masks on the real supplied complete Bobby model.

Run with Blender --background --factory-startup --python-exit-code1 --python
this_file -- --application PATH --character PATH --output PATH.
"""
import argparse
import array
import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile

import bpy


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--application", required=True, type=Path)
    parser.add_argument("--character", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    app, root = args.application.resolve(), args.character.resolve()
    os.environ["GS_ROOT"] = str(app)
    sys.path[:0] = [str(Path(__file__).resolve().parents[1] / "src/renderer"), str(app / "engine/blender")]
    import gorilla_geometry as geometry
    import gorilla_body_masks as masks
    import gorilla_blender
    sources = (root / "BaseModel/baseModel.mcd", root / "287_Default_Attire/Attire.mcd")
    before = {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in sources}
    checks = 0
    def check(condition, message):
        nonlocal checks
        if not condition:
            raise AssertionError(message)
        checks += 1
    def values(items, key, width, typecode):
        result = array.array(typecode, [0]) * (len(items) * width)
        items.foreach_get(key, result)
        return result.tobytes()
    def signature(obj):
        mesh = obj.data
        shapes = () if mesh.shape_keys is None else tuple((key.name, key.value,
                  hashlib.sha256(values(key.data, "co", 3, "f")).hexdigest()) for key in mesh.shape_keys.key_blocks)
        return (hashlib.sha256(values(mesh.vertices, "co", 3, "f") + values(mesh.loops, "vertex_index", 1, "i")).hexdigest(),
                tuple((uv.name, hashlib.sha256(values(uv.data, "uv", 2, "f")).hexdigest()) for uv in mesh.uv_layers),
                tuple(material.as_pointer() if material else None for material in mesh.materials),
                tuple((group.name, group.index) for group in obj.vertex_groups),
                tuple(tuple((group.group, group.weight) for group in vertex.groups) for vertex in mesh.vertices), shapes)
    def find_body():
        return next(obj for obj in bpy.context.scene.objects if obj.type == "MESH"
                    and (geometry.read_provenance(obj) or {}).get("role") == "base"
                    and geometry.read_provenance(obj)["name"] == "M_Body")
    options = {"char_dir": str(root), "attire_mcd": "287_Default_Attire/Attire.mcd", "gorilla_gear": "ring"}
    geometry_state = geometry.register(options)
    state = masks.register(options, geometry_state)
    original_callback = state.previous_callback
    base = gorilla_blender.import_mcd(sources[0], shape_keys=True, textures=False)
    attire = gorilla_blender.import_mcd(sources[1], shape_keys=True, textures=False)
    check(set(state.records) == {"base", "attire"}, "Both real native ImportRecords are captured")
    body = find_body()
    original_mesh = body.data
    original_signature = signature(body)
    unrelated = bpy.data.objects.new("Unrelated shared Bobby body", original_mesh)
    bpy.context.scene.collection.objects.link(unrelated)
    all_originals = {obj.name: signature(obj) for obj in tuple(base[1:]) + tuple(attire[1:])}
    source = state.records["base"].model
    source_body_index = geometry.read_provenance(body)["index"]
    source_faces = tuple(tuple(map(int, face)) for face in source.lods[0][source_body_index][0])
    mask_ids = masks.common_masks(state.records["attire"].model,
                     {mesh["name"]: mesh["numbering"] for mesh in source.meshes})["M_Body"]
    selection = masks.masked_faces(geometry.decode_remap(body), source_faces, mask_ids)
    check(len(mask_ids) == 6251 and sum(selection) == 11091, "Actual common mask matches independently audited counts")
    with tempfile.TemporaryDirectory(prefix="gorilla-bobby-masks-") as temporary:
        neutral, applied = Path(temporary) / "neutral.blend", Path(temporary) / "applied.blend"
        bpy.ops.wm.save_as_mainfile(filepath=str(neutral), check_existing=False, compress=True)
        stats = state.apply(bpy.context.scene)
        check(stats["masked_objects"] == 1 and stats["masked_faces"] == 11091 and stats["skipped"] == 0,
              "Real base/attire pairing installs only the common body mask")
        check(body.data is not original_mesh and unrelated.data is original_mesh,
              "Private mesh isolates unrelated objects sharing the original datablock")
        check(signature(body) == original_signature, "Original coordinates/topology/UVs/weights/materials/shape keys survive")
        check(all(signature(obj) == all_originals[obj.name] for obj in tuple(base[1:]) + tuple(attire[1:])),
              "Every other native body and attire mesh is unchanged")
        check(unrelated.data.attributes.get(masks.ATTRIBUTE) is None and not unrelated.modifiers,
              "The unrelated shared mesh receives no attribute or modifier")
        check(tuple(item.value for item in body.data.attributes[masks.ATTRIBUTE].data) == selection,
              "Only all-corners common-ID source faces receive mask attributes")
        depsgraph = bpy.context.evaluated_depsgraph_get()
        evaluated = body.evaluated_get(depsgraph)
        expected_faces = tuple(face for face, hide in zip(source_faces, selection) if not hide)
        check(tuple(tuple(poly.vertices) for poly in evaluated.data.polygons) == expected_faces,
              "Evaluated geometry removes exactly the validated source faces")
        check(len(evaluated.data.vertices) == len(body.data.vertices) == 12755,
              "ONLY_FACE evaluation retains all original vertices")
        check(len(body.data.polygons) == 23168 and len(evaluated.data.polygons) == 12077,
              "Original topology remains23168; evaluated output contains12077 faces")
        original_model = state._model
        def forbidden(*args, **kwargs):
            raise AssertionError("Per-frame model IO was repeated")
        state._model = forbidden
        try:
            for _ in range(20):
                state.apply(bpy.context.scene)
        finally:
            state._model = original_model
        check(signature(body) == original_signature, "Repeated frame callbacks do no IO and preserve originals")
        options["gorilla_gear"] = "entrance"
        geometry_state.options["gorilla_gear"] = "entrance"
        state.apply(bpy.context.scene)
        check(tuple(item.value for item in body.data.attributes[masks.ATTRIBUTE].data) == selection,
              "Entrance/ring selection never guesses HPL DATA meanings")
        bpy.ops.wm.save_as_mainfile(filepath=str(applied), check_existing=False, compress=True)
        bpy.ops.wm.open_mainfile(filepath=str(applied))
        body = find_body()
        check(not state.prepared and not state.records, "Persistent cache load clears per-scene preparation")
        stats = state.apply(bpy.context.scene)
        check(stats["reused_objects"] == 1 and stats["masked_objects"] == 0 and stats["skipped"] == 0,
              "Applied cached scene validates and reuses exactly one managed modifier")
        check(len([mod for mod in body.modifiers if mod.type == "NODES"]) == 1,
              "Reopening a masked cache cannot stack mask modifiers")
        managed = next(mod for mod in body.modifiers if mod.type == "NODES")
        next(node for node in managed.node_group.nodes if node.bl_idname == "GeometryNodeDeleteGeometry").mode = "ALL"
        state.prepared = False
        stats = state.apply(bpy.context.scene)
        check(stats["skipped"] == 1, "A changed cached node operation is rejected")
        check(not managed.show_render and not managed.show_viewport,
              "Rejected helper-owned nodes are disabled for render and viewport")
        depsgraph = bpy.context.evaluated_depsgraph_get()
        depsgraph.update()
        evaluated = body.evaluated_get(depsgraph)
        check(len(evaluated.data.vertices) == 12755 and len(evaluated.data.polygons) == 23168,
              "A rejected ALL operation cannot keep deleting evaluated source vertices")
        bpy.ops.wm.open_mainfile(filepath=str(applied))
        body = find_body()
        managed = next(mod for mod in body.modifiers if mod.type == "NODES")
        next(node for node in managed.node_group.nodes if node.bl_idname == "GeometryNodeDeleteGeometry").mute = True
        stats = state.apply(bpy.context.scene)
        check(stats["skipped"] == 1 and not managed.show_render,
              "Muted cached mask nodes cannot claim a successfully reused repair")
        bpy.ops.wm.open_mainfile(filepath=str(applied))
        body = find_body()
        body["gorilla_geometry_source_sha256"] = "0" * 64
        stats = state.apply(bpy.context.scene)
        check(stats["skipped"] == 1 and all(not mod.show_render for mod in body.modifiers if mod.type == "NODES"),
              "Stale cached source proof also disables a previously valid managed mask")
        bpy.ops.wm.open_mainfile(filepath=str(neutral))
        body = find_body()
        for callback in list(bpy.app.handlers.render_pre):
            if getattr(callback, "_gorilla_body_masks", False):
                callback(bpy.context.scene)
        check(state.stats["masked_faces"] == 11091 and state.stats["skipped"] == 0,
              "Legacy unmasked cached blend acquires the validated repair through persistent render_pre")
        bpy.ops.wm.open_mainfile(filepath=str(neutral))
        body = find_body()
        body["gorilla_geometry_source_sha256"] = "0" * 64
        stats = state.apply(bpy.context.scene)
        check(stats["masked_objects"] == 0 and stats["skipped"] == 1 and not any(m.type == "NODES" for m in body.modifiers),
              "Stale provenance leaves the entire source pair unmodified")
        bpy.ops.wm.open_mainfile(filepath=str(neutral))
        body = find_body()
        duplicate = body.copy()
        bpy.context.scene.collection.objects.link(duplicate)
        stats = state.apply(bpy.context.scene)
        check(stats["masked_objects"] == 0 and stats["skipped"] == 1,
              "Duplicate source identities are rejected without arbitrary object choice")
        bpy.ops.wm.open_mainfile(filepath=str(neutral))
        body = find_body()
        body["gorilla_geometry_mesh_name"] = "M_Head"
        stats = state.apply(bpy.context.scene)
        check(stats["masked_objects"] == 0 and stats["skipped"] == 1,
              "A source mesh-name mismatch cannot acquire another mesh's mask")
        bpy.ops.wm.open_mainfile(filepath=str(neutral))
        body = find_body()
        body.data.loops[0].vertex_index = (body.data.loops[0].vertex_index + 1) % len(body.data.vertices)
        stats = state.apply(bpy.context.scene)
        check(stats["masked_objects"] == 0 and stats["skipped"] == 1,
              "Changed source topology is rejected before any mask is installed")
        state.unregister()
        check(geometry_state.on_import is original_callback, "Unregister restores previous import callback")
        check(not any(getattr(cb, "_gorilla_body_masks", False) for cb in bpy.app.handlers.render_pre),
              "Unregister removes its persistent frame callback")
    check(all(hashlib.sha256(path.read_bytes()).hexdigest() == before[str(path)] for path in sources),
          "Both uploaded original MCD files remain byte-identical")
    report = {"checks_passed": checks, "blender_version": bpy.app.version_string, "native_import_executed": True,
              "source_character": str(root), "masked_source_mesh": "M_Body", "common_remap_ids": len(mask_ids),
              "source_faces": 23168, "evaluated_faces": 12077, "render_only_faces_hidden": 11091,
              "cached_scene_apply_and_reuse_verified": True, "source_files_unchanged": True,
              "original_positions_UVs_weights_materials_shape_keys_unchanged": True,
              "gear_specific_HPL_mapping_enabled": False, "image_render_executed": False,
              "YCL_simulation_used": False,
              "limitations": ["No global HPL DATA-to-gear mapping is assumed.",
                              "Mode-specific shirt and collar masks remain unchanged.",
                              "No cloth/hair simulation, collision inference, or skin-weight repair is performed.",
                              "The supplied Bobby YCL has no simulated objects according to the separate YCL audit."],
              "Adam_Cole_model_tested": False, "Windows_game_executed": False}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    print("PASS", checks, "actual Bobby body-mask checks")


if __name__ == "__main__":
    main()
