"""Exercise the production native-model cache migration without rendering.

Run with ordinary CPython. Real Blender save/load coverage is in the companion
validate_geometry_cache_blender.py fixture. Source MCD contents here are test
bytes; this suite verifies cache ownership and transaction semantics only.
"""
import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import subprocess
import sys
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("gorilla_geometry_cache", ROOT / "src/renderer/gorilla_geometry_cache.py")
cache = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cache)
checks = 0


def check(value, description):
    global checks
    assert value, description
    checks += 1


def reject(action, text):
    try:
        action()
    except RuntimeError as error:
        check(text in str(error), "Expected rejection: " + text)
    else:
        raise AssertionError("Expected RuntimeError: " + text)


def fixture(root, old=True):
    character = root / "Character"
    (character / "BaseModel").mkdir(parents=True)
    (character / "Default_Attire").mkdir()
    (character / "BaseModel/baseModel.mcd").write_bytes(b"unchanged body MCD source")
    (character / "Default_Attire/Attire.mcd").write_bytes(b"unchanged attire MCD source")
    work = root / "work"
    work.mkdir()
    blend = work / "model.blend"
    meshes = work / "meshes.json"
    if old:
        blend.write_bytes(b"BLENDER-v403 old cached model")
        manifest(meshes)
    return {"char_dir": str(character), "attire_mcd": "Default_Attire/Attire.mcd", "blend": str(blend)}


def manifest(path):
    path.write_text(json.dumps([{"name": "M_Body", "full": "M_Body:skinned", "verts": 4, "materials": ["M_Body"]}]))


def save(options, payload=b"BLENDER-v403 new cached model"):
    path = Path(options["blend"])
    path.write_bytes(payload)
    manifest(path.parent / "meshes.json")


