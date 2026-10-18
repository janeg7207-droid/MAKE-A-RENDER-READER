"""Validate the unmodified engine against a supplied Discovery dataset and an added plain-name character.

Run using CPython 3.14 with matching Linux NumPy/Pillow installed. The packaged
Windows .pyc modules are used directly; the Windows GUI is not executed here.
"""
from __future__ import annotations

import argparse
from contextlib import redirect_stdout
import hashlib
from io import StringIO
import json
import os
from pathlib import Path
import shutil
import sys


def digest(path: Path) -> str:
    with path.open("rb") as file:
        return hashlib.file_digest(file, "sha256").hexdigest()


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
    # Load Linux-native packages before the release path initializer locates its Windows DLLs.
    import numpy
    import PIL
    sys.path.append(str(args.engine_modules.resolve()))
    import gs_paths
    # In a frozen release ENGINE is beside gs_engine.exe, not the unpacked bytecode.
    gs_paths.ENGINE = str((args.application / "engine").resolve())
    import roster_lookup
    import gorilla_render

    characters = args.discovery / "Characters"
    table = args.discovery / "Roster" / "CharProfileTable.jsfb"
    originals = [table, *characters.rglob("*.mcd")]
    before = {str(p): digest(p) for p in originals}
    results = []
    roster = []
    for folder in sorted(characters.iterdir()):
        if not folder.is_dir():
            continue
        wid, name = folder.name.split("_", 1)
        info = roster_lookup.lookup(str(folder.resolve()))
        assert info["id"] == int(wid), info
        assert Path(info["table"]).samefile(table), info
        assert (folder / gorilla_render.find_attire(str(folder), None)).is_file(), folder
        roster.append({"id": info["id"], "name": info["name"]})
        results.append(info)
    assert len(results) == 10, f"Expected the uploaded dataset's 10 character folders; found {len(results)}"

    # Add a separate copy of the user's real Flammer mod; never rename the supplied folder.
    test_root = args.work / "Discovery Test"
    plain = test_root / "Characters" / "Flammer Experimental"
    shutil.copytree(characters / "1506_Flammer", plain)
    (test_root / "Roster").mkdir()
    shutil.copyfile(table, test_root / "Roster" / table.name)
    try:
        roster_lookup.lookup(str(plain.resolve()))
    except ValueError as error:
        assert "folder name must start" in str(error)
    else:
        raise AssertionError("The original engine unexpectedly accepted the unadapted plain folder")

    # Model the same visible directory layout as the Windows helper's NTFS junctions.
    # Linux symlinks exercise the original engine's path/roster contract, not Windows P/Invoke.
    cache = args.work / "compatibility-links"
    (cache / "Characters").mkdir(parents=True)
    (cache / "Roster").symlink_to(test_root / "Roster", target_is_directory=True)
    alias = cache / "Characters" / "1506_Flammer_Experimental"
    alias.symlink_to(plain, target_is_directory=True)
    added = roster_lookup.lookup(str(alias.absolute()))
    expected = next(info for info in results if info["id"] == 1506)
    assert added["id"] == 1506 and added["name"] == "Flammer Experimental", added
    assert added["female"] is True, added
    assert added["renders"] == expected["renders"], added
    assert Path(added["table"]).samefile(test_root / "Roster" / table.name), added
    assert (alias / gorilla_render.find_attire(str(alias), None)).is_file()
    assert plain.name == "Flammer Experimental" and plain.is_dir()
    assert digest(next(plain.rglob("*.mcd"))) == digest(next((characters / "1506_Flammer").rglob("*.mcd")))
    (args.work / "Discovery_roster.json").write_text(json.dumps(roster, indent=2) + "\n", encoding="utf-8")
    (args.work / "added_character_test_roster.json").write_text(json.dumps(roster + [
        {"id": 1506, "name": "Flammer Experimental"}], indent=2) + "\n", encoding="utf-8")

    # Actually start the original render pipeline; preserve its real outcome.
    library = json.loads((args.application / "data" / "library" / "taunts.json").read_text())
    taunt = library["taunts"][0]["key"]
    previous_argv = sys.argv
    render_output = StringIO()
    try:
        sys.argv = ["gorilla_render.py", str(alias), taunt, str(args.work / "render-output"), "quick=1"]
        try:
            with redirect_stdout(render_output):
                gorilla_render.main()
        except SystemExit as error:
            message = str(error)
            if "no BaseModel folder" in message:
                blocker = "missing_base_model"
            elif "WWE 2K26 was not found" in message and "oo2core_9_win64.dll" in message:
                blocker = "installed_game_files_required"
            else:
                raise
            render_status = {"status": "blocked", "blocker": blocker, "reason": message}
        else:
            render_status = {"status": "completed"}
    finally:
        sys.argv = previous_argv
    assert before == {str(p): digest(p) for p in originals}, "Original user files changed"
    report = {
        "original_characters_loaded": len(results),
        "original_character_attires_found": len(results),
        "characters_with_roster_picture_links": sum(bool(info["renders"]) for info in results),
        "characters_without_roster_picture_links": [info["folder"] for info in results if not info["renders"]],
        "actual_roster_table_read": str(table),
        "added_plain_name_character": added,
        "attire_model_found": True,
        "render_attempt": render_status,
        "render_log": render_output.getvalue(),
        "original_input_hashes_unchanged": True,
        "windows_gui_executed": False,
        "windows_junction_api_executed": False,
        "numpy_version": numpy.__version__,
        "characters": results,
    }
    (args.work / "validation.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k != "characters"}, indent=2))


if __name__ == "__main__":
    main()
