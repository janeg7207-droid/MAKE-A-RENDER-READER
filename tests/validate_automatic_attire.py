"""Exercise the original engine's automatic attire selection without rendering.

Run with the same CPython version used to package gs_engine. The unmodified
find_attire function is loaded from its original bytecode; no Windows application
or Blender process is started. Fixture links model the helper's compatibility
layout for a source whose MCD files sit directly in the selected directory.
"""
from __future__ import annotations

import argparse
import builtins
import hashlib
import importlib.util
import json
import marshal
import os
from pathlib import Path
import tempfile
import types


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def original_function(module: Path):
    raw = module.read_bytes()
    if raw[:4] != importlib.util.MAGIC_NUMBER:
        raise RuntimeError("Run this test with the original engine's CPython version.")
    code = marshal.loads(raw[16:])
    functions = [item for item in code.co_consts
                 if isinstance(item, types.CodeType) and item.co_name == "find_attire"]
    if len(functions) != 1:
        raise RuntimeError("The original engine has no unique find_attire function.")
    return types.FunctionType(functions[0], {"os": os, "__builtins__": builtins.__dict__})


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--engine-modules", type=Path, required=True)
    parser.add_argument("--discovery", type=Path, required=True)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    module = args.engine_modules / "gorilla_render.pyc"
    find_attire = original_function(module)
    discovery = args.discovery.resolve()
    original_models = sorted((discovery / "Characters").rglob("*.mcd"))
    if not original_models:
        raise AssertionError("The supplied Discovery Characters folder has no MCD models.")
    before = {str(path): digest(path) for path in original_models}
    before[str(module)] = digest(module)
    checks = []

    def equal(name: str, expected, actual) -> None:
        if expected != actual:
            raise AssertionError(f"{name}: expected {expected!r}, got {actual!r}")
        checks.append(name)

    with tempfile.TemporaryDirectory(prefix="gorilla-attire-") as temp:
        root = Path(temp)

        def model(character: str, child: str, filename: str = "Attire.mcd") -> Path:
            path = root / character / child / filename
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"disposable attire-selection fixture")
            return path

        model("PAC", "AA_Alternate")
        model("PAC", "Default_Attire")
        model("PAC", "BaseModel", "baseModel.mcd")
        equal("Whole character automatically chooses default over an earlier alternate",
              "Default_Attire/Attire.mcd", find_attire(str(root / "PAC"), None))
        equal("The native function does not require a numbered character name",
              "Default_Attire/Attire.mcd", find_attire(str(root / "PAC"), None))

        model("215_PAC", "215_Attire_2", "215_Attire.mcd")
        model("215_PAC", "215_Default_Attire", "215_Attire.mcd")
        equal("Numbered default attire also wins automatically",
              "215_Default_Attire/215_Attire.mcd", find_attire(str(root / "215_PAC"), None))
        equal("Explicit alternate selection is supported without modifying source data",
              "215_Attire_2/215_Attire.mcd",
              find_attire(str(root / "215_PAC"), "215_Attire_2/215_Attire.mcd"))

        model("Single", "Entrance_Gear", "gear.mcd")
        equal("A single available attire needs no manual choice",
              "Entrance_Gear/gear.mcd", find_attire(str(root / "Single"), None))

        model("Filename", "Default_Attire", "AAA_unused.mcd")
        model("Filename", "Default_Attire", "Attire.mcd")
        equal("The attire-named MCD wins over an earlier unrelated model file",
              "Default_Attire/Attire.mcd", find_attire(str(root / "Filename"), None))

        model("Uppercase", "BASEMODEL", "body.mcd")
        model("Uppercase", "DEFAULT_ATTIRE", "ATTIRE.MCD")
        equal("Case-insensitive body exclusion and MCD extension work",
              "DEFAULT_ATTIRE/ATTIRE.MCD", find_attire(str(root / "Uppercase"), None))

        # The native picker cannot use an MCD directly in its selected root. The
        # helper must expose it as a child attire, without copying user assets.
        direct = model("Direct", "", "Attire.mcd")
        try:
            find_attire(str(direct.parent), None)
        except ValueError as error:
            equal("A direct-MCD source needs the compatibility layout", True,
                  "no attire model" in str(error))
        else:
            raise AssertionError("The native direct-MCD limitation changed; update the compatibility test.")

        alias = root / "Compatibility" / "Characters" / "215_PAC"
        alias.mkdir(parents=True)
        (alias / "Default_Attire").symlink_to(direct.parent, target_is_directory=True)
        body = model("BodySource", "BaseModel", "baseModel.mcd")
        (alias / "BaseModel").symlink_to(body.parent, target_is_directory=True)
        relative = find_attire(str(alias), None)
        equal("The compatibility layout automatically selects a direct-MCD source",
              "Default_Attire/Attire.mcd", relative)
        equal("The compatibility attire resolves to the original file", True,
              (alias / relative).samefile(direct))
        equal("The compatibility body resolves to the explicitly supplied body", True,
              (alias / "BaseModel" / "baseModel.mcd").samefile(body))
        nested = direct.parent / "Costumes" / "Red" / "Attire.mcd"
        nested.parent.mkdir(parents=True)
        nested.write_bytes(b"disposable nested attire-selection fixture")
        requested = "Default_Attire/Costumes/Red/Attire.mcd"
        equal("The native engine honors an explicit nested attire through the wrapper",
              requested, find_attire(str(alias), requested))
        equal("The explicit nested wrapper attire resolves to its original source", True,
              (alias / requested).samefile(nested))

    actual = []
    characters = discovery / "Characters"
    for character in sorted(path for path in characters.iterdir() if path.is_dir()):
        candidates = [path for child in character.iterdir()
                      if child.is_dir() and child.name.lower() != "basemodel"
                      for path in child.iterdir()
                      if path.is_file() and path.suffix.lower() == ".mcd"]
        if not candidates:
            continue
        relative = find_attire(str(character), None)
        chosen = character / relative
        equal(f"Actual {character.name} automatically resolves to an existing attire", True,
              chosen.is_file() and any(chosen.samefile(candidate) for candidate in candidates))
        defaults = [path for path in candidates if "default" in path.parent.name.lower()]
        if defaults:
            equal(f"Actual {character.name} uses its available default attire", True,
                  any(chosen.samefile(default) for default in defaults))
        actual.append({"character": character.name, "attire": relative,
                       "sha256": digest(chosen)})

    equal("Every actual uploaded character model was exercised", len(original_models),
          len({str((characters / item["character"] / item["attire"]).resolve()) for item in actual}))
    equal("Original models and engine bytecode remain unchanged", before,
          {path: digest(Path(path)) for path in before})
    report = {"checks_passed": len(checks), "checks": checks,
              "actual_characters": actual,
              "original_find_attire_bytecode_executed": True,
              "original_engine_sha256": digest(module),
              "original_files_unchanged": True,
              "windows_gui_executed": False,
              "render_completed": False}
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
