"""Exercise attire selection and cache identity in the unmodified native engine.

AlternateAttireTests exports requests from the production C# option builder.
The real uploaded default MCD is copied as an added alternate to verify routing.
Native main is traced through path selection and cache naming, then stopped
before model import. This does not execute Blender, Windows GUI or WWE.
"""
import argparse
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("engine-modules", "application", "discovery", "fixtures", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    os.environ["GS_ROOT"] = str(args.application.resolve())
    import numpy  # Load Linux packages before exposing packaged Windows modules.
    from PIL import Image
    sys.path.append(str(args.engine_modules.resolve()))
    import gorilla_render

    manifest = json.loads((args.fixtures / "requests.json").read_text())
    source = Path(manifest["source_model"])
    source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
    checks = 0

    def check(value):
        nonlocal checks
        assert value
        checks += 1

    actual_defaults = {}
    for folder in sorted((args.discovery / "Characters").iterdir()):
        if not folder.is_dir():
            continue
        try:
            default = gorilla_render.find_attire(str(folder))
        except ValueError:
            continue
        check(Path(folder, default).is_file())
        check(gorilla_render.find_attire(str(folder), default) == default)
        actual_defaults[folder.name] = default
    check(len(actual_defaults) == 10)

    class SelectedBeforeImport(Exception):
        pass

    records = []
    saved_argv = sys.argv
    for index, request in enumerate(manifest["requests"], 1):
        captured = {}
        print("Checking native request %d/%d: %s, %s" % (index, len(manifest["requests"]),
              request["option"], request["selected"] or "automatic"), flush=True)

        def trace(frame, event, value):
            if frame.f_code is not gorilla_render.main.__code__:
                return None
            # At this point both attire path and the native model/taunt work
            # directories are computed. No model/texture/Blender work has run.
            if event == "line" and frame.f_lineno == 315:
                captured.update({name: frame.f_locals[name] for name in ("attire", "sig", "wwork", "work", "char_dir", "info")})
                raise SelectedBeforeImport()
            return trace

        sys.argv = ["gorilla_render"] + request["arguments"]
        sys.settrace(trace)
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                gorilla_render.main()
        except SelectedBeforeImport:
            pass
        finally:
            sys.settrace(None)
            sys.argv = saved_argv
        check(bool(captured))
        expected = request["selected"] or "312_Default_Attire/312_Attire.mcd"
        check(captured["attire"] == expected)
        check(captured["char_dir"] == manifest["character"])
        check(captured["info"]["folder_id"] == 312)
        check(captured["info"]["id"] == 1481)
        check(Path(captured["wwork"]).parent == args.fixtures.resolve() / "output/work")
        check(Path(captured["work"]).name == "roman3")
        records.append({"request": request["option"], "selection": request["selected"], "attire": captured["attire"],
                        "model_work": captured["wwork"], "taunt_work": captured["work"]})
    for option in ("prepare=model", "prepare=taunt", "quick=1", "end=loop"):
        selected = [record for record in records if record["request"] == option]
        check(selected[0]["model_work"] == selected[1]["model_work"])
        check(selected[0]["model_work"] != selected[2]["model_work"])
    alternate_dirs = {record["model_work"] for record in records if record["selection"] == "Added Alternate/Attire.mcd"}
    check(len(alternate_dirs) == 1)
    check(hashlib.sha256(source.read_bytes()).hexdigest() == source_hash)
    check(not (args.fixtures / "output").exists())
    report = {"checks_passed": checks, "native_requests_routed": len(records),
              "actual_uploaded_characters": len(actual_defaults), "native_default_attires": actual_defaults,
              "selected_relative_attire_honored": True, "original_character_root_preserved": True,
              "physical_312_logical_1481_preserved": True, "native_blend_cache_separates_attires": True,
              "all_request_types_share_the_correct_attire_cache": True,
              "added_alternate_is_a_copy_of_uploaded_default_for_routing_test": True,
              "body_is_a_structural_placeholder_not_imported": True,
              "source_model_unchanged": True, "windows_blender_game_executed": False,
              "native_model_import_executed": False, "requests": records}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({key: value for key, value in report.items() if key not in ("requests", "native_default_attires")}, indent=2))


if __name__ == "__main__":
    main()
