"""Exercise the original compiled renderer's compressed meshes-mode transaction.

Run with Blender --background --factory-startup --python-exit-code 1 --python
this_file -- --application APP --model SUPPLIED_ATTIRE.mcd --output report.json.
The supplied MCD is copied into both source roles solely to exercise the real
native import/save/schema/exit path. This is not a complete wrestler or render.
"""

import argparse
import contextlib
import hashlib
import importlib.util
import io
import json
import marshal
import os
from pathlib import Path
import shutil
import sys
import tempfile

import bpy
import numpy as np


class Forward:
    def __init__(self, target, **overrides):
        self.target, self.overrides = target, overrides

    def __getattr__(self, name):
        return self.overrides[name] if name in self.overrides else getattr(self.target, name)


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--application", required=True, type=Path)
    parser.add_argument("--model", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--module", type=Path,
                        default=Path(__file__).resolve().parents[1] / "src/renderer/gorilla_geometry_cache.py")
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    application, model = args.application.resolve(), args.model.resolve()
    engine = application / "engine/blender"
    native_path, rest_path = engine / "gr_render.pyc", engine / "dump_rest.pyc"
    original_hashes = {str(path): sha256(path) for path in (model, native_path, rest_path)}
    spec = importlib.util.spec_from_file_location("gorilla_geometry_cache", args.module)
    cache = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(cache)
    sys.path.insert(0, str(engine))
    old_environment = {name: os.environ.get(name) for name in ("GS_ROOT", "GORILLA_GEOMETRY_REVISION")}
    os.environ["GS_ROOT"] = str(application)
    os.environ["GORILLA_GEOMETRY_REVISION"] = cache.REVISION
    code = marshal.loads(native_path.read_bytes()[16:])
    rest_code = marshal.loads(rest_path.read_bytes()[16:])
    messages = []
    saves = []
    original_argv, original_trace = sys.argv, sys.gettrace()

    def check(condition, message):
        if not condition:
            raise AssertionError(message)
        messages.append(message)

    check(code.co_filename == "gr_render.py", "The supplied original renderer bytecode is executed")
    import gorilla_blender
    original_import = gorilla_blender.import_mcd
    imports = []

    def imported(path, *import_args, **import_options):
        result = original_import(path, *import_args, **import_options)
        imports.append(str(path))
        return result

    gorilla_blender.import_mcd = imported
    try:
        with tempfile.TemporaryDirectory(prefix="gorilla-native-compressed-cache-") as temporary:
            root = Path(temporary)
            character = root / "123_Native_Cache_Fixture"
            (character / "BaseModel").mkdir(parents=True)
            (character / "Default_Attire").mkdir()
            base, attire = character / "BaseModel/baseModel.mcd", character / "Default_Attire/Attire.mcd"
            for destination in (base, attire):
                shutil.copy2(model, destination)
            capture = root / "capture.npz"
            np.savez(capture, t=np.array([0.0]), A=np.zeros((1, 1, 4, 4)), names=np.array(["J_Root"]))
            work = root / "work"
            work.mkdir()
            blend, manifest, marker = work / "model.blend", work / "meshes.json", work / cache.SIDECAR
            options = {"char_dir": str(character), "attire_mcd": "Default_Attire/Attire.mcd", "blend": str(blend), "shape_keys": "1"}

            def native(selected_options, after_exit=None, rename=False):
                selected_work = Path(selected_options["blend"]).parent
                sys.argv = [str(native_path), "--", str(capture), str(selected_work), "meshes"]
                sys.argv.extend(key + "=" + str(value) for key, value in selected_options.items())
                namespace = {"__file__": str(native_path), "__name__": "__main__"}
                renamed = False

                def compressed_save(*save_args, **save_options):
                    # Force the genuine Blender compressed format while the
                    # original native save call and subsequent code still run.
                    save_options["compress"] = True
                    result = bpy.ops.wm.save_as_mainfile(*save_args, **save_options)
                    saves.append(Path(save_options["filepath"]).read_bytes()[:12].hex())
                    return result

                proxy = Forward(bpy, ops=Forward(bpy.ops, wm=Forward(bpy.ops.wm, save_as_mainfile=compressed_save)))

                def trace(frame, event, unused_argument):
                    nonlocal renamed
                    if frame.f_code is not code:
                        return None
                    if event == "line" and frame.f_lineno == 191:
                        # Only the saving operator is forwarded with compress.
                        # No bytecode, jump, manifest, print or exit is changed.
                        if rename and not renamed:
                            next(obj for obj in frame.f_globals["objs"] if obj.type == "MESH").name += "_FAILED_REBUILD"
                            renamed = True
                        frame.f_globals["bpy"] = proxy
                    return trace

                sys.settrace(trace)
                try:
                    exec(code, namespace)
                except SystemExit as error:
                    if error.code in (None, 0) and after_exit is not None:
                        after_exit(namespace)
                    raise
                finally:
                    sys.settrace(original_trace)
                    sys.argv = original_argv

            def run(selected_options, after_exit=None, rename=False):
                output = io.StringIO()
                error = None
                with contextlib.redirect_stdout(output):
                    try:
                        cache.run_with_geometry_cache(selected_options,
                            lambda: native(selected_options, after_exit, rename))
                    except BaseException as caught:
                        error = caught
                return error, output.getvalue()

            error, output = run(options)
            check(isinstance(error, SystemExit) and error.code == 0,
                  "A real native compressed first import preserves SystemExit(0)")
            check(len(imports) == 2 and len(saves) == 1,
                  "The original renderer imports both actual MCD source-role fixtures and saves once")
            check(blend.is_file() and manifest.is_file() and marker.is_file(),
                  "Fresh compressed import keeps model, complete native mesh manifest and committed identity together")
            check(bytes.fromhex(saves[0]).startswith(bytes.fromhex("28b52ffd")),
                  "The actual native model save uses Blender's Zstandard compressed file format")
            check(cache._blend_file(blend), "The production file guard accepts the real compressed Blender cache")
            rows = json.loads(manifest.read_text())
            check(len(rows) == 20 and all(set(row) == {"name", "full", "verts", "materials"} for row in rows),
                  "The exact compiled native meshes mode writes its 20-row manifest and original schema")
            check(output.count("MESHES_DONE 20") == 1,
                  "Exactly one native success marker is released after compressed first-import commit")
            check(not (work / cache.JOURNAL).exists(), "A successful native commit clears the pending journal")
            check(cache._current(cache._context(options)), "The compressed cache is current under its committed source identity")

            # Execute the real subsequent skeleton stage, after an actual load
            # of the compressed cache, rather than accepting only a magic tag.
            bpy.ops.wm.open_mainfile(filepath=str(blend))
            check(len([obj for obj in bpy.context.scene.objects if obj.type == "MESH"]) == len(rows),
                  "The compressed model reopens in Blender with all native imported meshes")
            rest = work / "rest.npz"
            rest_output = io.StringIO()
            sys.argv = [str(rest_path), "--", str(rest)]
            try:
                with contextlib.redirect_stdout(rest_output):
                    exec(rest_code, {"__file__": str(rest_path), "__name__": "__main__"})
            finally:
                sys.argv = original_argv
            check(rest.is_file() and "REST_DUMP" in rest_output.getvalue(),
                  "The original compiled skeleton-dump stage completes from the compressed saved model")
            with np.load(rest, allow_pickle=True) as captured_rest:
                check(len(captured_rest["names"]) == 175,
                      "The downstream original skeleton dump retains all 175 imported bone names")

            before_reuse = (sha256(blend), blend.stat().st_mtime_ns, len(imports), len(saves))
            error, output = run(options)
            check(isinstance(error, SystemExit) and error.code == 0 and output.count("MESHES_DONE 20") == 1,
                  "Current compressed native reuse preserves the native exit and single success marker")
            check((sha256(blend), blend.stat().st_mtime_ns, len(imports), len(saves)) == before_reuse,
                  "Current compressed reuse performs no MCD reimport or model save and leaves model bytes unchanged")

            # Force a genuine identity refresh without changing model bytes.
            # The original code writes a distinct new manifest before a damaged
            # saved model makes the transaction fail at commit.
            original_mtime = attire.stat().st_mtime_ns
            os.utime(attire, ns=(attire.stat().st_atime_ns, original_mtime + 1_000_000_000))
            previous = {path: path.read_bytes() for path in (blend, manifest, marker)}
            observed_native = []

            def corrupt_after_native(namespace):
                observed_native.append(len(namespace["meshes"]))
                Path(namespace["BLEND"]).write_bytes(bytes.fromhex("28b52ffd") + b"broken compressed stream")

            error, output = run(options, corrupt_after_native, rename=True)
            check(isinstance(error, RuntimeError) and "cache" in str(error).lower(),
                  "A post-native-output failed commit remains a visible cache failure")
            check(observed_native == [20], "The original failed rebuild reaches its native 20-mesh completion path")
            check("MESHES_DONE" not in output,
                  "Failed compressed commit never leaks the native success marker to the engine")
            check(all(path.read_bytes() == payload for path, payload in previous.items()),
                  "A failed compressed refresh restores the previous model, manifest and marker byte for byte")
            check(not (work / cache.JOURNAL).exists(), "A rolled-back native failure clears its pending journal")
            bpy.ops.wm.open_mainfile(filepath=str(blend))
            check(all("_FAILED_REBUILD" not in obj.name for obj in bpy.context.scene.objects),
                  "The restored compressed model loads with the prior mesh names rather than failed-refresh geometry")
            check(all("_FAILED_REBUILD" not in row["full"] for row in json.loads(manifest.read_text())),
                  "The restored native manifest describes the same prior model after rollback")

            # A valid native model must remain available if only the auxiliary
            # identity write fails, while the success marker remains withheld.
            atomic_json = cache._atomic_json

            def fail_marker_write(path, *write_args, **write_options):
                if path == marker:
                    raise OSError("simulated identity write failure after native model save")
                return atomic_json(path, *write_args, **write_options)

            cache._atomic_json = fail_marker_write
            try:
                error, output = run(options, rename=True)
            finally:
                cache._atomic_json = atomic_json
            check(isinstance(error, OSError) and "identity write failure" in str(error),
                  "An auxiliary identity-write failure preserves its actual diagnostic")
            check("MESHES_DONE" not in output,
                  "A valid native model cannot publish success before its identity commit succeeds")
            check(blend.is_file() and cache._blend_file(blend) and cache._mesh_manifest(manifest),
                  "An auxiliary commit failure keeps the valid compressed model and native manifest together")
            check((work / cache.JOURNAL).is_file() and marker.read_bytes() == previous[marker],
                  "Auxiliary commit failure retains the previous identity and an explicit recovery journal")
            bpy.ops.wm.open_mainfile(filepath=str(blend))
            check(any("_FAILED_REBUILD" in obj.name for obj in bpy.context.scene.objects),
                  "The preserved valid compressed model remains readable after auxiliary commit failure")
            error, output = run(options)
            check(isinstance(error, SystemExit) and error.code == 0 and output.count("MESHES_DONE 20") == 1,
                  "The next original native attempt safely recovers the interrupted commit and completes")
            check(cache._current(cache._context(options)) and not (work / cache.JOURNAL).exists(),
                  "Recovered native commit leaves a current compressed cache and no pending journal")

            fresh = root / "failed-fresh"
            fresh.mkdir()
            fresh_options = dict(options, blend=str(fresh / "model.blend"))
            error, output = run(fresh_options, corrupt_after_native, rename=True)
            check(isinstance(error, RuntimeError) and "MESHES_DONE" not in output,
                  "A failing first compressed import suppresses the native success marker")
            check(all(not (fresh / name).exists() for name in ("model.blend", "meshes.json", cache.SIDECAR, cache.JOURNAL)),
                  "A failed fresh transaction leaves no misleading model, mesh manifest, marker or pending journal")

            # The format check must inspect decoded Blender bytes; a familiar
            # compression magic prefix cannot establish a valid cache alone.
            damaged = root / "damaged.blend"
            for payload in (bytes.fromhex("28b52ffd"), bytes.fromhex("28b52ffd") + b"not a zstd frame",
                            bytes.fromhex("1f8b0800") + b"not a gzip stream"):
                damaged.write_bytes(payload)
                check(not cache._blend_file(damaged), "An invalid or truncated compressed header is rejected")
            check(all(sha256(path) == expected for path, expected in original_hashes.items()),
                  "The supplied original MCD and both original compiled renderer scripts remain byte-identical")
            check(sha256(base) == original_hashes[str(model)] and sha256(attire) == original_hashes[str(model)],
                  "Both temporary source-role fixtures retain the exact original MCD bytes")

            report = {"checks_passed": len(messages), "checks": messages, "blender_version": bpy.app.version_string,
                      "actual_original_compiled_meshes_mode_executed": True,
                      "actual_original_compiled_skeleton_dump_executed": True,
                      "native_imported_meshes": len(rows), "skeleton_bones": 175,
                      "compressed_save_headers": saves, "source_files_unchanged": True,
                      "MCD_source_roles": "The supplied attire is copied into both body and attire roles for this cache regression only.",
                      "image_render_executed": False, "user_Brody_King_model_tested": False,
                      "Windows_gui_or_game_executed": False}
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps(report, indent=2) + "\n")
            print("PASS", len(messages), "original native compressed-cache regression checks", flush=True)
    finally:
        gorilla_blender.import_mcd = original_import
        sys.settrace(original_trace)
        sys.argv = original_argv
        for name, value in old_environment.items():
            if value is None:
                os.environ.pop(name, None)
            else:
                os.environ[name] = value


if __name__ == "__main__":
    main()