def run_suite():
    with tempfile.TemporaryDirectory(prefix="gorilla-geometry-cache-") as temporary:
        root = Path(temporary)
        def case(name, old=True):
            folder = root / name
            folder.mkdir()
            return fixture(folder, old)
        options = case("migration")
        blend = Path(options["blend"])
        original = blend.read_bytes()
        source_hashes = {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in Path(options["char_dir"]).rglob("*.mcd")}
        calls = []
        def fresh():
            check(not blend.exists(), "Legacy cache is absent before the native importer runs")
            calls.append(1)
            save(options)
            return 37
        check(cache.run_with_geometry_cache(options, fresh) == 37, "Callback return value is retained")
        check(blend.read_bytes().endswith(b"new cached model"), "Fresh cache is retained")
        backups = list(blend.parent.glob("model.pre-geometry-v1*.blend"))
        check(len(backups) == 1 and backups[0].read_bytes() == original, "Original cache remains recoverable")
        marker = json.loads((blend.parent / cache.SIDECAR).read_text())
        check(marker["identity"]["revision"] == "1", "Successful save commits revision one")
        before = {p.name: p.stat().st_mtime_ns for p in blend.parent.iterdir()}
        check(cache.run_with_geometry_cache(options, lambda: "reused") == "reused", "Current cache goes directly to native reuse")
        check(len(calls) == 1 and before == {p.name: p.stat().st_mtime_ns for p in blend.parent.iterdir()}, "Current cache triggers no migration or metadata write")
        check(source_hashes == {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in Path(options["char_dir"]).rglob("*.mcd")}, "MCD source files remain byte-identical")
        for name, exception in (("failed-import", RuntimeError("native failure")), ("failed-exit", SystemExit(1)), ("interrupted", KeyboardInterrupt())):
            options = case(name)
            blend = Path(options["blend"])
            old = blend.read_bytes()
            def failure():
                save(options, b"BLENDER-v403 partially saved failed model")
                raise exception
            try:
                cache.run_with_geometry_cache(options, failure)
            except BaseException as got:
                check(got is exception, "Original native failure is preserved: " + name)
            check(blend.read_bytes() == old, "Previous cache is restored: " + name)
            check(not (blend.parent / cache.SIDECAR).exists(), "Failed cache is not marked current: " + name)
        options = case("failed-fresh", False)
        def failed_fresh():
            save(options, b"partial Blender save")
            raise RuntimeError("native failure")
        reject(lambda: cache.run_with_geometry_cache(options, failed_fresh), "native failure")
        check(not Path(options["blend"]).exists(), "A failed first import leaves no partial cache for native reuse")
        options = case("missing-save")
        old = Path(options["blend"]).read_bytes()
        reject(lambda: cache.run_with_geometry_cache(options, lambda: None), "not saved successfully")
        check(Path(options["blend"]).read_bytes() == old, "A successful return without a saved blend restores the old cache")
        options = case("normal-exit")
        def normal_exit():
            save(options)
            raise SystemExit(0)
        try:
            cache.run_with_geometry_cache(options, normal_exit)
        except SystemExit as error:
            check(error.code == 0, "Native mesh-preparation SystemExit(0) is preserved")
        check((Path(options["blend"]).parent / cache.SIDECAR).is_file(), "Native mesh-preparation success commits its cache marker")
        options = case("changed-source")
        old = Path(options["blend"]).read_bytes()
        def changed():
            save(options)
            (Path(options["char_dir"]) / options["attire_mcd"]).write_bytes(b"new source changed during import")
        reject(lambda: cache.run_with_geometry_cache(options, changed), "sources changed")
        check(Path(options["blend"]).read_bytes() == old, "Changed source cannot publish a new cache revision")
        options = case("marker-failure")
        blend = Path(options["blend"])
        old = blend.read_bytes()
        replace = os.replace
        def fail_marker(source, destination):
            if Path(destination).name == cache.SIDECAR:
                raise OSError("simulated sidecar failure")
            return replace(source, destination)
        try:
            with mock.patch.object(cache.os, "replace", side_effect=fail_marker):
                cache.run_with_geometry_cache(options, lambda: save(options))
        except OSError as error:
            check("sidecar failure" in str(error), "Sidecar commit error remains visible")
        check(blend.read_bytes() == old, "Failed sidecar publication restores the old cache")
        check(not list(blend.parent.glob(".geometry-*json")), "Failed marker temporary files are removed")
        options = case("no-hardlinks")
        old = Path(options["blend"]).read_bytes()
        with mock.patch.object(cache.os, "link", side_effect=OSError("links unavailable")):
            cache.run_with_geometry_cache(options, lambda: save(options))
        check(next(Path(options["blend"]).parent.glob("model.pre-geometry-v1*.blend")).read_bytes() == old, "Copy fallback preserves old cache where file links are unavailable")
        for name, alteration in (
            ("wrong-revision", lambda value: None),
            ("other-blend", lambda value: value.update(blend=str(Path(value["blend"]).with_name("user-project.blend")))) ,
            ("outside-attire", lambda value: value.update(attire_mcd="../outside.mcd")),
            ("absolute-attire", lambda value: value.update(attire_mcd=str(Path(value["char_dir"]) / value["attire_mcd"]))),
            ("missing-source", lambda value: (Path(value["char_dir"]) / value["attire_mcd"]).unlink()),
            ("bad-manifest", lambda value: (Path(value["blend"]).parent / "meshes.json").write_text("[{}]")),
            ("not-blender", lambda value: Path(value["blend"]).write_bytes(b"unrelated user file")),
        ):
            options = case(name)
            protected = Path(options["blend"])
            alteration(options)
            original = protected.read_bytes()
            env = {"GORILLA_GEOMETRY_REVISION": "0" if name == "wrong-revision" else "1"}
            with mock.patch.dict(os.environ, env):
                result = cache.run_with_geometry_cache(options, lambda: "untouched")
            check(result == "untouched" and protected.read_bytes() == original, "Unproven cache ownership does not change a file: " + name)
            check(not list(protected.parent.glob("model.pre-geometry-*.blend")), "Unproven cache is not moved or backed up: " + name)
        options = case("existing-backup")
        previous = Path(options["blend"]).with_name("model.pre-geometry-v1.blend")
        previous.write_bytes(b"previous backup is preserved")
        cache.run_with_geometry_cache(options, lambda: save(options))
        check(previous.read_bytes() == b"previous backup is preserved", "An earlier revision backup is never overwritten")
        check(len(list(previous.parent.glob("model.pre-geometry-v1*.blend"))) == 2, "A later migration receives a distinct backup filename")
        options = case("busy-cache")
        old = Path(options["blend"]).read_bytes()
        with cache._cache_lock(Path(options["blend"]).parent):
            reject(lambda: cache.run_with_geometry_cache(options, lambda: save(options)), "another Studio process")
        check(Path(options["blend"]).read_bytes() == old, "Lock contention leaves the previous cache untouched")
        options = case("fresh-native", False)
        cache.run_with_geometry_cache(options, lambda: save(options))
        check((Path(options["blend"]).parent / cache.SIDECAR).exists(), "A first native import is marked current after success")
        check(not list(Path(options["blend"]).parent.glob("model.pre-geometry-*.blend")), "A first import invents no previous-cache backup")
        def crash(options, valid_header):
            code = """import importlib.util,json,os,sys
from pathlib import Path
spec=importlib.util.spec_from_file_location('gorilla_geometry_cache',sys.argv[1])
cache=importlib.util.module_from_spec(spec);spec.loader.exec_module(cache)
options=json.loads(sys.argv[2])
def interrupted():
    blend=Path(options['blend'])
    blend.write_bytes(b'BLENDER-v403 partial save' if sys.argv[3]=='yes' else b'partial save without valid header')
    stream=open(blend.parent/'meshes.json','w')
    stream.write('[incomplete');stream.flush()
    os._exit(23)
cache.run_with_geometry_cache(options,interrupted)
"""
            result = subprocess.run([sys.executable, "-c", code, str(ROOT / "src/renderer/gorilla_geometry_cache.py"),
                                     json.dumps(options), "yes" if valid_header else "no"], capture_output=True, text=True)
            check(result.returncode == 23, "Crash fixture exits abruptly while writing the cache")
        for name, old, valid_header in (("crash-old-invalid", True, False), ("crash-old-valid", True, True),
                                        ("crash-fresh-invalid", False, False), ("crash-fresh-valid", False, True)):
            options = case(name, old)
            blend = Path(options["blend"])
            old_bytes = blend.read_bytes() if old else None
            old_json = (blend.parent / "meshes.json").read_bytes() if old else None
            crash(options, valid_header)
            check((blend.parent / cache.JOURNAL).exists(), "Abrupt exit retains an owned recovery journal: " + name)
            check(((blend.parent / "meshes.json").read_bytes() == old_json) if old
                  else not (blend.parent / "meshes.json").exists(),
                  "A killed mesh JSON write cannot block the native engine before recovery: " + name)
            def recovered():
                check(not blend.exists(), "Recovery prepares a clean native import after the crash: " + name)
                save(options)
                return "recovered"
            check(cache.run_with_geometry_cache(options, recovered) == "recovered", "Next load recovers the interrupted cache: " + name)
            backups = list(blend.parent.glob("model.pre-geometry-v1*.blend"))
            check(all(path.read_bytes() == old_bytes for path in backups) if old else len(backups) == 0,
                  "Recovery never substitutes the partial save for a genuine backup: " + name)
            check(not (blend.parent / cache.JOURNAL).exists() and not list(blend.parent.glob(".geometry-meshes-*.json")),
                  "Successful recovery removes its pending journal and partial JSON files: " + name)
        options = case("malicious-pending")
        crash(options, False)
        blend = Path(options["blend"])
        pending_path = blend.parent / cache.JOURNAL
        pending = json.loads(pending_path.read_text())
        outside = root / "outside.blend"
        outside.write_bytes(b"unrelated user model")
        pending["backup"] = "../../outside.blend"
        pending_path.write_text(json.dumps(pending))
        partial = blend.read_bytes()
        reject(lambda: cache.run_with_geometry_cache(options, lambda: save(options)), "invalid backup path")
        check(outside.read_bytes() == b"unrelated user model" and blend.read_bytes() == partial,
              "An invalid journal cannot touch files outside the cache or replace its current target")
        options = case("changed-pending-owner")
        crash(options, True)
        blend = Path(options["blend"])
        alternate = Path(options["char_dir"]) / "Default_Attire/Other.mcd"
        alternate.write_bytes(b"different selected model source")
        other = dict(options, attire_mcd="Default_Attire/Other.mcd")
        partial = blend.read_bytes()
        reject(lambda: cache.run_with_geometry_cache(other, lambda: save(other)), "different cache sources")
        check(blend.read_bytes() == partial and (blend.parent / cache.JOURNAL).exists(),
              "Source-option mismatch never guesses another pending transaction's target")
        options = case("committed-pending", False)
        cache.run_with_geometry_cache(options, lambda: save(options))
        context = cache._context(options)
        cache._begin_journal(context, None)
        before = Path(options["blend"]).stat().st_mtime_ns
        check(cache.run_with_geometry_cache(options, lambda: "already committed") == "already committed",
              "Recovery recognizes a revision committed before a process exited")
        check(Path(options["blend"]).stat().st_mtime_ns == before and not (Path(options["blend"]).parent / cache.JOURNAL).exists(),
              "A valid committed cache is reused and only its stale pending journal is removed")
        options = case("atomic-current")
        cache.run_with_geometry_cache(options, lambda: save(options))
        mesh_path = Path(options["blend"]).parent / "meshes.json"
        previous = mesh_path.read_bytes()
        def bad_json():
            open(mesh_path, "w").write("[invalid")
        reject(lambda: cache.run_with_geometry_cache(options, bad_json), "mesh cache was not saved successfully")
        check(mesh_path.read_bytes() == previous, "Invalid completed JSON cannot replace the previous native mesh manifest")
        def valid_json():
            json.dump(json.loads(previous), open(mesh_path, "w"))
            return "native-json-saved"
        check(cache.run_with_geometry_cache(options, valid_json) == "native-json-saved", "The native json.dump(open(...)) pattern publishes its complete original schema")
        print(json.dumps({"checks_passed": checks, "native_Blender_executed": False,
                          "MCD_model_import_executed": False, "abrupt_process_exit_recovery": True,
                          "scope": "Cache transaction, ownership, crash recovery and atomic native mesh metadata fixtures"}))


if __name__ == "__main__":
    with mock.patch.dict(os.environ, {"GORILLA_GEOMETRY_REVISION": "1"}):
        run_suite()
