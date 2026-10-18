"""Verify speed options through the original packaged engine and renderer.

Run with CPython 3.14, NumPy and Pillow, and pass a CPython 3.13 interpreter for
the shipped Blender bytecode.  Expensive model preparation and image rendering
are skipped with tracing.  Real command builders, option parsing, resolution
setup and EEVEE sample assignment execute unchanged.  This is structural
validation; it does not run Windows, Blender, encode video or measure speed.
"""

import argparse
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import runpy
import shutil
import subprocess
import sys
import tempfile
import types
import warnings


def blender_probe(wrapper, argv):
    """Execute the original 3.13 module's parser and render settings with stubs."""
    scene = types.SimpleNamespace(
        render=types.SimpleNamespace(), view_settings=types.SimpleNamespace(),
        eevee=types.SimpleNamespace(),
    )
    background = types.SimpleNamespace(inputs=[types.SimpleNamespace(), types.SimpleNamespace()])
    world = types.SimpleNamespace(node_tree=types.SimpleNamespace(
        nodes=types.SimpleNamespace(get=lambda name: background)))
    bpy = types.ModuleType("bpy")
    bpy.context = types.SimpleNamespace(scene=scene)
    bpy.data = types.SimpleNamespace(worlds=types.SimpleNamespace(get=lambda name: world))
    sys.modules["bpy"] = bpy
    sys.modules["numpy"] = types.ModuleType("numpy")
    sys.modules["file_audit"] = types.ModuleType("file_audit")
    mathutils = types.ModuleType("mathutils")
    mathutils.Matrix = mathutils.Vector = object
    sys.modules["mathutils"] = mathutils
    paths = types.ModuleType("gs_paths")
    paths.STOCK_ROOT = Path(wrapper).parent / "unused-stock"
    sys.modules["gs_paths"] = paths
    sys.argv = list(argv)
    result = {}

    class Captured(BaseException):
        pass

    def trace(frame, event, unused):
        if frame.f_code.co_filename == "gr_render.py" and event == "line":
            if frame.f_lineno == 24:
                # The actual parser has run; skip model/texture preparation.
                frame.f_lineno = 981
            elif frame.f_lineno == 983:
                # reset_render has run; skip light/material creation.
                frame.f_lineno = 1009
            elif frame.f_lineno == 1019:
                result.update({
                    "options": frame.f_globals["OPT"],
                    "engine": scene.render.engine,
                    "resolution": [scene.render.resolution_x, scene.render.resolution_y],
                    "samples": scene.eevee.taa_render_samples,
                    "argv": list(sys.argv),
                })
                raise Captured()
        return trace

    sys.settrace(trace)
    try:
        with contextlib.redirect_stdout(io.StringIO()):
            runpy.run_path(wrapper, run_name="__main__")
    except Captured:
        pass
    finally:
        sys.settrace(None)
    assert result, "The original render settings block did not execute"
    print(json.dumps(result))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine-modules", type=Path, required=True)
    parser.add_argument("--application", type=Path, required=True)
    parser.add_argument("--blender-python", type=Path, required=True)
    parser.add_argument("--wrapper", type=Path, default=Path(__file__).resolve().parents[1] / "runtime/engine/gr_render.py")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    os.environ["GS_ROOT"] = str(args.application.resolve())
    # Platform packages must load before the packaged Windows modules enter sys.path.
    import numpy  # noqa: F401
    from PIL import Image  # noqa: F401
    sys.path.append(str(args.engine_modules.resolve()))
    import gorilla_render
    import gs_paths

    checks = 0
    commands = {}
    probes = []
    originals = [args.application / "engine/blender" / (name + ".pyc")
                 for name in ("gr_render", "render_kit")]
    before = {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in originals}

    def check(condition, message):
        nonlocal checks
        assert condition, message
        checks += 1

    class Captured(BaseException):
        def __init__(self, command):
            self.command = command

    def capture_command(mode, work):
        work.mkdir()
        values = {
            "quick": mode == "preview", "only": None,
            "still": mode == "still", "freeze": mode == "freeze", "play_to": 199,
            "work": str(work), "t1": 199 / 30,
            "base": ["capture.npz", str(work / "frames"), "clip"] + gorilla_render.LOOK,
            "cam": str(work / "cam.json"), "cam_in_render": False,
            "dark": [0.1] * 3, "light": [0.3] * 3, "hide": [],
            "render_file": None, "gmr": types.SimpleNamespace(ENC="unused-encoder"),
        }

        def pack(command, *unused, **unused_options):
            if mode == "fallback":
                return -1, "synthetic failure to exercise fallback", None, None
            raise Captured(command)

        fake = types.ModuleType("fast_pack")
        fake.render_and_pack = pack
        previous_pack = sys.modules.get("fast_pack")
        previous_watched = gorilla_render.run_watched
        sys.modules["fast_pack"] = fake

        def watched(command, *unused, **unused_options):
            raise Captured(command)

        gorilla_render.run_watched = watched
        entered = False

        def trace(frame, event, unused):
            nonlocal entered
            if frame.f_code is gorilla_render.main.__code__ and event == "line" and not entered:
                entered = True
                frame.f_locals.update(values)
                # Exercise the unmodified command-building branch, after model preparation.
                frame.f_lineno = 457
            return trace

        sys.settrace(trace)
        try:
            with warnings.catch_warnings(), contextlib.redirect_stdout(io.StringIO()):
                warnings.simplefilter("ignore", RuntimeWarning)
                gorilla_render.main()
        except Captured as captured:
            return captured.command
        finally:
            sys.settrace(None)
            gorilla_render.run_watched = previous_watched
            if previous_pack is None:
                sys.modules.pop("fast_pack", None)
            else:
                sys.modules["fast_pack"] = previous_pack
        raise AssertionError("The native command builder did not run")

    previous_extra = os.environ.get("GORILLA_EXTRA")
    previous_engine = gs_paths.ENGINE
    try:
        with tempfile.TemporaryDirectory(prefix="gorilla speed ") as temporary:
            root = Path(temporary)
            engine = root / "engine"
            (engine / "blender").mkdir(parents=True)
            wrapper = engine / "gr_render.py"
            shutil.copy2(args.wrapper, wrapper)
            for original in originals:
                shutil.copy2(original, engine / "blender" / original.name)

            # GS_ROOT locates data, while ENGINE independently locates Blender scripts.
            gs_paths.ENGINE = str(engine)
            check(gs_paths.ROOT == str(args.application.resolve()), "GS_ROOT data location changed")
            check(gs_paths.blender_script("gr_render") == ["--python", str(wrapper)],
                  "Native engine did not prefer the new wrapper")
            wrapper.rename(wrapper.with_suffix(".disabled"))
            fallback = gs_paths.blender_script("gr_render")
            check(fallback[0] == "--python-expr" and "import gr_render" in fallback[1],
                  "Native compiled-renderer fallback changed")
            wrapper.with_suffix(".disabled").rename(wrapper)

            for speed, render_extra, expected_samples in (
                ("original", "", 64), ("fast", "samples=32", 32),
                ("roster", "samples=8", 8),
            ):
                for mode in ("loop", "freeze", "fallback", "preview", "still"):
                    extra = "res=256 samples=8" if mode == "preview" and render_extra else render_extra
                    os.environ["GORILLA_EXTRA"] = extra
                    command = capture_command(mode, root / (speed + "-" + mode))
                    check(command[3:5] == ["--python", str(wrapper)], "MAKE bypassed wrapper")
                    check("fps=30" in command, "Native animation rate changed")
                    if extra:
                        native_has_extra = command[-len(extra.split()):] == extra.split()
                        check(native_has_extra == (mode not in ("loop", "freeze")),
                              "Native environment forwarding regression was not reproduced")
                    # Blender exposes the argument list after its executable as sys.argv.
                    native_argv = command[1:]
                    env = os.environ.copy()
                    completed = subprocess.run(
                        [str(args.blender_python), str(Path(__file__).resolve()), "--blender-probe",
                         str(wrapper), json.dumps(native_argv)],
                        text=True, capture_output=True, env=env,
                    )
                    check(completed.returncode == 0, "Renderer probe failed: " + completed.stderr)
                    probe = json.loads(completed.stdout)
                    expected = (8 if extra else 16) if mode == "preview" else expected_samples
                    check(probe["samples"] == expected, "EEVEE samples ignored for " + speed + "/" + mode)
                    resolution = (256 if extra else 384) if mode == "preview" else 1024
                    check(probe["resolution"] == [resolution, resolution], "Resolution changed")
                    check(probe["engine"] == "BLENDER_EEVEE", "Rendering engine changed")
                    check(probe["options"]["fps"] == "30", "Animation rate changed in renderer")
                    check(probe["options"]["t1"] == "6.63333", "Animation duration changed")
                    if mode == "freeze":
                        check(probe["options"]["only"] == ",".join(map(str, range(1, 200))),
                              "Freeze animation frames changed")
                    elif mode == "still":
                        check(probe["options"]["only"] == "199", "Still frame changed")
                    elif mode in ("loop", "fallback"):
                        check("only" not in probe["options"], "Full motion was truncated")
                    if extra:
                        tail = extra.split()
                        check(probe["argv"][-len(tail):] == tail, "Override was not last")
                        expected_length = len(native_argv) if native_argv[-len(tail):] == tail else len(native_argv) + len(tail)
                        check(len(probe["argv"]) == expected_length, "Override was appended twice")
                    commands[speed + "/" + mode] = command[3:5]
                    probes.append({"speed": speed, "mode": mode, "samples": probe["samples"],
                                   "resolution": probe["resolution"], "fps": 30, "source_motion_frames": 199,
                                   "rendered_frames": len(probe["options"]["only"].split(","))
                                   if "only" in probe["options"] else 199})

            # Existing user overrides and spaces in paths keep their native token semantics.
            os.environ["GORILLA_EXTRA"] = "hide=jacket res=768 samples=8"
            command = capture_command("loop", root / "custom")
            completed = subprocess.run(
                [str(args.blender_python), str(Path(__file__).resolve()), "--blender-probe",
                 str(wrapper), json.dumps(command[1:])],
                text=True, capture_output=True, env=os.environ.copy(),
            )
            check(completed.returncode == 0, "Custom renderer probe failed: " + completed.stderr)
            custom = json.loads(completed.stdout)
            check(custom["options"]["hide"] == "jacket", "Existing user mesh option lost")
            check(custom["resolution"] == [768, 768], "Existing user resolution option lost")
            check(custom["samples"] == 8, "Custom sample option lost")
    finally:
        gs_paths.ENGINE = previous_engine
        if previous_extra is None:
            os.environ.pop("GORILLA_EXTRA", None)
        else:
            os.environ["GORILLA_EXTRA"] = previous_extra

    for path, digest in before.items():
        check(hashlib.sha256(Path(path).read_bytes()).hexdigest() == digest, "Original bytecode changed")
    report = {
        "checks_passed": checks, "native_pipeline_branches": 5, "speed_modes": 3,
        "normal_make_previously_ignored_GORILLA_EXTRA": True,
        "original_bytecode_unchanged": True, "actual_EEVEE_settings_executed": True,
        "blender_python": str(args.blender_python), "results": probes,
        "windows_blender_encoding_game_executed": False, "timings_measured": False,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--blender-probe":
        blender_probe(sys.argv[2], json.loads(sys.argv[3]))
    else:
        main()
