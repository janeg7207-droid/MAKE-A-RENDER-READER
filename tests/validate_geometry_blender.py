"""Exercise geometry identity/visibility through the real native Blender importer.

Run Blender in factory-startup background mode with this file as --python, then
pass --application, --discovery and --output after --. No image render or Windows
application/game process is started, and uploaded source files remain unchanged.
"""

import argparse
import array
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile

import bpy


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--application", required=True, type=Path)
    parser.add_argument("--discovery", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--module", type=Path, default=Path(__file__).resolve().parents[1] / "src/renderer/gorilla_geometry.py")
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    app = args.application.resolve()
    os.environ["GS_ROOT"] = str(app)
    sys.path.insert(0, str(app / "engine/blender"))
    import gorilla_blender
    spec = importlib.util.spec_from_file_location("gorilla_geometry", args.module)
    geometry = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(geometry)
    sources = sorted((args.discovery / "Characters").rglob("*.mcd"))
    before = {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in sources}
    checks = 0
    records = []

    def check(condition, message):
        nonlocal checks
        if not condition:
            raise AssertionError(message)
        checks += 1

    def signature(obj):
        mesh = obj.data
        position = array.array("f", [0.0]) * (len(mesh.vertices) * 3)
        corner = array.array("i", [0]) * len(mesh.loops)
        mesh.vertices.foreach_get("co", position)
        mesh.loops.foreach_get("vertex_index", corner)
        shapes = () if mesh.shape_keys is None else tuple((item.name, item.value) for item in mesh.shape_keys.key_blocks)
        return (hashlib.sha256(position.tobytes() + corner.tobytes()).hexdigest(),
                tuple(material.as_pointer() if material else 0 for material in mesh.materials),
                tuple((m.name, m.type, m.object.as_pointer() if m.type == "ARMATURE" and m.object else None) for m in obj.modifiers),
                tuple(group.name for group in obj.vertex_groups), shapes)

    def clean_scene():
        for obj in list(bpy.data.objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        for mesh in list(bpy.data.meshes):
            if mesh.users == 0:
                bpy.data.meshes.remove(mesh)
        for arm in list(bpy.data.armatures):
            if arm.users == 0:
                bpy.data.armatures.remove(arm)

    check(len(sources) == 10, "The actual ten supplied wrestler attire models are present")
    native = gorilla_blender.import_mcd
    native_reader = gorilla_blender.GM.Model
    first_options = None
    state = None
    with tempfile.TemporaryDirectory(prefix="gorilla-geometry-blender-") as temporary:
        cache = Path(temporary) / "model.blend"
        for index, path in enumerate(sources):
            clean_scene()
            options = {"char_dir": str(path.parent.parent), "attire_mcd": str(path.relative_to(path.parent.parent)), "gorilla_gear": "ring"}
            imported = []
            state = geometry.register(options, imported.append)
            result = gorilla_blender.import_mcd(path, shape_keys=True, textures=False)
            check(len(imported) == 1, path.name + ": the real importer captures exactly one model")
            record = imported[0]
            objects = tuple(result[1:])
            check(result[0].type == "ARMATURE" and len(objects) == len(record.model.meshes), "Source order includes the armature and every mesh")
            check(record.sha256 == before[str(path)] and record.role == "attire", "Exact source bytes and attire role are established")
            check(gorilla_blender.GM.Model is native_reader, "Shared native Model is not replaced")
            for mi, obj in enumerate(objects):
                proof = geometry.read_provenance(obj)
                check(proof is not None and proof["index"] == mi and proof["name"] == record.model.meshes[mi]["name"], "Each real mesh keeps its exact source index/name")
                check(geometry.decode_remap(obj) == tuple(map(int, record.model.meshes[mi]["numbering"])), "Real uint32 REMAP values are unchanged")
            check(all(not obj.hide_render for obj in objects), "Import provenance keeps model preparation gear-neutral")
            known_active = next((obj for obj in objects if geometry.read_provenance(obj)["flags"] == 128), None)
            if known_active is not None:
                known_active.hide_render = True
            if index == 0:
                first_options = dict(options)
                bpy.ops.wm.save_as_mainfile(filepath=str(cache), check_existing=False)
            originals = {obj.as_pointer(): signature(obj) for obj in objects}
            candidate = next((obj for obj in objects if geometry.read_provenance(obj)["flags"] == 129), objects[0])
            # An unrelated later object deliberately shares all mesh geometry,
            # material identity and counts. It must not acquire attire visibility.
            unrelated = bpy.data.objects.new(candidate.name, candidate.data)
            bpy.context.scene.collection.objects.link(unrelated)
            check(unrelated.data is candidate.data and not unrelated.hide_render, "Unrelated duplicate shares the real source datablock")
            state.apply_visibility(bpy.context.scene)
            check(all(obj.hide_render for obj in objects if geometry.read_provenance(obj)["flags"] == 129), "Ring mode hides only proven entrance-only pieces")
            check(all(not obj.hide_render for obj in objects if geometry.read_provenance(obj)["flags"] == 130), "Ring-only pieces remain visible in ring mode")
            check(not unrelated.hide_render and geometry.read_provenance(unrelated) is None, "Later duplicate is not confused with an attire piece")
            check(known_active is None or known_active.hide_render, "An existing manual hide is preserved")
            check(all(not obj.hide_render for obj in objects if geometry.read_provenance(obj)["flags"] not in (128, 129, 130)), "Unknown real flags are left untouched")
            check(all(signature(obj) == originals[obj.as_pointer()] for obj in objects), "Visibility preserves positions, topology, materials, shape keys, groups and modifiers")
            # A frame pass must never reparse files, hash model bytes or decode
            # complete REMAP arrays while the animation is playing.
            original_hash, original_model = geometry.hashlib.sha256, gorilla_blender.GM.Model
            original_decode = geometry.decode_remap
            def forbidden(*unused_args, **unused_kwargs):
                raise AssertionError("A frame visibility pass repeated model preparation")
            geometry.hashlib.sha256 = forbidden
            gorilla_blender.GM.Model = forbidden
            geometry.decode_remap = forbidden
            try:
                for _ in range(12):
                    state.apply_visibility(bpy.context.scene)
            finally:
                geometry.hashlib.sha256 = original_hash
                gorilla_blender.GM.Model = original_model
                geometry.decode_remap = original_decode
            check(all(signature(obj) == originals[obj.as_pointer()] for obj in objects), "Repeated frame passes preserve every original mesh")
            state.options["gorilla_gear"] = "entrance"
            state.apply_visibility(bpy.context.scene)
            check(all(not obj.hide_render for obj in objects if geometry.read_provenance(obj)["flags"] == 129), "A mode change restores only repair-owned entrance pieces")
            check(all(obj.hide_render for obj in objects if geometry.read_provenance(obj)["flags"] == 130), "Entrance mode hides the proven ring-only pieces")
            check(not unrelated.hide_render and (known_active is None or known_active.hide_render), "Mode changes preserve unrelated and manually hidden objects")
            records.append({"source": str(path), "source_sha256": record.sha256, "meshes": len(objects),
                            "flags": sorted(set(mesh["flags"] for mesh in record.model.meshes)),
                            "verified_entrance_only": sum(mesh["flags"] == 129 for mesh in record.model.meshes),
                            "verified_ring_only": sum(mesh["flags"] == 130 for mesh in record.model.meshes)})
        state = geometry.register(first_options)
        bpy.ops.wm.open_mainfile(filepath=str(cache))
        reopened = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
        check(reopened and all(geometry.read_provenance(obj) is not None for obj in reopened), "Native prepared blend retains exact provenance after reopen")
        check(all(not obj.hide_render for obj in reopened if geometry.read_provenance(obj)["flags"] in (129, 130)), "Prepared cache stays neutral for entrance/ring pieces")
        check(len([h for h in bpy.app.handlers.render_pre if getattr(h, "_gorilla_geometry_visibility", False)]) == 1, "Exactly one persistent geometry handler survives cache load")
        for callback in list(bpy.app.handlers.render_pre):
            if getattr(callback, "_gorilla_geometry_visibility", False):
                callback(bpy.context.scene)
        check(all(obj.hide_render for obj in reopened if geometry.read_provenance(obj)["flags"] == 129), "Persistent cached-scene pass applies the requested gear mode")
        state.unregister()
        check(gorilla_blender.import_mcd is native, "Unregister restores the original native importer")
    check(all(hashlib.sha256(path.read_bytes()).hexdigest() == before[str(path)] for path in sources), "All supplied MCD files remain byte-identical")
    report = {"checks_passed": checks, "native_linux_blender_import_executed": True,
              "blender_version": bpy.app.version_string, "actual_attire_models": len(sources),
              "mesh_count": sum(row["meshes"] for row in records), "model_results": records,
              "native_cache_save_reopen_executed": True, "geometry_materials_and_weights_changed": False,
              "source_files_unchanged": True, "image_render_executed": False,
              "windows_gui_or_game_executed": False, "Adam_Cole_model_tested": False,
              "body_hide_masks_applied": False}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    print("PASS", checks, "real Blender geometry import and visibility checks", flush=True)


if __name__ == "__main__":
    main()
