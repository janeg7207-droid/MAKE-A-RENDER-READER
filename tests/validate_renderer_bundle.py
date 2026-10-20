"""Exercise the self-contained updater renderer in real background Blender.

Tiny source/importer fixtures isolate bundle loading, gear switching, the native
save-before-render ordering and cache reuse. Actual shipped importer/MCD checks
run separately in validate_geometry_blender.py.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import textwrap


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--blender", default="/usr/bin/blender")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    subprocess.run([shutil.which("python3"), str(repo / "tools/build_renderer_bundle.py"), "--check"], check=True)
    checks = 0

    def check(condition, message):
        nonlocal checks
        if not condition:
            raise AssertionError(message)
        checks += 1
        print("PASS " + message, flush=True)

    with tempfile.TemporaryDirectory(prefix="gorilla bundle ") as temporary:
        root = Path(temporary)
        engine, character, cache = root / "engine", root / "Adam test", root / "work" / "model"
        native = engine / "blender"
        for directory in (native, character / "BaseModel", character / "Attire", cache):
            directory.mkdir(parents=True, exist_ok=True)
        wrapper = engine / "gr_render.py"
        shutil.copy2(repo / "runtime/engine/gr_render.py", wrapper)
        (character / "BaseModel/baseModel.mcd").write_bytes(b"fixture base source")
        (character / "Attire/Attire.mcd").write_bytes(b"fixture attire source")
        (native / "gs_paths.py").write_text("from pathlib import Path\nSTOCK_ROOT=Path(" + repr(str(root / "stock")) + ")\n")
        (native / "gorilla_mcd.py").write_text(textwrap.dedent('''
            from pathlib import Path
            class Model:
                def __init__(self,path):
                    self.path=Path(path); self.b=self.path.read_bytes()
                    names=[('Body',128)] if self.path.parent.name=='BaseModel' else [('Duplicate',129),('Duplicate',130)]
                    self.meshes=[dict(name=name,flags=flag,verts=3,numbering=[0,1,2]) for name,flag in names]
        '''))
        (native / "gorilla_blender.py").write_text(textwrap.dedent('''
            import bpy
            import gorilla_mcd as GM
            def import_mcd(path,**kwargs):
                model=GM.Model(path)
                arm=bpy.data.objects.new('rig',bpy.data.armatures.new('rig'))
                bpy.context.collection.objects.link(arm)
                result=[arm]
                for descriptor in model.meshes:
                    mesh=bpy.data.meshes.new(descriptor['name'])
                    mesh.from_pydata([(0,0,0),(1,0,0),(0,1,0)],[],[(0,1,2)])
                    obj=bpy.data.objects.new(descriptor['name'],mesh)
                    bpy.context.collection.objects.link(obj); result.append(obj)
                return result
        '''))
        (native / "gr_render.py").write_text(textwrap.dedent('''
            from pathlib import Path
            import json,os,sys
            import bpy
            import gorilla_blender as GB
            values=dict(v.split('=',1) for v in sys.argv[sys.argv.index('--')+4:] if '=' in v)
            blend=Path(values['blend']); manifest=blend.parent/'meshes.json'
            reused=blend.exists()
            if reused:
                bpy.ops.wm.open_mainfile(filepath=str(blend))
            else:
                for obj in list(bpy.data.objects): bpy.data.objects.remove(obj,do_unlink=True)
                source=Path(values['char_dir'])
                GB.import_mcd(source/'BaseModel/baseModel.mcd')
                GB.import_mcd(source/values['attire_mcd'])
                bpy.ops.wm.save_as_mainfile(filepath=str(blend))
                manifest.write_text(json.dumps([dict(name=o.name,full=o.name,verts=len(o.data.vertices),materials=[]) for o in bpy.data.objects if o.type=='MESH']))
            neutral=all(not o.hide_render for o in bpy.data.objects if o.type=='MESH')
            for handler in list(bpy.app.handlers.render_pre): handler(bpy.context.scene)
            rows=[dict(name=o.name,role=o.get('gorilla_geometry_role'),flag=o.get('gorilla_geometry_flags'),hidden=o.hide_render,vertices=len(o.data.vertices)) for o in bpy.data.objects if o.type=='MESH']
            (blend.parent/'observed.json').write_text(json.dumps(dict(reused=reused,neutral=neutral,rows=rows,mode=os.environ.get('GORILLA_GEAR_CHOICE'))))
        '''))
        sources = {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in character.rglob("*.mcd")}
        results = []
        for mode in ("ring", "entrance"):
            env = os.environ.copy()
            env["GORILLA_GEOMETRY_REVISION"] = "1"
            env["GORILLA_GEAR_CHOICE"] = mode
            command = [args.blender, "--background", "--factory-startup", "--python-exit-code", "1", "--python", str(wrapper),
                       "--", "capture.npz", str(root / "output"), "clip", "char_dir=" + str(character),
                       "attire_mcd=Attire/Attire.mcd", "blend=" + str(cache / "model.blend")]
            completed = subprocess.run(command, env=env, text=True, capture_output=True, timeout=120)
            if completed.returncode != 0:
                raise AssertionError(completed.stdout + "\n" + completed.stderr)
            result = json.loads((cache / "observed.json").read_text())
            results.append(result)
            check(len(result["rows"]) == 3, mode + ": standalone updater wrapper imports every fixture mesh")
            check(result["neutral"], mode + ": saved cache remains neutral before render visibility")
            check(result["reused"] == (mode == "entrance"), mode + ": first source imports once and second mode reuses its cache")
            by_flag = {row["flag"]: row for row in result["rows"]}
            check(by_flag[128]["role"] == "base" and not by_flag[128]["hidden"], mode + ": base mesh keeps its own proven source and visibility")
            check(by_flag[129]["hidden"] == (mode == "ring"), mode + ": exact entrance-only piece follows current gear mode")
            check(by_flag[130]["hidden"] == (mode == "entrance"), mode + ": duplicate-name ring-only piece follows current gear mode")
            check(all(row["vertices"] == 3 for row in result["rows"]), mode + ": gear switching preserves source vertex counts")
        check((cache / "model.geometry.json").is_file(), "Standalone bundle commits a reusable geometry cache revision")
        check(not list(engine.glob("gorilla_geometry*.py")), "Update entry works without additional updater payload files")
        check(all(hashlib.sha256(Path(p).read_bytes()).hexdigest() == digest for p, digest in sources.items()), "Source character files remain unchanged")
        check(not list(cache.glob("model.pre-geometry*.blend")), "A fresh current cache does not accumulate rebuild backups")
        marker_before = (cache / "model.geometry.json").read_bytes()
        blend_before = (cache / "model.blend").stat().st_mtime_ns
        (native / "gorilla_blender.py").write_text("def import_mcd(*args,**kwargs):\n    raise RuntimeError('unused unsupported importer')\n")
        completed = subprocess.run(command, env=env, text=True, capture_output=True, timeout=120)
        check(completed.returncode == 0, "An unsupported importer leaves the original cached-renderer path usable")
        check((cache / "model.geometry.json").read_bytes() == marker_before, "Unsupported registration does not claim a fresh geometry cache")
        check((cache / "model.blend").stat().st_mtime_ns == blend_before, "Unsupported registration never migrates the existing model cache")
    report = {"checks_passed": checks, "Blender_executed": True, "actual_shipped_MCD_importer": False,
              "self_contained_eleven_file_update_entry_verified": True, "Adam_Cole_case_rendered": False,
              "scope": "Embedded source integrity, real Blender importer fixture, save-before-render gear neutrality and cross-mode cache reuse"}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report))


if __name__ == "__main__":
    main()
