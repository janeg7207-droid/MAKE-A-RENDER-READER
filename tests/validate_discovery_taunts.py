"""Run the original engine's taunt retargeter against an imported Discovery clip.

Requires the same CPython version as the packaged engine and NumPy. This runs
unmodified Python bytecode; it does not start the Windows GUI, Blender, or a game.
The overlay must already have been built by DiscoveryTauntLibrary.Build.
"""
from __future__ import annotations

import argparse
import contextlib
import hashlib
import importlib.util
import io
import json
import marshal
import os
from pathlib import Path
import re
import sys
import tempfile
import types


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def load_original(name: str, modules: Path, source_engine: Path):
    path = modules / (name + ".pyc")
    raw = path.read_bytes()
    if raw[:4] != importlib.util.MAGIC_NUMBER:
        raise RuntimeError("Run this validator with the original engine's CPython version.")
    module = types.ModuleType(name)
    # Released scripts use __file__ only to locate adjacent packaged resources.
    # Give the unchanged bytecode the same engine location as the real release.
    module.__file__ = str(source_engine / (name + ".py"))
    sys.modules[name] = module
    exec(marshal.loads(raw[16:]), module.__dict__)
    return module


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine-modules", type=Path, required=True)
    parser.add_argument("--overlay", type=Path, required=True)
    parser.add_argument("--source-root", type=Path,
                        help="Original GorillaStudio folder; defaults to the overlay's engine target parent.")
    parser.add_argument("--clip", default="62541a000")
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    import numpy as np

    modules = args.engine_modules.resolve()
    overlay = args.overlay.resolve()
    source_root = (args.source_root.resolve() if args.source_root
                   else (overlay / "engine").resolve().parent)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    output = args.report.with_suffix(".capture.npz")
    if output.exists():
        raise FileExistsError("Choose a new report path so an existing capture is not overwritten: " + str(output))
    checks = []

    def check(label, condition):
        if not condition:
            raise AssertionError(label)
        checks.append(label)

    with tempfile.TemporaryDirectory(prefix="gorilla-taunt-validator-") as temp:
        user = Path(temp) / "user"
        user.mkdir()
        os.environ["GS_ROOT"] = str(overlay)
        os.environ["GS_USER"] = str(user)
        for key in ("GORILLA_STOCK", "GORILLA_ANIM", "REST_NPZ", "GR_AUDIT"):
            os.environ.pop(key, None)
        source_engine = source_root / "engine"
        paths = load_original("gs_paths", modules, source_engine)
        load_original("file_audit", modules, source_engine)

        check("The original GS_ROOT hook selects the overlay", Path(paths.ROOT) == overlay)
        check("The original library path uses the imported manifest", Path(paths.LIB) == overlay / "data/library")
        check("The original decoded animation path uses the overlay pack", Path(paths.PACK) == overlay / "data/gamepack")
        check("The original engine resource path remains the full packaged engine", Path(paths.ENGINE) == source_engine)
        check("The original helper cache uses isolated disposable user storage", Path(paths.USER) == user)
        check("The imported taunt manifest exists", (Path(paths.LIB) / "taunts.json").is_file())
        manifest = json.loads((Path(paths.LIB) / "taunts.json").read_text(encoding="utf-8"))
        records = [entry for entry in manifest["taunts"] if entry.get("clip") == args.clip]
        check("The imported manifest contains the selected actual animation", len(records) == 1)
        decoded_path = Path(paths.PACK) / "decoded" / (args.clip + ".npz")
        check("The original decoded_clip lookup has a real imported file", decoded_path.is_file())
        check("The original rest skeleton exists", Path(paths.ROMAN_REST).is_file())
        check("The original helper captures remain available", bool(paths.CAPS) and all(Path(path).is_file() for path in paths.CAPS))
        check("The default Orton capture remains available", Path(paths.ANY_CAP).is_file())
        check("The overlay engine resource directory resolves to original engine", (overlay / "engine").samefile(source_engine))
        check("The overlay plugin resources remain available", (overlay / "plugin").is_dir())
        check("The renderer's original Blender bytecode remains available", (source_engine / "blender/gr_render.pyc").is_file())
        check("The animation library has its writable preview directory", (Path(paths.LIB) / "previews_game").is_dir())
        source_inputs = ([modules / (name + ".pyc") for name in ("gs_paths", "file_audit", "make_taunt_capture")]
                         + [decoded_path, Path(paths.ROMAN_REST), Path(paths.ANY_CAP)]
                         + [Path(path) for path in paths.CAPS])
        before = {str(path): digest(path) for path in source_inputs}
        stdout = io.StringIO()
        with contextlib.redirect_stdout(stdout):
            capture = load_original("make_taunt_capture", modules, source_engine)
            ids, tracks, scale_ids, scales, header = paths.decoded_clip(args.clip)
            in_clip = [name for name in capture.RN if paths.fnv1a64_raw(name) in ids]
            helpers = [name for name in capture.RN if name not in in_clip
                       and (name.startswith("H_") or name.startswith("J_Collar_RBF"))]
            original_argv = sys.argv
            sys.argv = ["make_taunt_capture.py", args.clip, str(output)]
            try:
                capture.main()
            finally:
                sys.argv = original_argv
        check("The original retargeter wrote an actual capture", output.is_file())
        check("The original retargeter reported successful completion", "WROTE " in stdout.getvalue())
        helper_model = re.search(r"helper model: (\d+) helpers", stdout.getvalue())
        trained_helpers = int(helper_model.group(1)) if helper_model else None
        with np.load(output, allow_pickle=False) as result:
            a, b, times, names = (result[key] for key in ("A", "B", "t", "names"))
            frames, bones = tracks.shape[1], len(capture.RN)
            check("Every decoded frame retargets to every rest-skeleton bone", list(a.shape) == [frames, bones, 4, 4])
            check("Both capture matrix streams have the same shape", b.shape == a.shape)
            check("All generated pose matrices are finite", bool(np.isfinite(a).all() and np.isfinite(b).all()))
            check("Generated poses animate across the selected motion", bool(np.max(np.abs(a[1:] - a[0])) > 0.001))
            check("Generated capture bone names match the actual rest skeleton", list(map(str, names)) == capture.RN)
            check("Generated timestamps preserve the actual source frame rate", bool(np.allclose(times, np.arange(frames) / header["fps"])))
            check("The original retargeter retains every helper bone", all(name in names for name in helpers))
            if args.clip == "62541a000":
                check("Actual Undisputed 2 contains 281 frames", frames == 281)
                check("Actual Undisputed 2 retargets to 175 skeleton bones", bones == 175)
                check("Actual Undisputed 2 retains 82 runtime helper slots including RBF collars", len(helpers) == 82)
                check("Actual Undisputed 2 includes 74 H_ helper bones", sum(name.startswith("H_") for name in helpers) == 74)
                check("The native helper model reconstructs 74 trained helper bones", trained_helpers == 74)
                check("Actual Undisputed 2 runs at 60 frames per second", header["fps"] == 60)
            matrix_shape = list(a.shape)
        check("The engine bytecode, decoded input, skeleton and helper captures remain unchanged",
              before == {path: digest(Path(path)) for path in before})
        resources = {name: str(getattr(paths, name)) for name in
                     ("ROOT", "ENGINE", "DATA", "LIB", "PACK", "ROMAN_REST", "ANY_CAP")}
        resources["helper_capture_count"] = len(paths.CAPS)
        resources["caps_root"] = str(overlay / "data/caps")
        resources["start_root"] = str(overlay / "data/start")
        report = {
            "clip": args.clip, "name": records[0]["name"],
            "checks_passed": len(checks), "checks": checks,
            "original_engine_bytecode_executed": True,
            "retargeted_frames": frames, "fps": header["fps"],
            "skeleton_bones": bones, "runtime_helper_bones": len(helpers),
            "h_helper_bones": sum(name.startswith("H_") for name in helpers),
            "trained_helper_model_bones": trained_helpers,
            "pose_matrix_shape": matrix_shape, "finite_pose_matrices": True,
            "poses_animate": True, "timestamps_match_source_fps": True,
            "generated_decoded_npz_sha256": digest(decoded_path),
            "retargeted_capture_sha256": digest(output),
            "original_make_taunt_capture_pyc_sha256": digest(modules / "make_taunt_capture.pyc"),
            "original_inputs_unchanged": True, "resources": resources,
            "engine_output": stdout.getvalue().splitlines(),
            "windows_gui_executed": False, "blender_render_completed": False,
        }
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
