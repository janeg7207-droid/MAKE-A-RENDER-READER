"""Run cache migration against genuine Blender save/load operations.

blender --background --factory-startup --python tests/validate_geometry_cache_blender.py -- --output report.json
The MCD inputs are cache-ownership fixtures; this does not import a WWE model.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import bpy

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("gorilla_geometry_cache", ROOT / "src/renderer/gorilla_geometry_cache.py")
cache = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cache)
checks = 0


def check(value, description):
    global checks
    assert value, description
    checks += 1


def clear():
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)


def object_source(name):
    clear()
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([(0, 0, 0), (2, 0, 0), (2, 2, 0), (0, 2, 0)], [], [(0, 1, 2), (0, 2, 3)])
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.shape_key_add(name="Basis")
    obj.shape_key_add(name="Expression").data[2].co.z = 0.25
    uv = mesh.uv_layers.new(name="UVMap")
    for item in uv.data:
        item.uv = (0.25, 0.75)
    material = bpy.data.materials.new(name + "_material")
    mesh.materials.append(material)
    group = obj.vertex_groups.new(name="J_Spine")
    group.add([0, 1, 2, 3], 0.75, "REPLACE")
    obj["gorilla_geometry_revision"] = 1
    return obj


def loaded(name):
    obj = bpy.data.objects.get(name)
    return (obj is not None and len(obj.data.vertices) == 4 and len(obj.data.polygons) == 2
            and obj.data.shape_keys is not None and len(obj.data.shape_keys.key_blocks) == 2
            and len(obj.data.uv_layers) == 1 and len(obj.data.materials) == 1
            and len(obj.vertex_groups) == 1 and obj.get("gorilla_geometry_revision") == 1)


def manifest(path, name):
    path.write_text(json.dumps([{"name": name, "full": name, "verts": 4, "materials": [name + "_material"]}]))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    old_revision = os.environ.get("GORILLA_GEOMETRY_REVISION")
    os.environ["GORILLA_GEOMETRY_REVISION"] = "1"
    try:
        with tempfile.TemporaryDirectory(prefix="gorilla-geometry-cache-blender-") as temporary:
            root = Path(temporary)
            character = root / "Character"
            (character / "BaseModel").mkdir(parents=True)
            (character / "Default_Attire").mkdir()
            base = character / "BaseModel/baseModel.mcd"
            attire = character / "Default_Attire/Attire.mcd"
            base.write_bytes(b"body model source fixture")
            attire.write_bytes(b"attire model source fixture")
            source_hashes = [hashlib.sha256(path.read_bytes()).hexdigest() for path in (base, attire)]
            work = root / "work"
            work.mkdir()
            blend = work / "model.blend"
            options = {"char_dir": str(character), "attire_mcd": "Default_Attire/Attire.mcd", "blend": str(blend)}
            object_source("Legacy")
            bpy.ops.wm.save_as_mainfile(filepath=str(blend))
            manifest(work / "meshes.json", "Legacy")
            check(cache._blend_file(blend), "Actual Blender output passes the cache file format guard")
            original = hashlib.sha256(blend.read_bytes()).hexdigest()
            def fresh():
                check(not blend.exists(), "Actual native-style save starts without the old cache path")
                object_source("Fresh")
                bpy.ops.wm.save_as_mainfile(filepath=str(blend))
                manifest(work / "meshes.json", "Fresh")
                return "fresh-saved"
            check(cache.run_with_geometry_cache(options, fresh) == "fresh-saved", "Real Blender save completes migration")
            marker = json.loads((work / cache.SIDECAR).read_text())
            check(marker["identity"]["revision"] == "1", "Real Blender migration commits revision one")
            backup = next(work.glob("model.pre-geometry-v1*.blend"))
            check(hashlib.sha256(backup.read_bytes()).hexdigest() == original, "Legacy Blender backup remains byte-identical")
            bpy.ops.wm.open_mainfile(filepath=str(backup))
            check(loaded("Legacy"), "Legacy backup loads with vertices, faces, shape keys, UVs, materials and groups intact")
            before = blend.stat().st_mtime_ns
            def reuse():
                bpy.ops.wm.open_mainfile(filepath=str(blend))
                check(loaded("Fresh"), "Fresh cache loads through current-cache reuse")
                return "reused"
            check(cache.run_with_geometry_cache(options, reuse) == "reused", "Current cache bypasses reimport")
            check(blend.stat().st_mtime_ns == before and len(list(work.glob("model.pre-geometry-v1*.blend"))) == 1,
                  "Current reuse creates no extra save or backup")
            # Force a source revision change, then simulate a failure after an
            # actual partial model save. The prior valid fresh cache must return.
            attire.write_bytes(b"attire source changed for rollback fixture")
            fresh_hash = hashlib.sha256(blend.read_bytes()).hexdigest()
            def fail_after_save():
                object_source("Failed")
                bpy.ops.wm.save_as_mainfile(filepath=str(blend))
                raise RuntimeError("simulated post-save native failure")
            try:
                cache.run_with_geometry_cache(options, fail_after_save)
            except RuntimeError as error:
                check("post-save native failure" in str(error), "Native render failure remains visible")
            else:
                raise AssertionError("Expected failure")
            check(hashlib.sha256(blend.read_bytes()).hexdigest() == fresh_hash, "A failed real save restores the previous cache byte-for-byte")
            bpy.ops.wm.open_mainfile(filepath=str(blend))
            check(loaded("Fresh") and bpy.data.objects.get("Failed") is None,
                  "The restored real cache opens with its full prior geometry and metadata")
            check(source_hashes[0] == hashlib.sha256(base.read_bytes()).hexdigest(), "Base source bytes remain unchanged")
            report = {"checks_passed": checks, "Blender_executed": True, "Blender_version": bpy.app.version_string,
                      "MCD_model_import_executed": False, "user_Adam_Cole_case_rendered": False,
                      "scope": "Real Blender cache save/load, backups, metadata preservation, reuse and rollback"}
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps(report, indent=2) + "\n")
            print(json.dumps(report))
    finally:
        if old_revision is None:
            os.environ.pop("GORILLA_GEOMETRY_REVISION", None)
        else:
            os.environ["GORILLA_GEOMETRY_REVISION"] = old_revision


main()
