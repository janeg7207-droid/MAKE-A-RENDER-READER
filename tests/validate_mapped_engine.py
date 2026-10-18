"""Exercise the original v1.0.2 engine's physical-model/logical-slot split.

Requires CPython 3.14 and matching Linux NumPy/Pillow. Original packaged .pyc
modules execute unchanged. The test stops at the installed-game body lookup,
before extraction/rendering, and does not claim to test the Windows GUI.
"""
from __future__ import annotations

import argparse
from contextlib import redirect_stdout
import hashlib
from io import StringIO
import json
import os
from pathlib import Path
import sys


class BodyLookupReached(BaseException):
    """Stop before a Windows-only game extraction would run."""


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--engine-modules", type=Path, required=True)
    parser.add_argument("--application", type=Path, required=True)
    parser.add_argument("--discovery", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--native-packages", type=Path)
    args = parser.parse_args()
    args.work.mkdir(parents=True, exist_ok=False)
    os.environ["GS_ROOT"] = str(args.application.resolve())
    os.environ["GS_USER"] = str((args.work / "engine-user").resolve())
    if args.native_packages:
        sys.path.append(str(args.native_packages.resolve()))
    import numpy
    import PIL
    sys.path.append(str(args.engine_modules.resolve()))
    import gs_paths
    gs_paths.ENGINE = str((args.application / "engine").resolve())
    import extract_stock
    import gorilla_render
    import roster_lookup

    root = args.discovery.resolve()
    table = root / "Roster" / "CharProfileTable.jsfb"
    originals = [table, *(root / "Characters").rglob("*.mcd")]
    before = {str(path): digest(path) for path in originals}
    taunt = json.loads((args.application / "data/library/taunts.json").read_text())["taunts"][0]["key"]
    real_stock_folder_for = extract_stock.stock_folder_for
    real_renders_for = roster_lookup.renders_for
    real_is_female = roster_lookup.is_female
    real_argv = sys.argv
    reports = []
    try:
        # The first two pairs exist in the user's actual CharacterMapping table.
        # The third is an explicit user-selected cross-gender slot association.
        cases = [
            ("312_CM_Punk", 312, 1481, "148100_CM_PUNK_GM"),
            ("123_Rey_Mysterio_2019", 123, 910, None),
            ("312_CM_Punk", 312, 1506, None),
        ]
        for index, (physical_folder, physical_id, logical_id, expected_render) in enumerate(cases):
            model = root / "Characters" / physical_folder
            cache = args.work / f"case-{index}"
            (cache / "Characters").mkdir(parents=True)
            (cache / "Roster").symlink_to(root / "Roster", target_is_directory=True)
            alias = cache / "Characters" / f"{physical_id}_Chosen_Wrestler"
            alias.symlink_to(model, target_is_directory=True)
            expected_table, expected_renders = real_renders_for(logical_id, str(cache))
            assert Path(expected_table).samefile(table)
            expected_female = real_is_female(logical_id, expected_table)
            if expected_render:
                assert [item["name"] for item in expected_renders] == [expected_render]
            expected_attire = gorilla_render.find_attire(str(model), None)
            actual_attire = gorilla_render.find_attire(str(alias), None)
            assert (alias / actual_attire).samefile(model / expected_attire)

            observed = {"render_lookup_ids": [], "gender_lookup_ids": []}

            def renders_for(wid, *positional, **keywords):
                observed["render_lookup_ids"].append(wid)
                return real_renders_for(wid, *positional, **keywords)

            def is_female(wid, *positional, **keywords):
                observed["gender_lookup_ids"].append(wid)
                return real_is_female(wid, *positional, **keywords)

            def stock_folder_for(wid):
                # Called by original gorilla_render.main after it applies slot=.
                caller = sys._getframe(1)
                assert caller.f_code is gorilla_render.main.__code__
                info = caller.f_locals["info"]
                observed["engine_info"] = json.loads(json.dumps(info))
                observed["body_lookup_id"] = wid
                observed["stock_folder"] = real_stock_folder_for(wid)
                raise BodyLookupReached()

            roster_lookup.renders_for = renders_for
            roster_lookup.is_female = is_female
            extract_stock.stock_folder_for = stock_folder_for
            log = StringIO()
            sys.argv = ["gorilla_render.py", str(alias), taunt, str(cache / "output"), "prepare=model", f"slot={logical_id}"]
            try:
                with redirect_stdout(log):
                    gorilla_render.main()
            except BodyLookupReached:
                pass
            else:
                raise AssertionError("Original engine did not reach the expected body lookup")
            info = observed["engine_info"]
            assert info["folder_id"] == physical_id
            assert info["id"] == logical_id
            assert info["renders"] == expected_renders
            assert info["female"] is expected_female
            assert Path(info["table"]).samefile(table)
            assert observed["body_lookup_id"] == physical_id
            assert observed["stock_folder"] == physical_folder
            assert observed["render_lookup_ids"] == [physical_id, logical_id]
            assert observed["gender_lookup_ids"] == [physical_id, logical_id]
            assert f"slot chosen by the user: {logical_id} (folder {physical_id})" in log.getvalue()
            reports.append({
                "physical_model_id": physical_id,
                "logical_roster_id": logical_id,
                "body_lookup_id": observed["body_lookup_id"],
                "stock_folder": observed["stock_folder"],
                "attire_file": actual_attire,
                "attire_sha256": digest(alias / actual_attire),
                "renders": [item["name"] for item in info["renders"]],
                "female": info["female"],
                "source": "actual CharacterMapping" if index < 2 else "explicit cross-gender slot choice",
                "original_engine_reached_body_lookup": True,
            })
    finally:
        sys.argv = real_argv
        extract_stock.stock_folder_for = real_stock_folder_for
        roster_lookup.renders_for = real_renders_for
        roster_lookup.is_female = real_is_female
    assert before == {str(path): digest(path) for path in originals}, "Original user files changed"
    report = {
        "cases": reports,
        "original_input_hashes_unchanged": True,
        "original_engine_bytecode_executed": True,
        "installed_game_extraction_executed": False,
        "windows_gui_executed": False,
        "render_completed": False,
        "numpy_version": numpy.__version__,
    }
    (args.work / "mapped-engine-validation.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
